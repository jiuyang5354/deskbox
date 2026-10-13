using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using NSubstitute;

namespace DeskBox.Domain.Tests;

/// <summary>P1-2 / P1-3 / P2-3（TDD v1.0.5）：恢复流程本身被打断时仍然幂等。</summary>
public class RecoveryTransactionTests
{
    private const string IdA = "1a2b3c4d:000000000000000000000000000000aa";
    private const string IdB = "1a2b3c4d:000000000000000000000000000000bb";
    private const string IdC = "1a2b3c4d:000000000000000000000000000000cc";
    private const string IdD = "1a2b3c4d:000000000000000000000000000000dd";
    private const string IdOther = "1a2b3c4d:0000000000000000000000000000ffff";

    private sealed class World
    {
        public FakeFs Fs { get; } = new();
        public InMemoryJournal Journal { get; } = new();
        public InMemoryNotices Notices { get; } = new();
        public InMemoryVaultRecords Records { get; } = new();
        public FakeDb Db { get; } = new();
        public IRecoveryLog Log { get; } = Substitute.For<IRecoveryLog>();
        public JournalRecovery Recovery() => new(Db, Journal, Fs, Fs, Records, Notices, Log, TimeProvider.System);

        public JournalEntry Seed(string fileId, string src, string dst)
        {
            var e = new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), fileId, src, dst, default);
            Db.InTransactionAsync(u => Journal.AppendPendingAsync(u, e)).GetAwaiter().GetResult();
            return e;
        }
    }

    // A：文件已在候选路径 → 补提交；B：在收纳库别处 → 扫描补提交；C：哪里都没有 → Lost；D：源文件还在 → 回滚
    private static (World w, JournalEntry a, JournalEntry b, JournalEntry c, JournalEntry d) Scenario()
    {
        var w = new World();
        var a = w.Seed(IdA, @"C:\desk\a.txt", @"C:\vault\a.txt");
        w.Fs.Files[@"C:\vault\a.txt"] = IdA;
        var b = w.Seed(IdB, @"C:\desk\b.txt", @"C:\vault\b.txt");
        w.Fs.Files[@"C:\vault\b.txt"] = IdOther;
        w.Fs.Files[@"C:\vault\sub\b (4).txt"] = IdB;
        var c = w.Seed(IdC, @"C:\desk\c.txt", @"C:\vault\c.txt");
        var d = w.Seed(IdD, @"C:\desk\d.txt", @"C:\vault\d.txt");
        w.Fs.Files[@"C:\desk\d.txt"] = IdD;
        w.Db.Restart();
        return (w, a, b, c, d);
    }

    private static void AssertFinal(World w, JournalEntry a, JournalEntry b, JournalEntry c, JournalEntry d)
    {
        (a.Status, a.ActualTargetPath).Should().Be((JournalStatus.Committed, @"C:\vault\a.txt"));
        (b.Status, b.ActualTargetPath).Should().Be((JournalStatus.Committed, @"C:\vault\sub\b (4).txt"));
        c.Status.Should().Be(JournalStatus.Lost);
        d.Status.Should().Be(JournalStatus.RolledBack);
        w.Records.All.Select(r => (r.VaultPath, r.FileId)).Should().BeEquivalentTo(new[]
        {
            (@"C:\vault\a.txt", IdA.ToUpperInvariant()),
            (@"C:\vault\sub\b (4).txt", IdB.ToUpperInvariant()),
        });
        w.Notices.All.Should().ContainSingle().Which.JournalEntryId.Should().Be(c.Id);
        // 业务表与 Journal 状态一致：每条 Committed 恰好一条 vault_record，每条 Lost 恰好一条提示
        foreach (var e in new[] { a, b, c, d })
        {
            w.Records.All.Count(r => r.VaultPath == e.ActualTargetPath).Should().Be(e.Status == JournalStatus.Committed ? 1 : 0);
            w.Notices.All.Count(n => n.JournalEntryId == e.Id).Should().Be(e.Status == JournalStatus.Lost ? 1 : 0);
        }
    }

    [Fact]
    public async Task Without_crash_reaches_expected_state_with_one_transaction_per_entry()
    {
        var (w, a, b, c, d) = Scenario();
        var r = await w.Recovery().RecoverAsync();
        r.Select(x => x.Outcome).Should().Equal(RecoveryOutcome.CommittedAtCandidate, RecoveryOutcome.CommittedFromVaultScan,
            RecoveryOutcome.Lost, RecoveryOutcome.Aborted);
        AssertFinal(w, a, b, c, d);
        w.Db.Commits.Should().Be(5); // 读 Pending 1 次 + 每条 1 个事务
        w.Log.DidNotReceiveWithAnyArgs().RecoveryFailed(default!);
    }

    [Fact]
    public async Task Crash_between_every_step_then_restart_is_idempotent()
    {
        var crashes = 0;
        for (var step = 1; ; step++)
        {
            var (w, a, b, c, d) = Scenario();
            w.Db.CrashAtStep = step;
            try { await w.Recovery().RecoverAsync(); }
            catch (KilledException) { /* 进程在读取 Pending 时被杀 */ }
            var crashed = w.Db.Dead;

            w.Db.Restart(); // 重启后再次恢复
            await FluentActions.Awaiting(() => w.Recovery().RecoverAsync()).Should().NotThrowAsync($"crash at step {step}");
            AssertFinal(w, a, b, c, d);
            foreach (var e in new[] { a, b, c, d }) w.Journal.Failures(e.Id).Should().Be(0, "被杀导致的失败连计数都写不进去");
            (await w.Recovery().RecoverAsync()).Should().BeEmpty("第三次启动没有剩余 Pending");

            if (!crashed) break;
            crashes++;
        }
        crashes.Should().BeGreaterThan(15, "每个 Begin / 写入 / Commit 之间都注入过一次崩溃");
    }

    [Fact]
    public async Task Crash_between_vault_record_and_commit_does_not_duplicate_on_restart()
    {
        // P1-2 原始场景：旧实现会在重启时因 vault_path UNIQUE 冲突卡死
        var w = new World();
        var a = w.Seed(IdA, @"C:\desk\a.txt", @"C:\vault\a.txt");
        w.Fs.Files[@"C:\vault\a.txt"] = IdA;
        w.Db.Restart();
        w.Db.CrashAtStep = 7; // 1 Begin 2 读 Pending 3 Commit | 4 Begin 5 vault_record 6 journal Commit 7 事务提交时被杀
        await w.Recovery().RecoverAsync();
        w.Records.All.Should().BeEmpty("事务未提交，vault_record 不落盘");
        a.Status.Should().Be(JournalStatus.Pending);
        w.Db.Restart();
        (await w.Recovery().RecoverAsync()).Single().Outcome.Should().Be(RecoveryOutcome.CommittedAtCandidate);
        w.Records.All.Should().ContainSingle();
    }

    [Fact]
    public async Task Crash_between_notice_and_mark_lost_keeps_entry_pending_and_notice_absent()
    {
        // P1-3 原始场景：不会出现"已不是 Pending 却没有提示"的状态
        var w = new World();
        var c = w.Seed(IdC, @"C:\desk\c.txt", @"C:\vault\c.txt");
        w.Db.Restart();
        w.Db.CrashAtStep = 7; // 4 Begin 5 提示 6 MarkLost 7 事务提交时被杀
        await w.Recovery().RecoverAsync();
        (c.Status, w.Notices.All.Count).Should().Be((JournalStatus.Pending, 0));
        w.Db.Restart();
        (await w.Recovery().RecoverAsync()).Single().Outcome.Should().Be(RecoveryOutcome.Lost);
        (c.Status, w.Notices.All.Count).Should().Be((JournalStatus.Lost, 1));
    }

    [Fact]
    public async Task One_failing_entry_does_not_block_the_others()
    {
        var w = new World();
        var bad = w.Seed(IdA, @"E:\desk\a.txt", @"C:\vault\a.txt");
        w.Fs.Broken.Add(@"E:\desk\a.txt");
        var good = w.Seed(IdB, @"C:\desk\b.txt", @"C:\vault\b.txt");
        w.Fs.Files[@"C:\vault\b.txt"] = IdB;

        var r = await w.Recovery().RecoverAsync();

        r.Should().Equal(new RecoveredEntry(bad.Id, RecoveryOutcome.Failed, null),
            new RecoveredEntry(good.Id, RecoveryOutcome.CommittedAtCandidate, @"C:\vault\b.txt"));
        (bad.Status, bad.RecoveryFailures).Should().Be((JournalStatus.Pending, 1));
        good.Status.Should().Be(JournalStatus.Committed);
        w.Log.Received(1).RecoveryFailed(new RecoveryFailedEvent(bad.Id, bad.JobId, IdA.ToUpperInvariant(), @"E:\desk\a.txt",
            @"C:\vault\a.txt", 1, false, nameof(IOException), "设备未就绪"));
        w.Notices.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Vault_scan_failure_is_isolated_too()
    {
        var w = new World();
        var vault = Substitute.For<IVaultIndex>();
        vault.EnumerateFiles().Returns(_ => throw new UnauthorizedAccessException("denied"));
        var e = w.Seed(IdA, @"C:\desk\a.txt", @"C:\vault\a.txt");
        var sut = new JournalRecovery(w.Db, w.Journal, w.Fs, vault, w.Records, w.Notices, w.Log, TimeProvider.System);
        (await sut.RecoverAsync()).Single().Outcome.Should().Be(RecoveryOutcome.Failed);
        e.Status.Should().Be(JournalStatus.Pending);
        w.Log.Received(1).RecoveryFailed(Arg.Is<RecoveryFailedEvent>(x => x.ExceptionType == nameof(UnauthorizedAccessException)));
    }

    [Fact]
    public async Task Third_consecutive_startup_failure_marks_lost_with_notice()
    {
        var w = new World();
        var e = w.Seed(IdA, @"E:\desk\a.txt", @"C:\vault\a.txt");
        w.Fs.Broken.Add(@"E:\desk\a.txt");

        for (var run = 1; run <= 2; run++)
        {
            (await w.Recovery().RecoverAsync()).Single().Outcome.Should().Be(RecoveryOutcome.Failed);
            (e.Status, e.RecoveryFailures).Should().Be((JournalStatus.Pending, run));
            w.Notices.All.Should().BeEmpty();
        }

        (await w.Recovery().RecoverAsync()).Single().Should().Be(new RecoveredEntry(e.Id, RecoveryOutcome.Lost, null));
        e.Status.Should().Be(JournalStatus.Lost);
        w.Notices.All.Should().ContainSingle().Which.FileName.Should().Be("a.txt");
        w.Log.Received(1).RecoveryFailed(Arg.Is<RecoveryFailedEvent>(x => x.ConsecutiveFailures == 3 && x.MarkedLost));
        (await w.Recovery().RecoverAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Crash_while_recording_third_strike_is_all_or_nothing()
    {
        var w = new World();
        var e = w.Seed(IdA, @"E:\desk\a.txt", @"C:\vault\a.txt");
        w.Fs.Broken.Add(@"E:\desk\a.txt");
        await w.Recovery().RecoverAsync();
        await w.Recovery().RecoverAsync();
        w.Db.Restart();
        w.Db.CrashAtStep = 8; // 4 Begin 5 失败计数 6 提示 7 MarkLost 8 事务提交时被杀
        await w.Recovery().RecoverAsync();
        (e.Status, e.RecoveryFailures, w.Notices.All.Count).Should().Be((JournalStatus.Pending, 2, 0));
        w.Db.Restart();
        (await w.Recovery().RecoverAsync()).Single().Outcome.Should().Be(RecoveryOutcome.Lost);
        (e.RecoveryFailures, w.Notices.All.Count).Should().Be((3, 1));
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var w = new World();
        w.Seed(IdA, @"C:\desk\a.txt", @"C:\vault\a.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await FluentActions.Awaiting(() => w.Recovery().RecoverAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Work_failure_rolls_back_the_unit_of_work()
    {
        var db = new FakeDb();
        var records = new InMemoryVaultRecords();
        var rec = new VaultRecord(Guid.NewGuid(), @"C:\desk\a", @"C:\vault\a", IdA, default);
        await FluentActions.Awaiting(() => db.InTransactionAsync(async u =>
        {
            await records.AddAsync(u, rec);
            throw new IOException("boom");
        })).Should().ThrowAsync<IOException>();
        records.All.Should().BeEmpty();
        (db.Commits, db.Rollbacks).Should().Be((0, 1));
        (await db.InTransactionAsync(u => Task.FromResult(42))).Should().Be(42);
    }

    [Fact]
    public async Task Extension_guards()
    {
        IUnitOfWorkFactory? none = null;
        var db = new FakeDb();
        await FluentActions.Awaiting(() => none!.InTransactionAsync(_ => Task.CompletedTask)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => db.InTransactionAsync((Func<IUnitOfWork, Task>)null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => none!.InTransactionAsync(_ => Task.FromResult(1))).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => db.InTransactionAsync((Func<IUnitOfWork, Task<int>>)null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Failure_count_write_error_is_logged_as_secondary_not_swallowed()
    {
        var w = new World();
        var e = new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), IdA, @"E:\desk\a.txt", @"C:\vault\a.txt", default);
        w.Fs.Broken.Add(@"E:\desk\a.txt");
        var journal = Substitute.For<IJournal>();
        journal.GetPendingAsync(default!).ReturnsForAnyArgs(new[] { e });
        journal.RecordRecoveryFailureAsync(default!, default).ReturnsForAnyArgs<Task<int>>(_ => throw new IOException("磁盘已满"));
        var sut = new JournalRecovery(w.Db, journal, w.Fs, w.Fs, w.Records, w.Notices, w.Log, TimeProvider.System);

        (await sut.RecoverAsync()).Single().Should().Be(new RecoveredEntry(e.Id, RecoveryOutcome.Failed, null));

        w.Log.Received(1).RecoveryFailed(new RecoveryFailedEvent(e.Id, e.JobId, IdA.ToUpperInvariant(), @"E:\desk\a.txt",
            @"C:\vault\a.txt", 0, false, nameof(IOException), "设备未就绪", nameof(IOException), "磁盘已满"));
        e.Status.Should().Be(JournalStatus.Pending);
        w.Db.Rollbacks.Should().Be(1, "写失败计数的事务被回滚");
    }

    [Fact]
    public async Task Successful_failure_count_has_no_secondary_info()
    {
        var w = new World();
        w.Seed(IdA, @"E:\desk\a.txt", @"C:\vault\a.txt");
        w.Fs.Broken.Add(@"E:\desk\a.txt");
        await w.Recovery().RecoverAsync();
        w.Log.Received(1).RecoveryFailed(Arg.Is<RecoveryFailedEvent>(x =>
            x.ConsecutiveFailures == 1 && x.SecondaryExceptionType == null && x.SecondaryMessage == null));
    }

    [Theory]
    [InlineData("1a2b3c4d:000000000000000000000000000000AA", "1a2b3c4d:000000000000000000000000000000aa", true)]
    [InlineData("1A2B3C4D:00", "1a2b3c4d:00", true)]
    [InlineData("1a2b3c4d:aa", "1a2b3c4d:ab", false)]
    [InlineData("1a2b3c4d/aa", "1a2b3c4d\\aa", false)] // 不是路径：分隔符不等价
    [InlineData(null, "1a2b3c4d:aa", false)]
    [InlineData(null, null, true)]
    public void File_id_equality_is_ordinal_ignore_case_not_path_semantics(string? a, string? b, bool expected) =>
        JournalRecovery.FileIdEquals(a, b).Should().Be(expected);

    [Fact]
    public async Task Lower_case_file_id_from_probe_matches_entry()
    {
        var w = new World();
        var a = w.Seed(IdA, @"C:\desk\a.txt", @"C:\vault\a.txt");
        w.Fs.Files[@"C:\vault\a.txt"] = IdA.ToLowerInvariant();
        (await w.Recovery().RecoverAsync()).Single().Outcome.Should().Be(RecoveryOutcome.CommittedAtCandidate);
        a.Status.Should().Be(JournalStatus.Committed);
    }

    [Fact]
    public void Recovery_failure_counter_only_on_pending()
    {
        var e = new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), IdA, "s", "t", default);
        e.RecordRecoveryFailure().Should().Be(1);
        e.RecordRecoveryFailure().Should().Be(2);
        e.MarkLost();
        FluentActions.Invoking(() => e.RecordRecoveryFailure()).Should().Throw<InvalidTransitionException>();
        JournalEntry.MaxRecoveryFailures.Should().Be(3);
    }
}

using System.Globalization;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Application.Resources;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;
using NSubstitute;

namespace DeskBox.Domain.Tests;

/// <summary>内存 Journal。Frozen 后忽略一切写入，模拟进程被杀时磁盘上留下的状态。</summary>
internal sealed class InMemoryJournal : IJournal
{
    public Dictionary<Guid, JournalEntry> Entries { get; } = [];
    public bool Frozen { get; set; }

    public Task AppendPendingAsync(JournalEntry entry, CancellationToken ct = default) => Write(() => Entries[entry.Id] = entry);

    // 候选路径已经由调用方写到 entry 上（同一对象）；这里只校验持久化顺序
    public Task UpdateCandidateAsync(Guid entryId, string candidatePath, int attempt, CancellationToken ct = default) =>
        Write(() => Entries[entryId].TargetPath.Should().Be(candidatePath));

    public Task CommitAsync(Guid entryId, string actualTargetPath, CancellationToken ct = default) => Write(() => Entries[entryId].Commit(actualTargetPath));
    public Task RollBackAsync(Guid entryId, string abortReason, CancellationToken ct = default) => Write(() => Entries[entryId].RollBack(abortReason));
    public Task MarkLostAsync(Guid entryId, CancellationToken ct = default) => Write(() => Entries[entryId].MarkLost());

    public Task<IReadOnlyList<JournalEntry>> GetPendingAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<JournalEntry>>(Entries.Values.Where(e => e.Status == JournalStatus.Pending).ToList());

    private Task Write(Action a)
    {
        if (!Frozen) a();
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryNotices : ILostFileNoticeStore
{
    public List<LostFileNotice> All { get; } = [];
    public int Saves { get; private set; }
    public Task AddAsync(LostFileNotice notice, CancellationToken ct = default) { All.Add(notice); return Task.CompletedTask; }
    public Task<IReadOnlyList<LostFileNotice>> GetUnacknowledgedAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LostFileNotice>>(All.Where(n => !n.IsAcknowledged).ToList());
    public Task SaveAsync(LostFileNotice notice, CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
}

public class JournalRecoveryTests
{
    private const string IdA = "1a2b3c4d:000000000000000000000000000000aa";
    private const string IdOther = "1a2b3c4d:000000000000000000000000000000bb";
    private const string Src = @"C:\desk\notes.txt";
    private const string Dst = @"C:\vault\notes.txt";

    private readonly FakeFs _fs = new();
    private readonly InMemoryJournal _journal = new();
    private readonly InMemoryNotices _notices = new();
    private readonly IVaultRecordStore _records = Substitute.For<IVaultRecordStore>();
    private readonly IRecoveryLog _log = Substitute.For<IRecoveryLog>();

    private JournaledMove Move() => new(new RenamingFileMover(_fs, _fs, _fs), _journal, _fs, _log, TimeProvider.System);
    private JournalRecovery Recovery() => new(_journal, _fs, _fs, _records, _notices, TimeProvider.System);

    private void Occupy(int upTo)
    {
        if (upTo >= 1) _fs.Files[Dst] = IdOther;
        for (var n = 2; n <= upTo; n++) _fs.Files[$@"C:\vault\notes ({n}).txt"] = IdOther;
    }

    private static string Candidate(int attempt) => attempt == 1 ? Dst : $@"C:\vault\notes ({attempt}).txt";

    [Fact]
    public async Task Pending_records_file_id_and_each_candidate_before_attempt()
    {
        _fs.Files[Src] = IdA;
        Occupy(2);
        var seen = new List<string>();
        _fs.BeforeAttempt = _ => seen.Add(_journal.Entries.Values.Single().TargetPath);
        var r = await Move().ExecuteAsync(Guid.NewGuid(), Src, Dst);
        r.Should().Be(new MoveResult.Moved(Src, Candidate(3), true));
        seen.Should().Equal(Candidate(1), Candidate(2), Candidate(3));
        var e = _journal.Entries.Values.Single();
        (e.FileId, e.Attempt, e.Status, e.ActualTargetPath).Should().Be((IdA.ToUpperInvariant(), 3, JournalStatus.Committed, Candidate(3)));
    }

    // 杀进程模拟：在每次重试（1..4）的移动前/后被杀，重启后恢复
    [Theory]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)]
    public async Task Kill_at_each_retry_then_recover(int attempt, bool afterMove)
    {
        _fs.Files[Src] = IdA;
        Occupy(attempt - 1);
        _fs.KillAtAttempt = attempt;
        _fs.KillAfterMove = afterMove;
        _fs.OnKill = () => _journal.Frozen = true;

        await FluentActions.Awaiting(() => Move().ExecuteAsync(Guid.NewGuid(), Src, Dst)).Should().ThrowAsync<KilledException>();
        var pending = _journal.Entries.Values.Single();
        pending.Status.Should().Be(JournalStatus.Pending);
        (pending.TargetPath, pending.Attempt).Should().Be((Candidate(attempt), attempt));

        _journal.Frozen = false; // 重启
        var result = (await Recovery().RecoverAsync()).Single();

        if (afterMove)
        {
            result.Should().Be(new RecoveredEntry(pending.Id, RecoveryOutcome.CommittedAtCandidate, Candidate(attempt)));
            pending.Status.Should().Be(JournalStatus.Committed);
            pending.ActualTargetPath.Should().Be(Candidate(attempt));
            await _records.Received(1).AddAsync(Arg.Is<VaultRecord>(v =>
                v.OriginalPath == Src && v.VaultPath == Candidate(attempt) && v.FileId == IdA.ToUpperInvariant()), Arg.Any<CancellationToken>());
        }
        else
        {
            result.Outcome.Should().Be(RecoveryOutcome.Aborted);
            pending.Status.Should().Be(JournalStatus.RolledBack);
            _fs.Files[Src].Should().Be(IdA);
            await _records.DidNotReceiveWithAnyArgs().AddAsync(default!);
        }
        // 已占位的同名文件都没有被改动
        for (var k = 1; k < attempt; k++) _fs.Files[Candidate(k)].Should().Be(IdOther);
        _notices.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Vault_scan_finds_by_file_id_and_ignores_preexisting_same_name()
    {
        // 候选路径上是别人的同名文件，我们的文件在收纳库另一处（例如候选更新未落盘）
        var e = new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), IdA, Src, Dst, default);
        await _journal.AppendPendingAsync(e);
        _fs.Files[Dst] = IdOther;
        _fs.Files[@"C:\vault\sub\notes (7).txt"] = IdA;

        var r = (await Recovery().RecoverAsync()).Single();

        r.Should().Be(new RecoveredEntry(e.Id, RecoveryOutcome.CommittedFromVaultScan, @"C:\vault\sub\notes (7).txt"));
        e.ActualTargetPath.Should().Be(@"C:\vault\sub\notes (7).txt");
        await _records.Received(1).AddAsync(Arg.Is<VaultRecord>(v => v.VaultPath == @"C:\vault\sub\notes (7).txt"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Not_found_anywhere_marks_lost_with_persistent_notice_until_acknowledged()
    {
        var e = new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), IdA, @"C:\Users\u\Desktop\notes.txt", Dst, default);
        await _journal.AppendPendingAsync(e);
        _fs.Files[Dst] = IdOther; // 同名但不是它

        (await Recovery().RecoverAsync()).Single().Outcome.Should().Be(RecoveryOutcome.Lost);
        e.Status.Should().Be(JournalStatus.Lost);
        _notices.All.Should().ContainSingle().Which.FileName.Should().Be("notes.txt");
        await _records.DidNotReceiveWithAnyArgs().AddAsync(default!);

        const string expected = "有 1 个文件在上次整理中断后没能找回：notes.txt。它可能已被移动或删除，你可以在收纳库和桌面上搜索这个文件名。";
        // 两次"重启"都仍然提示
        (await new LostFileNotices(_notices, TimeProvider.System).GetMessageAsync()).Should().Be(expected);
        (await new LostFileNotices(_notices, TimeProvider.System).GetMessageAsync(CultureInfo.GetCultureInfo("zh-CN"))).Should().Be(expected);
        (await Recovery().RecoverAsync()).Should().BeEmpty();

        await new LostFileNotices(_notices, TimeProvider.System).AcknowledgeAllAsync();
        _notices.Saves.Should().Be(1);
        (await new LostFileNotices(_notices, TimeProvider.System).GetMessageAsync()).Should().BeNull();
    }

    [Fact]
    public void Lost_files_text_lists_all_names()
    {
        UiText.LostFiles(["a.txt", "b.png"]).Should()
            .Be("有 2 个文件在上次整理中断后没能找回：a.txt、b.png。它可能已被移动或删除，你可以在收纳库和桌面上搜索这个文件名。");
        FluentActions.Invoking(() => UiText.LostFiles([])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => UiText.LostFiles(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Source_without_file_id_is_not_moved_or_journaled()
    {
        var r = await Move().ExecuteAsync(Guid.NewGuid(), @"C:\desk\gone.txt", Dst);
        r.Should().Be(new MoveResult.Skipped(@"C:\desk\gone.txt", SkipReason.NotFound));
        _journal.Entries.Should().BeEmpty();
        _fs.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task Skipped_move_rolls_back_with_reason()
    {
        _fs.Files[Src] = IdA;
        _fs.Locked.Add(Src);
        (await Move().ExecuteAsync(Guid.NewGuid(), Src, Dst)).Should().BeOfType<MoveResult.Skipped>();
        _journal.Entries.Values.Single().AbortReason.Should().Be("Locked (Win32 32)");
    }

    // P1-1：取消 / 探测异常
    [Fact]
    public async Task Cancel_before_any_attempt_rolls_back_immediately()
    {
        _fs.Files[Src] = IdA;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var journal = Substitute.For<IJournal>();
        var sut = new JournaledMove(new RenamingFileMover(_fs, _fs, _fs), journal, _fs, _log, TimeProvider.System);
        await FluentActions.Awaiting(() => sut.ExecuteAsync(Guid.NewGuid(), Src, Dst, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        await journal.Received(1).RollBackAsync(Arg.Any<Guid>(), "Cancelled", CancellationToken.None);
        _fs.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_exception_rolls_back_as_not_moved()
    {
        _fs.Files[Src] = IdA;
        var probe = Substitute.For<IFileSystemProbe>();
        probe.GetAttributes(Src).Returns(_ => throw new UnauthorizedAccessException("denied"));
        var sut = new JournaledMove(new RenamingFileMover(_fs, probe, _fs), _journal, _fs, _log, TimeProvider.System);
        await FluentActions.Awaiting(() => sut.ExecuteAsync(Guid.NewGuid(), Src, Dst)).Should().ThrowAsync<UnauthorizedAccessException>();
        var e = _journal.Entries.Values.Single();
        (e.Status, e.AbortReason).Should().Be((JournalStatus.RolledBack, "Interrupted (UnauthorizedAccessException)"));
        _log.DidNotReceiveWithAnyArgs().UncertainPending(default!);
    }

    [Fact]
    public async Task Cancel_between_retries_with_source_intact_rolls_back_verified()
    {
        _fs.Files[Src] = IdA;
        Occupy(1);
        using var cts = new CancellationTokenSource();
        _fs.BeforeAttempt = _ => cts.Cancel(); // 第一次尝试（重名）时取消，第二次尝试前生效
        await FluentActions.Awaiting(() => Move().ExecuteAsync(Guid.NewGuid(), Src, Dst, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        var e = _journal.Entries.Values.Single();
        (e.Status, e.AbortReason).Should().Be((JournalStatus.RolledBack, "Cancelled, verified not moved"));
        _fs.Files[Src].Should().Be(IdA);
    }

    [Fact]
    public async Task Exception_after_attempt_with_uncertain_state_keeps_pending_and_logs()
    {
        _fs.Files[Src] = IdA;
        _fs.KillAtAttempt = 1;
        _fs.KillAfterMove = true; // 已移动后抛异常（非真正被杀，catch 会执行）
        var jobId = Guid.NewGuid();
        await FluentActions.Awaiting(() => Move().ExecuteAsync(jobId, Src, Dst)).Should().ThrowAsync<KilledException>();
        var e = _journal.Entries.Values.Single();
        e.Status.Should().Be(JournalStatus.Pending);
        _log.Received(1).UncertainPending(new UncertainPendingEvent(e.Id, jobId, IdA.ToUpperInvariant(), Src, Dst, 1,
            nameof(KilledException), "模拟进程被杀"));
        (await Recovery().RecoverAsync()).Single().Outcome.Should().Be(RecoveryOutcome.CommittedAtCandidate);
    }

    [Fact]
    public async Task Identity_probe_failure_during_verification_is_treated_as_uncertain()
    {
        _fs.Files[Src] = IdA;
        var identity = Substitute.For<IFileIdentity>();
        var calls = 0;
        identity.TryGetFileId(Src).Returns(_ => ++calls == 1 ? IdA : throw new IOException("busy"));
        var native = Substitute.For<INativeFileMove>();
        native.Move(Src, Dst).Returns(_ => throw new InvalidOperationException("boom"));
        var sut = new JournaledMove(new RenamingFileMover(native, _fs, _fs), _journal, identity, _log, TimeProvider.System);
        await FluentActions.Awaiting(() => sut.ExecuteAsync(Guid.NewGuid(), Src, Dst)).Should().ThrowAsync<InvalidOperationException>();
        _journal.Entries.Values.Single().Status.Should().Be(JournalStatus.Pending);
        _log.ReceivedWithAnyArgs(1).UncertainPending(default!);
    }

    [Fact]
    public async Task Default_interface_overload_passes_no_callback()
    {
        _fs.Files[Src] = IdA;
        IFileMover m = new RenamingFileMover(_fs, _fs, _fs);
        (await m.MoveAsync(Src, Dst)).Should().BeOfType<MoveResult.Moved>();
    }
}

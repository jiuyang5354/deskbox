using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace DeskBox.Infrastructure.Tests;

public sealed class StoreTests : IAsyncLifetime
{
    private const string IdA = "1A2B3C4D:000000000000000000000000000000AA";
    private const string IdB = "1A2B3C4D:000000000000000000000000000000BB";
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.FromHours(8));

    private readonly TempDb _t = new();
    private readonly SqliteJournal _journal = new();
    private readonly SqliteVaultRecordStore _vault = new();
    private readonly SqliteLostFileNoticeStore _notices = new();
    private readonly SqliteSnapshotStore _snapshots = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<JournalEntry> Seed(string fileId = IdA, string dst = @"C:\vault\a.txt", DateTimeOffset? at = null)
    {
        var e = new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), fileId, @"C:\desk\" + Path.GetFileName(dst.Replace('\\', '/')), dst, at ?? T0);
        await _t.Tx(u => _journal.AppendPendingAsync(u, e));
        return e;
    }

    private Task<JournalEntry?> Load(Guid id) => _t.Tx(u => _journal.GetAsync(u, id));

    // ---------- Journal ----------
    [Fact]
    public async Task Journal_roundtrips_all_fields()
    {
        var e = await Seed();
        var p = (await _t.Tx(u => _journal.GetPendingAsync(u))).Single();
        (p.Id, p.JobId, p.FileId, p.SourcePath, p.TargetPath, p.CreatedAt, p.Attempt, p.Status, p.RecoveryFailures)
            .Should().Be((e.Id, e.JobId, IdA, @"C:\desk\a.txt", @"C:\vault\a.txt", T0, 0, JournalStatus.Pending, 0));
        p.CreatedAt.Offset.Should().Be(TimeSpan.Zero, "统一以 UTC 保存");
        (await Load(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Journal_update_candidate_then_commit()
    {
        var e = await Seed();
        await _t.Tx(u => _journal.UpdateCandidateAsync(u, e.Id, @"C:\vault\a (2).txt", 2));
        var p = (await Load(e.Id))!;
        (p.TargetPath, p.Attempt).Should().Be((@"C:\vault\a (2).txt", 2));
        await _t.Tx(u => _journal.CommitAsync(u, e.Id, @"C:\vault\a (2).txt"));
        p = (await Load(e.Id))!;
        (p.Status, p.ActualTargetPath).Should().Be((JournalStatus.Committed, @"C:\vault\a (2).txt"));
        (await _t.Tx(u => _journal.GetPendingAsync(u))).Should().BeEmpty();
    }

    [Fact]
    public async Task Journal_rollback_and_mark_lost()
    {
        var a = await Seed();
        var b = await Seed(IdB);
        await _t.Tx(u => _journal.RollBackAsync(u, a.Id, "Locked (Win32 32)"));
        await _t.Tx(u => _journal.MarkLostAsync(u, b.Id));
        var ra = (await Load(a.Id))!;
        (ra.Status, ra.AbortReason).Should().Be((JournalStatus.RolledBack, "Locked (Win32 32)"));
        (await Load(b.Id))!.Status.Should().Be(JournalStatus.Lost);
    }

    [Fact]
    public async Task Journal_recovery_failures_count_and_persist()
    {
        var e = await Seed();
        (await _t.Tx(u => _journal.RecordRecoveryFailureAsync(u, e.Id))).Should().Be(1);
        (await _t.Tx(u => _journal.RecordRecoveryFailureAsync(u, e.Id))).Should().Be(2);
        (await Load(e.Id))!.RecoveryFailures.Should().Be(2);
    }

    [Fact]
    public async Task Journal_transitions_require_pending()
    {
        var e = await Seed();
        await _t.Tx(u => _journal.MarkLostAsync(u, e.Id));
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.CommitAsync(u, e.Id, "x"))).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.RollBackAsync(u, e.Id, "x"))).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.MarkLostAsync(u, e.Id))).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.UpdateCandidateAsync(u, e.Id, "x", 1))).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.RecordRecoveryFailureAsync(u, e.Id))).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.RecordRecoveryFailureAsync(u, Guid.NewGuid()))).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Journal_guards_and_duplicate_id()
    {
        var e = await Seed();
        await _t.Tx(u => _journal.MarkLostAsync(u, e.Id)); // 不再 Pending，排除部分唯一索引
        var sameId = JournalEntry.Restore(e.Id, e.JobId, IdB, e.SourcePath, e.TargetPath, T0, 0, JournalStatus.Pending, null, null, 0);
        (await FluentActions.Awaiting(() => _t.Tx(u => _journal.AppendPendingAsync(u, sameId))).Should().ThrowAsync<SqliteException>(
            "主键冲突不是 AlreadyInProgress，按错误抛出")).Which.SqliteErrorCode.Should().Be(19);
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.AppendPendingAsync(u, null!))).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.CommitAsync(u, e.Id, " "))).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.RollBackAsync(u, e.Id, ""))).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.UpdateCandidateAsync(u, e.Id, "", 1))).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Journal_check_constraint_rejects_unknown_status()
    {
        var e = await Seed();
        await FluentActions.Awaiting(() => _t.Tx(u => SqliteUnitOfWork.From(u).ExecuteAsync(
            $"UPDATE journal SET status = 'Bogus' WHERE id = '{e.Id:D}';", default))).Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Pending_is_ordered_by_creation_time()
    {
        var late = await Seed(IdA, @"C:\vault\1", T0.AddMinutes(5));
        var early = await Seed(IdB, @"C:\vault\2", T0);
        (await _t.Tx(u => _journal.GetPendingAsync(u))).Select(e => e.Id).Should().Equal(early.Id, late.Id);
    }

    // ---------- vault_record ----------
    [Fact]
    public async Task Vault_record_roundtrip_and_case_insensitive_unique_path()
    {
        var r = new VaultRecord(Guid.NewGuid(), @"C:\desk\a.txt", @"C:\Vault\A.txt", IdA, T0);
        await _t.Tx(u => _vault.AddAsync(u, r));
        var back = (await _t.Tx(u => _vault.FindByVaultPathAsync(u, @"c:\vault\a.TXT")))!;
        (back.Id, back.OriginalPath, back.VaultPath, back.FileId, back.MovedAt, back.IsRestored)
            .Should().Be((r.Id, r.OriginalPath, r.VaultPath, IdA, T0, false));

        var dup = new VaultRecord(Guid.NewGuid(), @"C:\desk\b.txt", @"c:\vault\a.txt", IdB, T0);
        (await FluentActions.Awaiting(() => _t.Tx(u => _vault.AddAsync(u, dup))).Should().ThrowAsync<SqliteException>())
            .Which.SqliteErrorCode.Should().Be(19, "UNIQUE vault_path");
        (await _t.Tx(u => _vault.FindByVaultPathAsync(u, @"C:\nope"))).Should().BeNull();
        await FluentActions.Awaiting(() => _t.Tx(u => _vault.AddAsync(u, null!))).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Vault_record_restored_at_roundtrips()
    {
        var r = new VaultRecord(Guid.NewGuid(), @"C:\desk\a.txt", @"C:\vault\a.txt", IdA, T0);
        r.MarkRestored(T0.AddHours(1));
        await _t.Tx(u => _vault.AddAsync(u, r));
        (await _t.Tx(u => _vault.FindByVaultPathAsync(u, r.VaultPath)))!.RestoredAt.Should().Be(T0.AddHours(1));
    }

    // ---------- lost_file_notice ----------
    [Fact]
    public async Task Notices_roundtrip_acknowledge_and_constraints()
    {
        var e = await Seed();
        var n = new LostFileNotice(e.Id, IdA, @"C:\desk\a.txt", T0);
        await _t.Tx(u => _notices.AddAsync(u, n));
        var back = (await _t.Tx(u => _notices.GetUnacknowledgedAsync(u))).Single();
        (back.JournalEntryId, back.FileId, back.FileName, back.DetectedAt).Should().Be((e.Id, IdA, "a.txt", T0));

        (await FluentActions.Awaiting(() => _t.Tx(u => _notices.AddAsync(u, n))).Should().ThrowAsync<SqliteException>())
            .Which.SqliteErrorCode.Should().Be(19, "主键 journal_entry_id");

        back.Acknowledge(T0.AddDays(1));
        await _t.Tx(u => _notices.SaveAsync(u, back));
        (await _t.Tx(u => _notices.GetUnacknowledgedAsync(u))).Should().BeEmpty();
    }

    [Fact]
    public async Task Notice_requires_existing_journal_entry_foreign_key()
    {
        var orphan = new LostFileNotice(Guid.NewGuid(), IdA, @"C:\desk\a.txt", T0);
        (await FluentActions.Awaiting(() => _t.Tx(u => _notices.AddAsync(u, orphan))).Should().ThrowAsync<SqliteException>())
            .Which.SqliteErrorCode.Should().Be(19, "foreign_keys=ON");
        await FluentActions.Awaiting(() => _t.Tx(u => _notices.SaveAsync(u, orphan))).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => _t.Tx(u => _notices.AddAsync(u, null!))).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _t.Tx(u => _notices.SaveAsync(u, null!))).Should().ThrowAsync<ArgumentNullException>();
    }

    // ---------- snapshot ----------
    [Fact]
    public async Task Snapshots_roundtrip_list_newest_first_and_delete_cascades()
    {
        var fence = Guid.NewGuid();
        var s1 = new Snapshot(Guid.NewGuid(), SnapshotTrigger.Manual, T0,
            [new SnapshotEntry(IdA, @"C:\desk\a.txt", fence, 10, 20), new SnapshotEntry(IdB, @"C:\desk\b.txt", null, -5, 0)]);
        var s2 = new Snapshot(Guid.NewGuid(), SnapshotTrigger.Auto, T0.AddMinutes(1), []);
        await _t.Tx(async u => { await _snapshots.SaveAsync(u, s1); await _snapshots.SaveAsync(u, s2); });

        var back = (await _t.Tx(u => _snapshots.GetAsync(u, s1.Id)))!;
        (back.Trigger, back.CreatedAt).Should().Be((SnapshotTrigger.Manual, T0));
        back.Entries.Should().Equal(s1.Entries);
        (await _t.Tx(u => _snapshots.ListAsync(u))).Select(s => s.Id).Should().Equal(s2.Id, s1.Id);
        (await _t.Tx(u => _snapshots.GetAsync(u, s2.Id)))!.Entries.Should().BeEmpty();

        await _t.Tx(u => _snapshots.DeleteAsync(u, s1.Id));
        (await _t.Tx(u => _snapshots.GetAsync(u, s1.Id))).Should().BeNull();
        (await _t.Scalar<long>("SELECT COUNT(*) FROM snapshot_entry;")).Should().Be(0, "ON DELETE CASCADE");
        await FluentActions.Awaiting(() => _t.Tx(u => _snapshots.SaveAsync(u, null!))).Should().ThrowAsync<ArgumentNullException>();
    }

    // ---------- 架构评审：约束与时间格式 ----------
    [Fact]
    public async Task Only_unique_index_on_journal_covering_file_id_is_the_pending_partial_index()
    {
        var rows = await _t.Tx(u => SqliteUnitOfWork.From(u).RunAsync(
            """
            SELECT il.name, il."unique", il.partial, ii.name FROM pragma_index_list('journal') il
            JOIN pragma_index_info(il.name) ii WHERE il."unique" = 1;
            """, c => Sql.ListAsync(c, r => (Index: r.GetString(0), Partial: r.GetInt64(2), Column: r.GetString(3)), default)));
        rows.Where(r => r.Column == "file_id").Should().ContainSingle()
            .Which.Should().Be((SqliteSchema.JournalPendingIndex, 1L, "file_id"));
        (await _t.Scalar<string>($"SELECT sql FROM sqlite_master WHERE name = '{SqliteSchema.JournalPendingIndex}';"))
            .Should().Contain("WHERE status = 'Pending'");
    }

    [Fact]
    public async Task Has_pending_is_case_insensitive_and_only_counts_pending()
    {
        var e = await Seed(IdA);
        (await _t.Tx(u => _journal.HasPendingAsync(u, IdA.ToLowerInvariant()))).Should().BeTrue();
        (await _t.Tx(u => _journal.HasPendingAsync(u, IdB))).Should().BeFalse();
        await _t.Tx(u => _journal.CommitAsync(u, e.Id, @"C:\vault\a.txt"));
        (await _t.Tx(u => _journal.HasPendingAsync(u, IdA))).Should().BeFalse();
        await FluentActions.Awaiting(() => _t.Tx(u => _journal.HasPendingAsync(u, " "))).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Direct_insert_of_second_pending_bypassing_precheck_throws_unique_error()
    {
        var first = await Seed(IdA, @"C:\vault\a.txt");
        var again = new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), IdA, @"C:\desk\a.txt", @"C:\vault\a (2).txt", T0);
        (await FluentActions.Awaiting(() => _t.Tx(u => _journal.AppendPendingAsync(u, again))).Should().ThrowAsync<SqliteException>())
            .Which.SqliteExtendedErrorCode.Should().Be(2067, "SQLITE_CONSTRAINT_UNIQUE，按错误抛出");
        (await Load(first.Id))!.Status.Should().Be(JournalStatus.Pending, "另一条 Pending 不受影响");

        await _t.Tx(u => _journal.CommitAsync(u, first.Id, @"C:\vault\a.txt"));
        await _t.Tx(u => _journal.AppendPendingAsync(u, again));
        await _t.Tx(u => _journal.RollBackAsync(u, again.Id, "Locked (Win32 32)"));
        await Seed(IdA, @"C:\vault\a (3).txt"); // 提交 / 回滚之后都可以再开新的 Pending
        (await _t.Scalar<long>("SELECT COUNT(*) FROM journal WHERE file_id = '" + IdA + "';")).Should().Be(3);
    }

    [Fact]
    public async Task Other_unique_violation_in_vault_record_propagates_as_error()
    {
        var r = new VaultRecord(Guid.NewGuid(), @"C:\desk\a.txt", @"C:\vault\a.txt", IdA, T0);
        await _t.Tx(u => _vault.AddAsync(u, r));
        var dup = new VaultRecord(Guid.NewGuid(), @"C:\desk\b.txt", @"C:\VAULT\A.TXT", IdB, T0);
        (await FluentActions.Awaiting(() => _t.Tx(u => _vault.AddAsync(u, dup))).Should().ThrowAsync<SqliteException>())
            .Which.SqliteExtendedErrorCode.Should().Be(2067, "两条在库记录同一路径 → 错误，不是 AlreadyInProgress");
    }

    [Fact]
    public async Task Restored_record_frees_its_vault_path_for_re_vaulting()
    {
        var r1 = new VaultRecord(Guid.NewGuid(), @"C:\desk\a.txt", @"C:\vault\a.txt", IdA, T0);
        await _t.Tx(u => _vault.AddAsync(u, r1));
        await _t.Tx(u => _vault.MarkRestoredAsync(u, r1.Id, T0.AddHours(1)));
        var r2 = new VaultRecord(Guid.NewGuid(), @"C:\desk\a.txt", @"C:\vault\a.txt", IdA, T0.AddHours(2));
        await _t.Tx(u => _vault.AddAsync(u, r2));
        (await _t.Tx(u => _vault.FindByVaultPathAsync(u, @"C:\vault\a.txt")))!.Id.Should().Be(r2.Id, "优先返回在库中的记录");
        (await _t.Scalar<long>("SELECT COUNT(*) FROM vault_record;")).Should().Be(2);
        await FluentActions.Awaiting(() => _t.Tx(u => _vault.MarkRestoredAsync(u, r1.Id, T0))).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Original_path_is_case_insensitive()
    {
        await _t.Tx(u => _vault.AddAsync(u, new VaultRecord(Guid.NewGuid(), @"C:\Desk\A.txt", @"C:\vault\a.txt", IdA, T0)));
        (await _t.Scalar<long>(@"SELECT COUNT(*) FROM vault_record WHERE original_path = 'c:\desk\a.TXT';")).Should().Be(1);
    }

    [Fact]
    public async Task Timestamps_are_stored_as_fixed_width_utc_text_and_sort_correctly()
    {
        var hk = new DateTimeOffset(2026, 10, 13, 9, 0, 0, 123, TimeSpan.FromHours(8));   // 01:00:00.123Z
        var ny = new DateTimeOffset(2026, 10, 12, 20, 30, 0, TimeSpan.FromHours(-4));      // 00:30:00.000Z，更早
        var later = await Seed(IdA, @"C:\vault\1", hk);
        var earlier = await Seed(IdB, @"C:\vault\2", ny);
        (await _t.Scalar<string>($"SELECT created_at FROM journal WHERE id = '{later.Id:D}';")).Should().Be("2026-10-13T01:00:00.123Z");
        (await _t.Scalar<string>($"SELECT created_at FROM journal WHERE id = '{earlier.Id:D}';")).Should().Be("2026-10-13T00:30:00.000Z");
        (await _t.Tx(u => _journal.GetPendingAsync(u))).Select(e => e.Id).Should().Equal(earlier.Id, later.Id);
        (await Load(later.Id))!.CreatedAt.Should().Be(hk, "同一时刻").And.Subject!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("2026-01-02T03:04:05.006Z")]
    [InlineData("0001-01-01T00:00:00.000Z")]
    public void Time_format_roundtrips(string text) => SqliteTime.Format(SqliteTime.Parse(text)).Should().Be(text);

    [Fact]
    public void Time_format_truncates_to_milliseconds_and_rejects_other_formats()
    {
        SqliteTime.Format(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(12_345)).Should().Be("2026-01-01T00:00:00.001Z");
        FluentActions.Invoking(() => SqliteTime.Parse("2026-01-01T00:00:00+08:00")).Should().Throw<FormatException>();
        FluentActions.Invoking(() => SqliteTime.Parse(null!)).Should().Throw<ArgumentNullException>();
    }

    // ---------- 端到端：JournalRecovery + SQLite ----------
    private sealed class Ids(Dictionary<string, string> map) : IFileIdentity, IVaultIndex
    {
        public string? TryGetFileId(string fullPath) => map.GetValueOrDefault(fullPath);
        public IEnumerable<string> EnumerateFiles() => map.Keys.Where(k => k.StartsWith(@"C:\vault\", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Log : IRecoveryLog
    {
        public List<RecoveryFailedEvent> Failed { get; } = [];
        public void UncertainPending(UncertainPendingEvent e) { }
        public void RecoveryFailed(RecoveryFailedEvent e) => Failed.Add(e);
    }

    [Fact]
    public async Task Recovery_on_sqlite_commits_marks_lost_and_rolls_back_failed_pairs_atomically()
    {
        var atTarget = await Seed(IdA, @"C:\vault\a.txt");
        var lost = await Seed(IdB, @"C:\vault\b.txt");
        // 第三条：落点已经有一条 vault_record（同路径），补提交时 UNIQUE 冲突 → 整个事务回滚，保持 Pending
        var clash = await Seed("1A2B3C4D:000000000000000000000000000000CC", @"C:\vault\c.txt");
        await _t.Tx(u => _vault.AddAsync(u, new VaultRecord(Guid.NewGuid(), @"C:\x", @"C:\vault\c.txt", IdA, T0)));
        var fs = new Ids(new() { [@"C:\vault\a.txt"] = IdA, [@"C:\vault\c.txt"] = "1A2B3C4D:000000000000000000000000000000CC" });
        var log = new Log();
        var recovery = new JournalRecovery(_t.Db, _journal, fs, fs, _vault, _notices, log, TimeProvider.System);

        var r = await recovery.RecoverAsync();

        r.Select(x => (x.EntryId, x.Outcome)).Should().BeEquivalentTo(new[]
        {
            (atTarget.Id, RecoveryOutcome.CommittedAtCandidate), (lost.Id, RecoveryOutcome.Lost), (clash.Id, RecoveryOutcome.Failed),
        });
        (await Load(atTarget.Id))!.Status.Should().Be(JournalStatus.Committed);
        (await _t.Tx(u => _vault.FindByVaultPathAsync(u, @"C:\vault\a.txt")))!.FileId.Should().Be(IdA);
        (await Load(lost.Id))!.Status.Should().Be(JournalStatus.Lost);
        (await _t.Tx(u => _notices.GetUnacknowledgedAsync(u))).Should().ContainSingle(n => n.JournalEntryId == lost.Id);
        var c = (await Load(clash.Id))!;
        (c.Status, c.RecoveryFailures).Should().Be((JournalStatus.Pending, 1));
        log.Failed.Should().ContainSingle().Which.ExceptionType.Should().Be(nameof(SqliteException));
        (await _t.Scalar<long>("SELECT COUNT(*) FROM vault_record;")).Should().Be(2);

        // 再跑两次：第三次失败转 Lost，提示与 MarkLost 同一事务
        await recovery.RecoverAsync();
        (await recovery.RecoverAsync()).Single().Outcome.Should().Be(RecoveryOutcome.Lost);
        (await Load(clash.Id))!.Status.Should().Be(JournalStatus.Lost);
        (await _t.Tx(u => _notices.GetUnacknowledgedAsync(u))).Should().HaveCount(2);
        (await new LostFileNotices(_t.Db, _notices, TimeProvider.System).GetMessageAsync()).Should().Contain("b.txt").And.Contain("c.txt");
    }
}

/// <summary>JournaledMove + SqliteJournal：同一文件已有 Pending 时，Begin 就返回 AlreadyInProgress，不调用移动。</summary>
public sealed class AlreadyInProgressIntegrationTests : IAsyncLifetime
{
    private const string IdA = "1A2B3C4D:000000000000000000000000000000AA";
    private readonly TempDb _t = new();
    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private sealed class CountingMover : IFileMover
    {
        public int Calls { get; private set; }
        public Task<Domain.Files.MoveResult> MoveAsync(string sourcePath, string targetPath,
            Func<string, CancellationToken, Task>? beforeAttempt, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<Domain.Files.MoveResult>(new Domain.Files.MoveResult.Moved(sourcePath, targetPath));
        }
    }

    private sealed class Fixed : IFileIdentity { public string? TryGetFileId(string fullPath) => IdA; }
    private sealed class NoProbe : IFileSystemProbe
    {
        public bool IsDirectory(string path) => false;
        public uint? GetAttributes(string path) => 0x20;
    }

    private sealed class NoLog : IRecoveryLog
    {
        public int Count { get; private set; }
        public void UncertainPending(UncertainPendingEvent e) => Count++;
        public void RecoveryFailed(RecoveryFailedEvent e) => Count++;
    }

    [Fact]
    public async Task Second_move_of_same_file_while_pending_is_skipped_at_begin()
    {
        var journal = new SqliteJournal();
        var pending = new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), IdA, @"C:\desk\a.txt", @"C:\vault\a.txt", DateTimeOffset.UtcNow);
        await _t.Tx(u => journal.AppendPendingAsync(u, pending)); // 另一个整理任务正在移动它
        var mover = new CountingMover();
        var log = new NoLog();
        var sut = new JournaledMove(_t.Db, mover, journal, new Fixed(), new NoProbe(), log, TimeProvider.System);

        var r = await sut.ExecuteAsync(Guid.NewGuid(), @"C:\desk\a.txt", @"C:\vault\a.txt");

        r.Should().Be(new Domain.Files.MoveResult.Skipped(@"C:\desk\a.txt", Domain.Files.SkipReason.AlreadyInProgress));
        mover.Calls.Should().Be(0);
        log.Count.Should().Be(0);
        (await _t.Tx(u => journal.GetPendingAsync(u))).Should().ContainSingle().Which.Id.Should().Be(pending.Id);
        (await _t.Scalar<long>("SELECT COUNT(*) FROM journal;")).Should().Be(1);
    }
}

/// <summary>
/// 崩溃注入：MoveFileExW 已成功、"标记完成"事务还没提交时进程崩溃。
/// 证明 Pending（含候选路径）在移动之前就已提交到磁盘，恢复按 file_id 在候选处找到文件并补标记完成。
/// </summary>
public sealed class CrashAfterMoveTests : IAsyncLifetime
{
    private const string IdA = "1A2B3C4D:000000000000000000000000000000AA";
    private const string Src = @"C:\desk\a.txt";
    private const string Dst = @"C:\vault\a.txt";
    private const string Candidate = @"C:\vault\a (2).txt"; // 首选目标已占用，改名后的候选
    private readonly TempDb _t = new();
    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    /// <summary>模拟文件系统：文件 ID → 当前路径。</summary>
    private sealed class Fs : IFileIdentity, IVaultIndex, IFileSystemProbe, IFileMover
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase) { [Src] = IdA };
        public bool Moved { get; private set; }
        public string? TryGetFileId(string fullPath) => Files.GetValueOrDefault(fullPath);
        public IEnumerable<string> EnumerateFiles() => Files.Keys.Where(k => k.StartsWith(@"C:\vault\", StringComparison.OrdinalIgnoreCase)).ToList();
        public bool IsDirectory(string path) => false;
        public uint? GetAttributes(string path) => 0x20;

        public async Task<Domain.Files.MoveResult> MoveAsync(string sourcePath, string targetPath,
            Func<string, CancellationToken, Task>? beforeAttempt, CancellationToken ct = default)
        {
            if (beforeAttempt is not null) await beforeAttempt(targetPath, ct);
            if (beforeAttempt is not null) await beforeAttempt(Candidate, ct); // 第一次 183，改名重试
            Files.Remove(sourcePath);
            Files[Candidate] = IdA; // MoveFileExW 成功
            Moved = true;
            return new Domain.Files.MoveResult.Moved(sourcePath, Candidate);
        }
    }

    /// <summary>移动完成后，再开任何事务都"崩溃"（进程在标记完成前死掉）。</summary>
    private sealed class CrashingDb(IUnitOfWorkFactory inner, Fs fs) : IUnitOfWorkFactory
    {
        public Task<IUnitOfWork> BeginAsync(CancellationToken ct = default) =>
            fs.Moved ? throw new InvalidOperationException("simulated crash before mark-complete") : inner.BeginAsync(ct);
    }

    private sealed class Log : IRecoveryLog
    {
        public void UncertainPending(UncertainPendingEvent e) { }
        public void RecoveryFailed(RecoveryFailedEvent e) { }
    }

    [Fact]
    public async Task Crash_after_move_before_mark_complete_leaves_committed_pending_and_recovery_finishes_it()
    {
        var fs = new Fs();
        var journal = new SqliteJournal();
        var vault = new SqliteVaultRecordStore();
        var sut = new JournaledMove(new CrashingDb(_t.Db, fs), fs, journal, fs, fs, new Log(), TimeProvider.System);

        await FluentActions.Awaiting(() => sut.ExecuteAsync(Guid.NewGuid(), Src, Dst)).Should().ThrowAsync<InvalidOperationException>();
        fs.Moved.Should().BeTrue();

        // "重启"：新连接读到的 Pending 已带候选路径——说明 Begin 与候选写入都在移动前提交
        var pending = (await _t.Tx(u => journal.GetPendingAsync(u))).Should().ContainSingle().Subject;
        (pending.Status, pending.FileId, pending.SourcePath, pending.TargetPath).Should().Be((JournalStatus.Pending, IdA, Src, Candidate));
        (await _t.Scalar<string>("SELECT target_path FROM journal;")).Should().Be(Candidate);

        var r = await new JournalRecovery(_t.Db, journal, fs, fs, vault, new SqliteLostFileNoticeStore(), new Log(), TimeProvider.System).RecoverAsync();

        r.Should().ContainSingle().Which.Outcome.Should().Be(RecoveryOutcome.CommittedAtCandidate);
        (await _t.Tx(u => journal.GetPendingAsync(u))).Should().BeEmpty();
        (await _t.Scalar<string>("SELECT status FROM journal;")).Should().Be("Committed");
        (await _t.Scalar<string>("SELECT actual_target_path FROM journal;")).Should().Be(Candidate, "恢复按 file_id 在候选处找到了文件");
        (await _t.Tx(u => vault.FindByVaultPathAsync(u, Candidate)))!.FileId.Should().Be(IdA);
    }
}

public sealed class FenceStoreTests : IAsyncLifetime
{
    private readonly TempDb _t = new();
    private readonly SqliteFenceStore _store = new();
    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Fence_roundtrips_upserts_and_orders()
    {
        var a = new Fence(Guid.NewGuid(), "工作", @"\\.\DISPLAY2", -40, 7, 300, 200, dpi: 144, opacity: 60, isStaging: true) { SortOrder = 2 };
        var b = new Fence(Guid.NewGuid(), "B", @"\\.\DISPLAY1", 0, 0, 120, 120) { SortOrder = 1 };
        await _t.Tx(u => _store.UpsertAsync(u, a));
        await _t.Tx(u => _store.UpsertAsync(u, b));

        var got = (await _t.Tx(u => _store.GetAsync(u, a.Id)))!;
        (got.Title, got.MonitorId, got.X, got.Y, got.Width, got.Height, got.Dpi, got.Opacity, got.IsStaging, got.SortOrder)
            .Should().Be(("工作", @"\\.\DISPLAY2", -40, 7, 300, 200, 144, 60, true, 2));
        (await _t.Tx(u => _store.ListAsync(u))).Select(f => f.Id).Should().Equal(b.Id, a.Id);

        a.MoveTo(@"\\.\DISPLAY1", 5, 6, 96);
        a.Resize(400, 500);
        await _t.Tx(u => _store.UpsertAsync(u, a));
        (await _t.Tx(u => _store.GetAsync(u, a.Id)))!.Should().Match<Fence>(f => f.X == 5 && f.Width == 400 && f.MonitorId == @"\\.\DISPLAY1");
        (await _t.Scalar<long>("SELECT COUNT(*) FROM fence;")).Should().Be(2);
        (await _t.Tx(u => _store.GetAsync(u, Guid.NewGuid()))).Should().BeNull();
        await FluentActions.Awaiting(() => _t.Tx(u => _store.UpsertAsync(u, null!))).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _t.Scalar<long>("INSERT INTO fence (id,title,monitor_id,x,y,width,height,dpi,opacity,is_staging) VALUES ('x','t','m',0,0,10,10,96,80,0);"))
            .Should().ThrowAsync<SqliteException>();
    }
}

public sealed class FenceItemStoreTests : IAsyncLifetime
{
    private const string IdA = "1A2B3C4D:000000000000000000000000000000AA";
    private const string IdB = "1A2B3C4D:000000000000000000000000000000BB";
    private readonly TempDb _t = new();
    private readonly SqliteFenceStore _fences = new();
    private readonly SqliteFenceItemStore _items = new();
    private readonly Fence _a = new(Guid.NewGuid(), "A", "m", 0, 0, 200, 200);
    private readonly Fence _b = new(Guid.NewGuid(), "B", "m", 300, 0, 200, 200);

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await _t.Tx(async u => { await _fences.UpsertAsync(u, _a); await _fences.UpsertAsync(u, _b); });
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Item_roundtrips_lists_by_fence_in_order_and_reassigns()
    {
        var i1 = new FenceItem(Guid.NewGuid(), _a.Id, @"C:\Users\u\Desktop\a.txt", IdA.ToLowerInvariant(), sortOrder: 1);
        var i2 = new FenceItem(Guid.NewGuid(), _a.Id, @"C:\Users\Public\Desktop\b.lnk", IdB, isPublicDesktop: true, sortOrder: 0);
        await _t.Tx(async u => { await _items.UpsertAsync(u, i1); await _items.UpsertAsync(u, i2); });

        var got = (await _t.Tx(u => _items.GetAsync(u, i2.Id)))!;
        (got.FenceId, got.FilePath, got.FileId, got.IsPublicDesktop, got.SortOrder)
            .Should().Be((_a.Id, @"C:\Users\Public\Desktop\b.lnk", IdB, true, 0));
        (await _t.Tx(u => _items.ListByFenceAsync(u, _a.Id))).Select(i => i.Id).Should().Equal(i2.Id, i1.Id);

        i1.MoveToFence(_b.Id, 0);
        await _t.Tx(u => _items.UpsertAsync(u, i1));
        (await _t.Tx(u => _items.ListByFenceAsync(u, _b.Id))).Single().Id.Should().Be(i1.Id);
        (await _t.Scalar<long>("SELECT COUNT(*) FROM fence_item;")).Should().Be(2, "改归属是更新，不是新增");

        await _t.Tx(u => _items.DeleteAsync(u, i1.Id));
        (await _t.Tx(u => _items.GetAsync(u, i1.Id))).Should().BeNull();
        await FluentActions.Awaiting(() => _t.Tx(u => _items.UpsertAsync(u, null!))).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task File_path_is_globally_unique_case_insensitive()
    {
        await _t.Tx(u => _items.UpsertAsync(u, new FenceItem(Guid.NewGuid(), _a.Id, @"C:\Desk\a.txt", IdA)));
        await FluentActions.Awaiting(() => _t.Tx(u => _items.UpsertAsync(u, new FenceItem(Guid.NewGuid(), _b.Id, @"c:\DESK\A.TXT", IdB))))
            .Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Item_requires_existing_fence_and_deleting_fence_cascades_mappings_only()
    {
        await FluentActions.Awaiting(() => _t.Tx(u => _items.UpsertAsync(u, new FenceItem(Guid.NewGuid(), Guid.NewGuid(), @"C:\x", IdA))))
            .Should().ThrowAsync<SqliteException>("外键");
        var i = new FenceItem(Guid.NewGuid(), _a.Id, @"C:\Desk\a.txt", IdA);
        await _t.Tx(u => _items.UpsertAsync(u, i));
        await _t.Tx(u => _fences.DeleteAsync(u, _a.Id));
        (await _t.Tx(u => _fences.GetAsync(u, _a.Id))).Should().BeNull();
        (await _t.Tx(u => _items.GetAsync(u, i.Id))).Should().BeNull();
        (await _t.Tx(u => _fences.GetAsync(u, _b.Id))).Should().NotBeNull();
    }

    [Fact]
    public async Task Collapsed_state_roundtrips()
    {
        _b.Collapse();
        await _t.Tx(u => _fences.UpsertAsync(u, _b));
        (await _t.Tx(u => _fences.GetAsync(u, _b.Id)))!.IsCollapsed.Should().BeTrue();
        _b.Expand();
        await _t.Tx(u => _fences.UpsertAsync(u, _b));
        (await _t.Tx(u => _fences.GetAsync(u, _b.Id)))!.IsCollapsed.Should().BeFalse();
    }
}

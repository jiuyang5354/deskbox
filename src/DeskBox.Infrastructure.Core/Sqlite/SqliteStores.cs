using System.Globalization;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace DeskBox.Infrastructure.Sqlite;

/// <summary>
/// 所有时间统一以 UTC 文本保存，固定格式 yyyy-MM-ddTHH:mm:ss.fffZ（定长，按文本排序即按时间排序）。
/// 写入时先转 UTC 并截断到毫秒；读出为 Offset = 0 的 DateTimeOffset。
/// </summary>
public static class SqliteTime
{
    public const string FormatString = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Format(DateTimeOffset t) => t.UtcDateTime.ToString(FormatString, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var dt = DateTime.ParseExact(s, FormatString, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
    }
}

internal static class Sql
{
    public static string Id(Guid g) => g.ToString("D");
    public static string Time(DateTimeOffset t) => SqliteTime.Format(t);
    public static string? Time(DateTimeOffset? t) => t is { } v ? SqliteTime.Format(v) : null;
    public static DateTimeOffset ParseTime(string s) => SqliteTime.Parse(s);
    public static Guid ParseId(string s) => Guid.ParseExact(s, "D");

    public static async Task<IReadOnlyList<T>> ListAsync<T>(SqliteCommand c, Func<SqliteDataReader, T> map, CancellationToken ct)
    {
        var list = new List<T>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }

    public static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>UPDATE 必须恰好命中一条 Pending，否则说明条目不存在或已不是 Pending。</summary>
    public static void EnsureOne(int rows, Guid id, string op)
    {
        if (rows != 1) throw new InvalidOperationException($"{op}: journal {id} 不存在或已不是 Pending");
    }
}

/// <summary>SQLite Journal。所有读写都在调用方的 <see cref="IUnitOfWork"/> 上，不自行打开连接。</summary>
public sealed class SqliteJournal : IJournal
{
    private const string Pending = nameof(JournalStatus.Pending);

    /// <summary>
    /// 在 SAVEPOINT 里插入：只有命中 <see cref="SqliteSchema.JournalPendingIndex"/> 的唯一冲突
    /// 才回滚到保存点并抛 <see cref="JournalConflictException"/>，工作单元仍可继续使用；其他错误原样抛出。
    /// </summary>
    public async Task AppendPendingAsync(IUnitOfWork uow, JournalEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var s = SqliteUnitOfWork.From(uow);
        await s.ExecuteAsync("SAVEPOINT append_pending;", ct);
        try
        {
            await s.ExecuteAsync(
                """
                INSERT INTO journal (id, job_id, file_id, source_path, target_path, attempt, status, actual_target_path, abort_reason, recovery_failures, created_at)
                VALUES ($id, $job, $fid, $src, $dst, $attempt, $status, $actual, $reason, $failures, $created);
                """, ct,
                ("$id", Sql.Id(entry.Id)), ("$job", Sql.Id(entry.JobId)), ("$fid", entry.FileId), ("$src", entry.SourcePath),
                ("$dst", entry.TargetPath), ("$attempt", entry.Attempt), ("$status", entry.Status.ToString()),
                ("$actual", entry.ActualTargetPath), ("$reason", entry.AbortReason), ("$failures", entry.RecoveryFailures),
                ("$created", Sql.Time(entry.CreatedAt)));
        }
        catch (SqliteException ex) when (IsPendingConflict(ex))
        {
            await s.ExecuteAsync("ROLLBACK TO append_pending;", ct);
            await s.ExecuteAsync("RELEASE append_pending;", ct);
            throw new JournalConflictException(entry.FileId, ex);
        }
        await s.ExecuteAsync("RELEASE append_pending;", ct);
    }

    internal const int SqliteConstraintUnique = 2067;

    /// <summary>仅 SQLITE_CONSTRAINT_UNIQUE 且消息点名 Pending 部分唯一索引（或其列）。</summary>
    internal static bool IsPendingConflict(SqliteException ex) =>
        ex.SqliteExtendedErrorCode == SqliteConstraintUnique &&
        (ex.Message.Contains(SqliteSchema.JournalPendingIndex, StringComparison.Ordinal) ||
         ex.Message.Contains("UNIQUE constraint failed: journal.file_id", StringComparison.Ordinal));

    public async Task UpdateCandidateAsync(IUnitOfWork uow, Guid entryId, string candidatePath, int attempt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        var n = await SqliteUnitOfWork.From(uow).ExecuteAsync(
            $"UPDATE journal SET target_path = $dst, attempt = $attempt WHERE id = $id AND status = '{Pending}';", ct,
            ("$dst", candidatePath), ("$attempt", attempt), ("$id", Sql.Id(entryId)));
        Sql.EnsureOne(n, entryId, nameof(UpdateCandidateAsync));
    }

    public Task CommitAsync(IUnitOfWork uow, Guid entryId, string actualTargetPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actualTargetPath);
        return TransitionAsync(uow, entryId, JournalStatus.Committed, "actual_target_path = $v", actualTargetPath, ct);
    }

    public Task RollBackAsync(IUnitOfWork uow, Guid entryId, string abortReason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(abortReason);
        return TransitionAsync(uow, entryId, JournalStatus.RolledBack, "abort_reason = $v", abortReason, ct);
    }

    public Task MarkLostAsync(IUnitOfWork uow, Guid entryId, CancellationToken ct = default) =>
        TransitionAsync(uow, entryId, JournalStatus.Lost, "abort_reason = abort_reason", null, ct);

    public async Task<int> RecordRecoveryFailureAsync(IUnitOfWork uow, Guid entryId, CancellationToken ct = default)
    {
        var result = await SqliteUnitOfWork.From(uow).RunAsync(
            $"UPDATE journal SET recovery_failures = recovery_failures + 1 WHERE id = $id AND status = '{Pending}' RETURNING recovery_failures;",
            c => c.ExecuteScalarAsync(ct), ("$id", Sql.Id(entryId)));
        if (result is null) Sql.EnsureOne(0, entryId, nameof(RecordRecoveryFailureAsync));
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public Task<IReadOnlyList<JournalEntry>> GetPendingAsync(IUnitOfWork uow, CancellationToken ct = default) =>
        SqliteUnitOfWork.From(uow).RunAsync(
            $"""
            SELECT id, job_id, file_id, source_path, target_path, created_at, attempt, status, actual_target_path, abort_reason, recovery_failures
            FROM journal WHERE status = '{Pending}' ORDER BY created_at, id;
            """, c => Sql.ListAsync(c, Map, ct));

    /// <summary>按 ID 读取（任何状态），供诊断与测试。</summary>
    public async Task<JournalEntry?> GetAsync(IUnitOfWork uow, Guid entryId, CancellationToken ct = default)
    {
        var list = await SqliteUnitOfWork.From(uow).RunAsync(
            """
            SELECT id, job_id, file_id, source_path, target_path, created_at, attempt, status, actual_target_path, abort_reason, recovery_failures
            FROM journal WHERE id = $id;
            """, c => Sql.ListAsync(c, Map, ct), ("$id", Sql.Id(entryId)));
        return list.Count == 0 ? null : list[0];
    }

    private static JournalEntry Map(SqliteDataReader r) => JournalEntry.Restore(
        Sql.ParseId(r.GetString(0)), Sql.ParseId(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4),
        Sql.ParseTime(r.GetString(5)), r.GetInt32(6), Enum.Parse<JournalStatus>(r.GetString(7)),
        Sql.NullableString(r, 8), Sql.NullableString(r, 9), r.GetInt32(10));

    private static async Task TransitionAsync(IUnitOfWork uow, Guid id, JournalStatus to, string set, string? value, CancellationToken ct)
    {
        var n = await SqliteUnitOfWork.From(uow).ExecuteAsync(
            $"UPDATE journal SET status = '{to}', {set} WHERE id = $id AND status = '{Pending}';", ct,
            ("$id", Sql.Id(id)), ("$v", value));
        Sql.EnsureOne(n, id, to.ToString());
    }
}

public sealed class SqliteVaultRecordStore : IVaultRecordStore
{
    public Task AddAsync(IUnitOfWork uow, VaultRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return SqliteUnitOfWork.From(uow).ExecuteAsync(
            """
            INSERT INTO vault_record (id, original_path, vault_path, file_id, moved_at, restored_at)
            VALUES ($id, $orig, $vault, $fid, $moved, $restored);
            """, ct,
            ("$id", Sql.Id(record.Id)), ("$orig", record.OriginalPath), ("$vault", record.VaultPath),
            ("$fid", record.FileId), ("$moved", Sql.Time(record.MovedAt)), ("$restored", Sql.Time(record.RestoredAt)));
    }

    /// <summary>标记已还原（仅在库中的记录）。之后同一 vault_path 可以再次收纳。</summary>
    public async Task MarkRestoredAsync(IUnitOfWork uow, Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        var n = await SqliteUnitOfWork.From(uow).ExecuteAsync(
            "UPDATE vault_record SET restored_at = $at WHERE id = $id AND restored_at IS NULL;", ct,
            ("$at", Sql.Time(at)), ("$id", Sql.Id(id)));
        if (n != 1) throw new InvalidOperationException($"vault_record {id} 不存在或已还原");
    }

    /// <summary>按收纳库路径读取（不区分大小写），优先返回在库中的记录。</summary>
    public async Task<VaultRecord?> FindByVaultPathAsync(IUnitOfWork uow, string vaultPath, CancellationToken ct = default)
    {
        var list = await SqliteUnitOfWork.From(uow).RunAsync(
            "SELECT id, original_path, vault_path, file_id, moved_at, restored_at FROM vault_record WHERE vault_path = $p ORDER BY restored_at IS NOT NULL, moved_at DESC LIMIT 1;",
            c => Sql.ListAsync(c, r =>
            {
                var v = new VaultRecord(Sql.ParseId(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), Sql.ParseTime(r.GetString(4)));
                if (!r.IsDBNull(5)) v.MarkRestored(Sql.ParseTime(r.GetString(5)));
                return v;
            }, ct), ("$p", vaultPath));
        return list.Count == 0 ? null : list[0];
    }
}

public sealed class SqliteLostFileNoticeStore : ILostFileNoticeStore
{
    public Task AddAsync(IUnitOfWork uow, LostFileNotice notice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return SqliteUnitOfWork.From(uow).ExecuteAsync(
            """
            INSERT INTO lost_file_notice (journal_entry_id, file_id, original_path, detected_at, acknowledged_at)
            VALUES ($id, $fid, $orig, $at, $ack);
            """, ct,
            ("$id", Sql.Id(notice.JournalEntryId)), ("$fid", notice.FileId), ("$orig", notice.OriginalPath),
            ("$at", Sql.Time(notice.DetectedAt)), ("$ack", Sql.Time(notice.AcknowledgedAt)));
    }

    public Task<IReadOnlyList<LostFileNotice>> GetUnacknowledgedAsync(IUnitOfWork uow, CancellationToken ct = default) =>
        SqliteUnitOfWork.From(uow).RunAsync(
            "SELECT journal_entry_id, file_id, original_path, detected_at FROM lost_file_notice WHERE acknowledged_at IS NULL ORDER BY detected_at, journal_entry_id;",
            c => Sql.ListAsync(c, r => new LostFileNotice(Sql.ParseId(r.GetString(0)), r.GetString(1), r.GetString(2), Sql.ParseTime(r.GetString(3))), ct));

    public async Task SaveAsync(IUnitOfWork uow, LostFileNotice notice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var n = await SqliteUnitOfWork.From(uow).ExecuteAsync(
            "UPDATE lost_file_notice SET acknowledged_at = $ack WHERE journal_entry_id = $id;", ct,
            ("$ack", Sql.Time(notice.AcknowledgedAt)), ("$id", Sql.Id(notice.JournalEntryId)));
        if (n != 1) throw new InvalidOperationException($"lost_file_notice {notice.JournalEntryId} 不存在");
    }
}

public sealed class SqliteSnapshotStore : ISnapshotStore
{
    private const string Columns = "s.id, s.trigger_kind, s.created_at, e.file_id, e.file_path, e.fence_id, e.x, e.y";

    public async Task SaveAsync(IUnitOfWork uow, Snapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var s = SqliteUnitOfWork.From(uow);
        await s.ExecuteAsync("INSERT INTO snapshot (id, trigger_kind, created_at) VALUES ($id, $t, $at);", ct,
            ("$id", Sql.Id(snapshot.Id)), ("$t", snapshot.Trigger.ToString()), ("$at", Sql.Time(snapshot.CreatedAt)));
        for (var i = 0; i < snapshot.Entries.Count; i++)
        {
            var e = snapshot.Entries[i];
            await s.ExecuteAsync(
                "INSERT INTO snapshot_entry (snapshot_id, ordinal, file_id, file_path, fence_id, x, y) VALUES ($s, $o, $fid, $p, $fence, $x, $y);", ct,
                ("$s", Sql.Id(snapshot.Id)), ("$o", i), ("$fid", e.FileId), ("$p", e.FilePath),
                ("$fence", e.FenceId is { } f ? Sql.Id(f) : null), ("$x", e.X), ("$y", e.Y));
        }
    }

    public async Task<Snapshot?> GetAsync(IUnitOfWork uow, Guid id, CancellationToken ct = default) =>
        (await QueryAsync(uow, "WHERE s.id = $id", ct, ("$id", Sql.Id(id)))).SingleOrDefault();

    public Task<IReadOnlyList<Snapshot>> ListAsync(IUnitOfWork uow, CancellationToken ct = default) => QueryAsync(uow, "", ct);

    public async Task DeleteAsync(IUnitOfWork uow, Guid id, CancellationToken ct = default) =>
        await SqliteUnitOfWork.From(uow).ExecuteAsync("DELETE FROM snapshot WHERE id = $id;", ct, ("$id", Sql.Id(id)));

    private static async Task<IReadOnlyList<Snapshot>> QueryAsync(IUnitOfWork uow, string where, CancellationToken ct, params (string, object?)[] args)
    {
        var rows = await SqliteUnitOfWork.From(uow).RunAsync(
            $"SELECT {Columns} FROM snapshot s LEFT JOIN snapshot_entry e ON e.snapshot_id = s.id {where} ORDER BY s.created_at DESC, s.id, e.ordinal;",
            c => Sql.ListAsync(c, r => (
                Id: Sql.ParseId(r.GetString(0)), Trigger: Enum.Parse<SnapshotTrigger>(r.GetString(1)), At: Sql.ParseTime(r.GetString(2)),
                Entry: r.IsDBNull(3) ? null : new SnapshotEntry(r.GetString(3), r.GetString(4),
                    r.IsDBNull(5) ? null : Sql.ParseId(r.GetString(5)), r.GetInt32(6), r.GetInt32(7))), ct), args);
        return rows.GroupBy(r => r.Id)
            .Select(g => new Snapshot(g.Key, g.First().Trigger, g.First().At, g.Where(x => x.Entry is not null).Select(x => x.Entry!)))
            .ToList();
    }
}

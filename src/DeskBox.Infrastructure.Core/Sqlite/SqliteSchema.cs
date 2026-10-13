using DeskBox.Application.Ports;

namespace DeskBox.Infrastructure.Sqlite;

/// <summary>
/// 数据库结构与迁移（TDD v1.0.6 §4.4.2）。版本号存于 PRAGMA user_version；
/// 每个迁移在一个 BEGIN IMMEDIATE 事务里执行并提升版本号，可重复调用。
/// </summary>
public static class SqliteSchema
{
    public const string JournalPendingIndex = "ux_journal_pending_file_id";

    // 数据库尚未发布，v1 迁移原地修改（TDD v1.0.6 §4.4.2）
    internal static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE journal (
            id                 TEXT    NOT NULL PRIMARY KEY,          -- 日志条目 ID（GUID）
            job_id             TEXT    NOT NULL,                      -- 整理任务 ID
            file_id            TEXT    NOT NULL,                      -- 卷序列号:128 位文件 ID（大写）
            source_path        TEXT    NOT NULL,                      -- 源路径
            target_path        TEXT    NOT NULL,                      -- 当前候选目标路径
            attempt            INTEGER NOT NULL DEFAULT 0 CHECK (attempt >= 0),
            status             TEXT    NOT NULL CHECK (status IN ('Pending','Committed','RolledBack','Lost')),
            actual_target_path TEXT    NULL,                          -- Commit 时的实际落点
            abort_reason       TEXT    NULL,                          -- RollBack 原因（含 Win32 错误码）
            recovery_failures  INTEGER NOT NULL DEFAULT 0 CHECK (recovery_failures >= 0), -- 启动恢复连续失败次数
            created_at         TEXT    NOT NULL                       -- UTC，yyyy-MM-ddTHH:mm:ss.fffZ
        );
        CREATE INDEX ix_journal_status ON journal (status);
        -- 同一 file_id 同时最多一条 Pending（Begin 时冲突 → Skipped(AlreadyInProgress)）
        CREATE UNIQUE INDEX ux_journal_pending_file_id ON journal (file_id) WHERE status = 'Pending';

        CREATE TABLE vault_record (
            id            TEXT NOT NULL PRIMARY KEY,
            original_path TEXT NOT NULL COLLATE NOCASE,
            vault_path    TEXT NOT NULL COLLATE NOCASE,               -- 收纳库中的路径（不区分大小写唯一）
            file_id       TEXT NOT NULL,
            moved_at      TEXT NOT NULL,
            restored_at   TEXT NULL
        );
        -- 只约束在库中的记录：还原后同一路径可以再次收纳
        CREATE UNIQUE INDEX ux_vault_record_vault_path ON vault_record (vault_path) WHERE restored_at IS NULL;
        CREATE INDEX ix_vault_record_file_id ON vault_record (file_id);

        CREATE TABLE lost_file_notice (
            journal_entry_id TEXT NOT NULL PRIMARY KEY REFERENCES journal (id),
            file_id          TEXT NOT NULL,
            original_path    TEXT NOT NULL,
            detected_at      TEXT NOT NULL,
            acknowledged_at  TEXT NULL
        );
        CREATE INDEX ix_lost_file_notice_unack ON lost_file_notice (acknowledged_at);

        CREATE TABLE snapshot (
            id           TEXT NOT NULL PRIMARY KEY,
            trigger_kind TEXT NOT NULL CHECK (trigger_kind IN ('Manual','Auto','BeforeRestore')),
            created_at   TEXT NOT NULL
        );
        CREATE INDEX ix_snapshot_created_at ON snapshot (created_at);

        CREATE TABLE snapshot_entry (
            snapshot_id TEXT    NOT NULL REFERENCES snapshot (id) ON DELETE CASCADE,
            ordinal     INTEGER NOT NULL,
            file_id     TEXT    NOT NULL,
            file_path   TEXT    NOT NULL,
            fence_id    TEXT    NULL,
            x           INTEGER NOT NULL,
            y           INTEGER NOT NULL,
            PRIMARY KEY (snapshot_id, ordinal)
        );
        """,
    ];

    public static int LatestVersion => Migrations.Length;

    /// <summary>把数据库升级到最新版本，返回升级前的版本。</summary>
    public static async Task<int> MigrateAsync(IUnitOfWorkFactory db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var before = -1;
        while (true)
        {
            var uow = await db.BeginAsync(ct);
            await using (uow.ConfigureAwait(false))
            {
                var s = SqliteUnitOfWork.From(uow);
                // 读版本也在 BEGIN IMMEDIATE 里，两个进程同时迁移时第二个会等第一个提交后再看版本
                var version = Convert.ToInt32(await s.RunAsync("PRAGMA user_version;", c => c.ExecuteScalarAsync(ct)),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (before < 0) before = version;
                if (version > LatestVersion)
                    throw new InvalidOperationException($"数据库版本 {version} 高于程序支持的 {LatestVersion}");
                if (version == LatestVersion) return before;
                await s.ExecuteAsync(Migrations[version], ct);
                await s.ExecuteAsync($"PRAGMA user_version={version + 1};", ct);
                await uow.CommitAsync(ct);
            }
        }
    }
}

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
        // v2（TDD v1.0.8 §4.5.2）：方框。位置为 DIP、相对所属显示器工作区左上角，与显示器位置 / 缩放无关
        """
        CREATE TABLE fence (
            id         TEXT    NOT NULL PRIMARY KEY,                  -- 方框 ID（GUID）
            title      TEXT    NOT NULL,
            monitor_id TEXT    NOT NULL,                              -- 所属显示器指纹
            x          INTEGER NOT NULL,                              -- DIP，相对工作区左上角
            y          INTEGER NOT NULL,
            width      INTEGER NOT NULL CHECK (width  >= 120),        -- DIP
            height     INTEGER NOT NULL CHECK (height >= 120),
            dpi        INTEGER NOT NULL,                              -- 最后保存时所在显示器 DPI（仅记录）
            opacity    INTEGER NOT NULL,
            is_staging INTEGER NOT NULL CHECK (is_staging IN (0, 1)),
            sort_order INTEGER NOT NULL DEFAULT 0
        );
        """,
        // v3（TDD §4.7，任务 5）：折叠状态持久化；方框中的文件映射。改归属只改 fence_item，不动磁盘文件
        """
        ALTER TABLE fence ADD COLUMN collapsed INTEGER NOT NULL DEFAULT 0 CHECK (collapsed IN (0, 1)); -- 1 = 折叠

        CREATE TABLE fence_item (
            id                TEXT    NOT NULL PRIMARY KEY,                               -- 映射 ID（GUID）
            fence_id          TEXT    NOT NULL REFERENCES fence (id) ON DELETE CASCADE,   -- 所属方框；删方框只删映射
            file_path         TEXT    NOT NULL COLLATE NOCASE,                            -- 桌面上的真实路径
            file_id           TEXT    NOT NULL,                                           -- 卷序列号:128 位文件 ID（大写）
            is_public_desktop INTEGER NOT NULL DEFAULT 0 CHECK (is_public_desktop IN (0, 1)), -- 公共桌面项不能移入收纳库
            sort_order        INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0)
        );
        -- 一个文件只属于一个方框（TDD §7 第 1 条，不区分大小写）
        CREATE UNIQUE INDEX ux_fence_item_file_path ON fence_item (file_path);
        CREATE INDEX ix_fence_item_fence_id_sort ON fence_item (fence_id, sort_order);
        """,
        // v4（TDD §4.6 第 2.5 步）：最多一个暂存方框。旧库若已有多个：保留 sort_order 最小（同序取 id 最小）的那个，
        // 其余降为普通方框（is_staging=0），方框与其中文件映射都保留，不删任何数据
        """
        UPDATE fence SET is_staging = 0
        WHERE is_staging = 1
          AND id <> (SELECT id FROM fence WHERE is_staging = 1 ORDER BY sort_order, id LIMIT 1);
        CREATE UNIQUE INDEX ux_fence_single_staging ON fence (is_staging) WHERE is_staging = 1;
        """,
    ];

    public static int LatestVersion => Migrations.Length;

    /// <summary>把数据库升级到最新版本，返回升级前的版本。</summary>
    /// <summary>v4 迁移把 <paramref name="fenceId"/> 从暂存方框降为普通方框（旧库有多个暂存方框时，每个被降级的记一条）。</summary>
    public interface IMigrationLog
    {
        void StagingFenceDemoted(Guid fenceId, Guid keptStagingFenceId);
    }

    public static Task<int> MigrateAsync(IUnitOfWorkFactory db, CancellationToken ct = default) => MigrateAsync(db, null, ct);

    public static async Task<int> MigrateAsync(IUnitOfWorkFactory db, IMigrationLog? log, CancellationToken ct = default)
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
                // v4 降级前先在同一事务里读出将被降级的暂存方框，提交成功后再逐条记日志
                var demoted = version == 3 ? await StagingDuplicatesAsync(s, ct) : [];
                await s.ExecuteAsync(Migrations[version], ct);
                await s.ExecuteAsync($"PRAGMA user_version={version + 1};", ct);
                await uow.CommitAsync(ct);
                foreach (var (id, kept) in demoted) log?.StagingFenceDemoted(id, kept);
            }
        }
    }

    private static async Task<IReadOnlyList<(Guid Id, Guid Kept)>> StagingDuplicatesAsync(SqliteUnitOfWork s, CancellationToken ct)
    {
        var ids = await s.RunAsync("SELECT id FROM fence WHERE is_staging = 1 ORDER BY sort_order, id;", c => Sql.ListAsync(c, r => Sql.ParseId(r.GetString(0)), ct));
        return ids.Skip(1).Select(id => (id, ids[0])).ToList();
    }
}

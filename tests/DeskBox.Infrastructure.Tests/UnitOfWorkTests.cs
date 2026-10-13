using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace DeskBox.Infrastructure.Tests;

public sealed class UnitOfWorkTests : IAsyncLifetime
{
    private readonly TempDb _t = new();
    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private static JournalEntry Entry() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "1a2b3c4d:000000000000000000000000000000aa", @"C:\desk\a.txt", @"C:\vault\a.txt", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Every_connection_gets_required_pragmas()
    {
        for (var i = 0; i < 2; i++) // 每个新连接都要设置（连接级 PRAGMA 不会继承）
        {
            (await _t.Scalar<string>("PRAGMA journal_mode;")).Should().Be("wal");
            (await _t.Scalar<long>("PRAGMA synchronous;")).Should().Be(2, "FULL");
            (await _t.Scalar<long>("PRAGMA foreign_keys;")).Should().Be(1);
            (await _t.Scalar<long>("PRAGMA busy_timeout;")).Should().Be(SqlitePragmas.BusyTimeoutMs);
        }
        await using var open = await _t.Db.BeginAsync();
        File.Exists(_t.Path_ + "-wal").Should().BeTrue("连接打开期间存在 WAL 文件");
    }

    [Fact]
    public async Task Transactions_begin_immediate_and_take_the_write_lock_up_front()
    {
        await using var holder = await _t.Db.BeginAsync();
        // 另一个连接（不等待）在第一个工作单元尚未写任何东西时就拿不到写锁 → 证明是 BEGIN IMMEDIATE 而不是 DEFERRED
        await using var other = new SqliteConnection($"Data Source={_t.Path_};Pooling=False;Default Timeout=1");
        await other.OpenAsync();
        await using var cmd = other.CreateCommand();
        cmd.CommandText = "BEGIN IMMEDIATE;"; // Default Timeout=1：驱动层最多重试 1 秒
        var act = () => cmd.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<SqliteException>()).Which.SqliteErrorCode.Should().Be(5, "SQLITE_BUSY");
    }

    [Fact]
    public async Task Dispose_without_commit_rolls_back_and_commit_persists()
    {
        var journal = new SqliteJournal();
        var lost = Entry();
        await using (var u = await _t.Db.BeginAsync()) await journal.AppendPendingAsync(u, lost);
        var kept = Entry();
        await _t.Tx(u => journal.AppendPendingAsync(u, kept));

        var pending = await _t.Tx(u => journal.GetPendingAsync(u));
        pending.Select(e => e.Id).Should().Equal(kept.Id);
    }

    [Fact]
    public async Task Exception_inside_transaction_rolls_back_all_writes()
    {
        var journal = new SqliteJournal();
        var e = Entry();
        var act = () => _t.Tx(async u =>
        {
            await journal.AppendPendingAsync(u, e);
            await journal.AppendPendingAsync(u, JournalEntry.Restore(e.Id, e.JobId, "1a2b3c4d:000000000000000000000000000000bb",
                e.SourcePath, e.TargetPath, e.CreatedAt, 0, JournalStatus.Pending, null, null, 0)); // 主键冲突
        });
        (await act.Should().ThrowAsync<SqliteException>()).Which.SqliteErrorCode.Should().Be(19);
        (await _t.Scalar<long>("SELECT COUNT(*) FROM journal;")).Should().Be(0);
    }

    [Fact]
    public async Task Completed_unit_of_work_cannot_be_reused()
    {
        var u = await _t.Db.BeginAsync();
        await u.CommitAsync();
        await FluentActions.Awaiting(() => u.CommitAsync()).Should().ThrowAsync<InvalidOperationException>();
        FluentActions.Invoking(() => SqliteUnitOfWork.From(u)).Should().Throw<InvalidOperationException>();
        ((SqliteUnitOfWork)u).IsCompleted.Should().BeTrue();
        await u.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_use_of_one_unit_of_work_is_rejected()
    {
        await using var u = await _t.Db.BeginAsync();
        var s = SqliteUnitOfWork.From(u);
        var gate = new TaskCompletionSource<int>();
        var first = s.RunAsync("SELECT 1;", _ => gate.Task);
        await FluentActions.Awaiting(() => s.RunAsync("SELECT 2;", c => c.ExecuteScalarAsync()))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*并发*");
        gate.SetResult(1);
        (await first).Should().Be(1);
        (await s.RunAsync("SELECT 3;", c => c.ExecuteScalarAsync())).Should().Be(3L);
    }

    [Fact]
    public async Task Foreign_unit_of_work_types_are_rejected()
    {
        FluentActions.Invoking(() => SqliteUnitOfWork.From(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SqliteUnitOfWork.From(new OtherUow())).Should().Throw<ArgumentException>().WithMessage("*OtherUow*");
        await FluentActions.Awaiting(() => new SqliteJournal().GetPendingAsync(new OtherUow())).Should().ThrowAsync<ArgumentException>();
    }

    private sealed class OtherUow : IUnitOfWork
    {
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Factory_guards_and_connection_string()
    {
        FluentActions.Invoking(() => new SqliteUnitOfWorkFactory(" ")).Should().Throw<ArgumentException>();
        _t.Db.ConnectionString.Should().Contain("Pooling=False");
    }

    [Fact]
    public async Task Open_failure_disposes_connection_and_throws()
    {
        var bad = new SqliteUnitOfWorkFactory(Path.Combine(_t.Dir, "missing-dir", "x.db"));
        await FluentActions.Awaiting(() => bad.BeginAsync()).Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Pragmas_reject_null_and_non_wal_databases()
    {
        await FluentActions.Awaiting(() => SqlitePragmas.ApplyAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        await using var mem = new SqliteConnection("Data Source=:memory:");
        await mem.OpenAsync();
        await FluentActions.Awaiting(() => SqlitePragmas.ApplyAsync(mem)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*WAL*");
    }

    [Fact]
    public async Task Migration_is_idempotent_and_refuses_newer_databases()
    {
        (await SqliteSchema.MigrateAsync(_t.Db)).Should().Be(SqliteSchema.LatestVersion);
        (await _t.Scalar<long>("PRAGMA user_version;")).Should().Be(SqliteSchema.LatestVersion);
        await _t.Tx(u => SqliteUnitOfWork.From(u).ExecuteAsync("PRAGMA user_version=99;", default));
        await FluentActions.Awaiting(() => SqliteSchema.MigrateAsync(_t.Db)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => SqliteSchema.MigrateAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Fresh_database_migrates_from_zero()
    {
        var db = new SqliteUnitOfWorkFactory(Path.Combine(_t.Dir, "fresh.db"));
        (await SqliteSchema.MigrateAsync(db)).Should().Be(0);
        (await SqliteSchema.MigrateAsync(db)).Should().Be(SqliteSchema.LatestVersion);
    }
}

public sealed class SchemaV2Tests : IAsyncLifetime
{
    private readonly TempDb _t = new();
    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task V1_database_upgrades_to_v2_and_gains_fence_table()
    {
        var db = new SqliteUnitOfWorkFactory(Path.Combine(_t.Dir, "v1.db"));
        await db.InTransactionAsync(async u =>
        {
            var s = SqliteUnitOfWork.From(u);
            await s.ExecuteAsync(SqliteSchema.Migrations[0], default);
            await s.ExecuteAsync("PRAGMA user_version=1;", default);
        });
        (await SqliteSchema.MigrateAsync(db)).Should().Be(1);
        SqliteSchema.LatestVersion.Should().Be(4);
        (await db.InTransactionAsync(u => SqliteUnitOfWork.From(u).RunAsync("SELECT COUNT(*) FROM sqlite_master WHERE name='fence';", c => c.ExecuteScalarAsync())))
            .Should().Be(1L);
    }

    [Fact]
    public async Task V2_database_with_fences_upgrades_to_v3_keeps_rows_and_adds_collapsed_and_fence_item()
    {
        var db = new SqliteUnitOfWorkFactory(Path.Combine(_t.Dir, "v2.db"));
        await db.InTransactionAsync(async u =>
        {
            var s = SqliteUnitOfWork.From(u);
            await s.ExecuteAsync(SqliteSchema.Migrations[0], default);
            await s.ExecuteAsync(SqliteSchema.Migrations[1], default);
            await s.ExecuteAsync("PRAGMA user_version=2;", default);
            await s.ExecuteAsync("INSERT INTO fence (id,title,monitor_id,x,y,width,height,dpi,opacity,is_staging,sort_order) " +
                "VALUES ('6f1c3c1e-0000-0000-0000-000000000001','旧','m',1,2,200,200,96,80,1,0);", default);
        });
        (await SqliteSchema.MigrateAsync(db)).Should().Be(2);
        var fence = (await db.InTransactionAsync(u => new SqliteFenceStore().ListAsync(u))).Single();
        (fence.Title, fence.IsStaging, fence.IsCollapsed).Should().Be(("旧", true, false), "旧行 collapsed 默认 0");
        (await db.InTransactionAsync(u => SqliteUnitOfWork.From(u).RunAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('fence_item','ux_fence_item_file_path','ix_fence_item_fence_id_sort');",
            c => c.ExecuteScalarAsync()))).Should().Be(3L);
        (await SqliteSchema.MigrateAsync(db)).Should().Be(SqliteSchema.LatestVersion, "重复调用不再迁移");
        await FluentActions.Awaiting(() => db.InTransactionAsync(u => SqliteUnitOfWork.From(u).ExecuteAsync(
            "UPDATE fence SET collapsed = 2;", default))).Should().ThrowAsync<SqliteException>("collapsed 只能是 0/1");
    }

    private static async Task<SqliteUnitOfWorkFactory> V3Async(string dir, params (string Id, int Sort, int Staging)[] rows)
    {
        var db = new SqliteUnitOfWorkFactory(Path.Combine(dir, $"v3-{Guid.NewGuid():N}.db"));
        await db.InTransactionAsync(async u =>
        {
            var s = SqliteUnitOfWork.From(u);
            for (var i = 0; i < 3; i++) await s.ExecuteAsync(SqliteSchema.Migrations[i], default);
            await s.ExecuteAsync("PRAGMA user_version=3;", default);
            foreach (var (id, sort, staging) in rows)
                await s.ExecuteAsync("INSERT INTO fence (id,title,monitor_id,x,y,width,height,dpi,opacity,is_staging,sort_order) " +
                    $"VALUES ('{id}','t','m',0,0,200,200,96,80,{staging},{sort});", default);
        });
        return db;
    }

    private static Task<long> Count(SqliteUnitOfWorkFactory db, string sql) =>
        db.InTransactionAsync(async u => (long)(await SqliteUnitOfWork.From(u).RunAsync(sql, c => c.ExecuteScalarAsync()))!);

    [Fact]
    public async Task V4_adds_partial_unique_index_allowing_one_staging_and_many_normal()
    {
        var db = await V3Async(_t.Dir, ("00000000-0000-0000-0000-000000000001", 0, 1), ("00000000-0000-0000-0000-000000000002", 1, 0));
        (await SqliteSchema.MigrateAsync(db)).Should().Be(3);
        (await Count(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_fence_single_staging';")).Should().Be(1);
        await FluentActions.Awaiting(() => db.InTransactionAsync(u => SqliteUnitOfWork.From(u).ExecuteAsync(
            "INSERT INTO fence (id,title,monitor_id,x,y,width,height,dpi,opacity,is_staging) VALUES ('x','t','m',0,0,200,200,96,80,1);", default)))
            .Should().ThrowAsync<SqliteException>("最多一个暂存方框");
        await db.InTransactionAsync(u => SqliteUnitOfWork.From(u).ExecuteAsync(
            "INSERT INTO fence (id,title,monitor_id,x,y,width,height,dpi,opacity,is_staging) VALUES ('y','t','m',0,0,200,200,96,80,0);", default));
        (await Count(db, "SELECT COUNT(*) FROM fence WHERE is_staging = 0;")).Should().Be(2, "普通方框不受限制");
    }

    [Fact]
    public async Task V4_demotes_duplicate_staging_fences_keeping_lowest_sort_order_and_all_rows()
    {
        var db = await V3Async(_t.Dir,
            ("00000000-0000-0000-0000-00000000000b", 2, 1),
            ("00000000-0000-0000-0000-00000000000a", 1, 1),
            ("00000000-0000-0000-0000-00000000000c", 1, 1));
        await db.InTransactionAsync(u => SqliteUnitOfWork.From(u).ExecuteAsync(
            "INSERT INTO fence_item (id,fence_id,file_path,file_id) VALUES ('i','00000000-0000-0000-0000-00000000000b','C:\\d\\x','1A2B3C4D:000000000000000000000000000000AA');", default));
        await SqliteSchema.MigrateAsync(db);
        (await Count(db, "SELECT COUNT(*) FROM fence;")).Should().Be(3, "不删方框");
        (await Count(db, "SELECT COUNT(*) FROM fence_item;")).Should().Be(1, "不删映射");
        (await db.InTransactionAsync(async u => (string)(await SqliteUnitOfWork.From(u).RunAsync(
            "SELECT id FROM fence WHERE is_staging = 1;", c => c.ExecuteScalarAsync()))!))
            .Should().Be("00000000-0000-0000-0000-00000000000a", "sort_order 最小，同序取 id 最小");
    }

    [Fact]
    public async Task V4_on_database_without_staging_is_a_no_op_for_rows()
    {
        var db = await V3Async(_t.Dir, ("00000000-0000-0000-0000-000000000001", 0, 0));
        await SqliteSchema.MigrateAsync(db);
        (await Count(db, "SELECT COUNT(*) FROM fence WHERE is_staging = 1;")).Should().Be(0, "补建由启动自检负责，迁移不建");
    }
}

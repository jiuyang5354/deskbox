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
        (await SqliteSchema.MigrateAsync(db)).Should().Be(1);
    }
}

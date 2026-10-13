using System.Data;
using DeskBox.Application.Ports;
using Microsoft.Data.Sqlite;

namespace DeskBox.Infrastructure.Sqlite;

/// <summary>
/// 一个工作单元 = 一个独占的 SQLite 连接 + 一个 BEGIN IMMEDIATE 事务（TDD v1.0.6 §4.4）。
/// 连接不在线程或工作单元之间共享，也不放回连接池；未调用 <see cref="CommitAsync"/> 就释放 = 回滚。
/// 仓储只能通过 <see cref="From"/> 拿到连接与事务，不得自行打开连接。
/// </summary>
public sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private int _inUse;

    public bool IsCompleted { get; private set; }

    private SqliteUnitOfWork(SqliteConnection connection, SqliteTransaction transaction) =>
        (_connection, _transaction) = (connection, transaction);

    internal static async Task<SqliteUnitOfWork> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new SqliteConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await SqlitePragmas.ApplyAsync(conn, ct);
            // deferred: false → BEGIN IMMEDIATE：开事务时就拿写锁，busy_timeout 在这里等待，避免事务中途升级锁时 SQLITE_BUSY
            var tx = conn.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            return new SqliteUnitOfWork(conn, tx);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>仓储取得当前事务上的命令。不是本类型或已结束的工作单元会抛异常。</summary>
    public static SqliteUnitOfWork From(IUnitOfWork uow)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var s = uow as SqliteUnitOfWork
            ?? throw new ArgumentException($"需要 {nameof(SqliteUnitOfWork)}，实际为 {uow.GetType().Name}", nameof(uow));
        if (s.IsCompleted) throw new InvalidOperationException("工作单元已提交或已释放");
        return s;
    }

    internal SqliteCommand Command(string sql)
    {
        if (IsCompleted) throw new InvalidOperationException("工作单元已提交或已释放");
        var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    /// <summary>防止同一工作单元被并发使用（例如两个线程同时执行命令）。</summary>
    internal async Task<T> RunAsync<T>(string sql, Func<SqliteCommand, Task<T>> body, params (string Name, object? Value)[] args)
    {
        if (Interlocked.Exchange(ref _inUse, 1) == 1)
            throw new InvalidOperationException("同一工作单元不能被并发使用");
        try
        {
            await using var cmd = Command(sql);
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return await body(cmd);
        }
        finally { Volatile.Write(ref _inUse, 0); }
    }

    internal Task<int> ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] args) =>
        RunAsync(sql, c => c.ExecuteNonQueryAsync(ct), args);

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (IsCompleted) throw new InvalidOperationException("工作单元已提交或已释放");
        await _transaction.CommitAsync(ct);
        IsCompleted = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!IsCompleted)
            {
                IsCompleted = true;
                await _transaction.RollbackAsync();
            }
        }
        finally
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}

/// <summary>每个工作单元打开一个新连接；连接池关闭，保证连接不会被别的工作单元或线程复用。</summary>
public sealed class SqliteUnitOfWorkFactory : IUnitOfWorkFactory
{
    public string ConnectionString { get; }

    public SqliteUnitOfWorkFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
    }

    public async Task<IUnitOfWork> BeginAsync(CancellationToken ct = default) =>
        await SqliteUnitOfWork.OpenAsync(ConnectionString, ct);
}

/// <summary>每次打开连接都执行；journal_mode=WAL 写入文件头，其余是连接级设置。</summary>
public static class SqlitePragmas
{
    public const int BusyTimeoutMs = 5000;

    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // busy_timeout 先设，后面切换 WAL 时若有别的进程持锁也会等待
        await ExecAsync(connection, $"PRAGMA busy_timeout={BusyTimeoutMs};", ct);
        var mode = (string?)await ScalarAsync(connection, "PRAGMA journal_mode=WAL;", ct);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"无法启用 WAL（journal_mode={mode}）");
        await ExecAsync(connection, "PRAGMA synchronous=FULL;", ct);
        await ExecAsync(connection, "PRAGMA foreign_keys=ON;", ct);
    }

    private static async Task ExecAsync(SqliteConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }
}

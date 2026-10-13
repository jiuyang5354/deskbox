using DeskBox.Application.Ports;
using DeskBox.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace DeskBox.Infrastructure.Tests;

/// <summary>每个测试一个真实的 SQLite 临时文件（WAL 模式会生成 -wal / -shm）。</summary>
public sealed class TempDb : IAsyncLifetime
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "deskbox-tests", Guid.NewGuid().ToString("N"));
    public string Path_ => Path.Combine(Dir, "deskbox.db");
    public SqliteUnitOfWorkFactory Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Dir);
        Db = new SqliteUnitOfWorkFactory(Path_);
        await SqliteSchema.MigrateAsync(Db);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Dir, recursive: true);
        return Task.CompletedTask;
    }

    public Task<T> Tx<T>(Func<IUnitOfWork, Task<T>> work) => Db.InTransactionAsync(work);
    public Task Tx(Func<IUnitOfWork, Task> work) => Db.InTransactionAsync(work);

    public Task<T> Scalar<T>(string sql) => Tx(async u =>
    {
        var v = await SqliteUnitOfWork.From(u).RunAsync(sql, c => c.ExecuteScalarAsync());
        return (T)Convert.ChangeType(v!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    });
}

/// <summary>仅在 Windows 上运行的事实测试（其他系统标记为跳过）。</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "需要 Windows";
    }
}

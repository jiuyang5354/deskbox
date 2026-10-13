using DeskBox.Application.Ports;
using DeskBox.Application.Startup;

namespace DeskBox.Infrastructure.Sqlite;

/// <summary>启动第 2 步：确保数据库目录存在，然后 <see cref="SqliteSchema.MigrateAsync"/>（版本高于程序时抛出，拒绝启动）。</summary>
public sealed class SqliteDatabaseBootstrapper(IUnitOfWorkFactory db, string databasePath) : IDatabaseBootstrapper
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (Path.GetDirectoryName(Path.GetFullPath(databasePath)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        await SqliteSchema.MigrateAsync(db, ct);
    }
}

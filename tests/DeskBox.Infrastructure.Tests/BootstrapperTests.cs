using DeskBox.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace DeskBox.Infrastructure.Tests;

public sealed class BootstrapperTests
{
    [Fact]
    public async Task Creates_missing_directory_and_migrates_to_latest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskbox-tests", Guid.NewGuid().ToString("N"), "nested");
        var path = Path.Combine(dir, "deskbox.db");
        try
        {
            var db = new SqliteUnitOfWorkFactory(path);
            var sut = new SqliteDatabaseBootstrapper(db, path);
            await sut.MigrateAsync();
            await sut.MigrateAsync(); // 可重复调用
            File.Exists(path).Should().BeTrue();
            var version = await DeskBox.Application.Ports.UnitOfWorkExtensions.InTransactionAsync(db, async u =>
                Convert.ToInt32(await SqliteUnitOfWork.From(u).RunAsync("PRAGMA user_version;", c => c.ExecuteScalarAsync()),
                    System.Globalization.CultureInfo.InvariantCulture));
            version.Should().Be(SqliteSchema.LatestVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [Fact]
    public async Task Rejects_null_arguments()
    {
        await FluentActions.Awaiting(() => new SqliteDatabaseBootstrapper(null!, "x.db").MigrateAsync()).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => new SqliteDatabaseBootstrapper(new SqliteUnitOfWorkFactory("x.db"), " ").MigrateAsync())
            .Should().ThrowAsync<ArgumentException>();
    }
}

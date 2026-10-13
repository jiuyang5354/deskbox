using System.Globalization;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Infrastructure.Sqlite;

namespace DeskBox.SqliteWriter;

/// <summary>双进程并发写测试的写入逻辑；子进程和测试进程各跑一份。</summary>
public static class Writer
{
    /// <summary>每条两个事务：Append Pending；写 vault_record + Commit。</summary>
    public static async Task RunAsync(string databasePath, int count, string volumeHex8)
    {
        var db = new SqliteUnitOfWorkFactory(databasePath);
        var journal = new SqliteJournal();
        var records = new SqliteVaultRecordStore();
        for (var i = 0; i < count; i++)
        {
            var fileId = string.Create(CultureInfo.InvariantCulture, $"{volumeHex8}:{i:X32}");
            var e = new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), fileId, $@"C:\desk\{volumeHex8}-{i}.txt",
                $@"C:\vault\{volumeHex8}-{i}.txt", DateTimeOffset.UtcNow);
            await db.InTransactionAsync(u => journal.AppendPendingAsync(u, e));
            await db.InTransactionAsync(async u =>
            {
                await records.AddAsync(u, new VaultRecord(Guid.NewGuid(), e.SourcePath, e.TargetPath, e.FileId, DateTimeOffset.UtcNow));
                await journal.CommitAsync(u, e.Id, e.TargetPath);
            });
        }
    }

    /// <summary>用法：DeskBox.SqliteWriter &lt;数据库路径&gt; &lt;条数&gt; &lt;8 位十六进制卷号&gt;。任何异常 → 退出码 1。</summary>
    public static async Task<int> Main(string[] args)
    {
        if (args is not [var path, var n, var vol]) { await Console.Error.WriteLineAsync("usage: <db> <count> <volume-hex8>"); return 2; }
        try
        {
            await RunAsync(path, int.Parse(n, CultureInfo.InvariantCulture), vol);
            Console.WriteLine($"ok {n}");
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Console.Error.WriteLineAsync(ex.ToString());
            return 1;
        }
    }
}

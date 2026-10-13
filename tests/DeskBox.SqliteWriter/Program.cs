using System.Globalization;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Domain.Files;
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
    /// <summary>
    /// begin 模式：等 go 文件出现后对同一 file_id 执行 JournaledMove 的 Begin。
    /// 抢到的一方在第一次原生移动前打印 "attempt" 并一直挂起（由测试进程杀掉，留下 Pending）；
    /// 另一方打印 "skipped AlreadyInProgress" 正常退出。
    /// </summary>
    public static async Task<int> BeginAsync(string databasePath, string fileId, string goFile)
    {
        var db = new SqliteUnitOfWorkFactory(databasePath);
        var sut = new JournaledMove(db, new HangingMover(), new SqliteJournal(), new FixedIdentity(fileId), new NoLog(), TimeProvider.System);
        await db.InTransactionAsync(_ => Task.CompletedTask); // 先确认数据库能打开，再等 go
        Console.WriteLine("ready");
        while (!File.Exists(goFile)) await Task.Delay(2);
        var r = await sut.ExecuteAsync(Guid.NewGuid(), @"C:\desk\same.txt", @"C:\vault\same.txt");
        Console.WriteLine(r is MoveResult.Skipped s ? $"skipped {s.Reason}" : "unexpected");
        return 0;
    }

    private sealed class FixedIdentity(string id) : IFileIdentity { public string? TryGetFileId(string fullPath) => id; }

    private sealed class NoLog : IRecoveryLog
    {
        public void UncertainPending(UncertainPendingEvent e) => Console.Error.WriteLine($"uncertain {e.EntryId}");
        public void RecoveryFailed(RecoveryFailedEvent e) => Console.Error.WriteLine($"failed {e.EntryId}");
    }

    private sealed class HangingMover : IFileMover
    {
        public async Task<MoveResult> MoveAsync(string sourcePath, string targetPath,
            Func<string, CancellationToken, Task>? beforeAttempt, CancellationToken ct = default)
        {
            if (beforeAttempt is not null) await beforeAttempt(targetPath, ct);
            Console.WriteLine("attempt");
            await Task.Delay(Timeout.Infinite, ct);
            return new MoveResult.Moved(sourcePath, targetPath);
        }
    }

    public static async Task<int> Main(string[] args)
    {
        if (args is ["begin", var bdb, var fid, var go])
        {
            try { return await BeginAsync(bdb, fid, go); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await Console.Error.WriteLineAsync(ex.ToString());
                return 1;
            }
        }
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

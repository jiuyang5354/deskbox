using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;

namespace DeskBox.Application.Files;

public enum RecoveryOutcome { Aborted, CommittedAtCandidate, CommittedFromVaultScan, Lost, Failed }

public sealed record RecoveredEntry(Guid EntryId, RecoveryOutcome Outcome, string? FinalPath);

/// <summary>
/// 启动时（嵌入桌面层之前）按 file_id 处理所有 Pending（v1.0.5）：
/// 源路径仍是该文件 → RollBack；候选路径是该文件 → vault_record + Commit；
/// 否则扫描收纳库按 file_id 查找；仍找不到 → 提示 + MarkLost。
/// 每条记录的"写业务表 + 改 Journal 状态"在同一个 SQLite 事务里提交，中途崩溃要么都没发生、要么都完成，重跑幂等。
/// 每条记录单独隔离异常：失败的保持 Pending、写结构化日志、继续下一条；同一条连续 3 次启动失败 → 提示 + MarkLost（同一事务）。
/// </summary>
public sealed class JournalRecovery(IUnitOfWorkFactory db, IJournal journal, IFileIdentity identity, IVaultIndex vault,
    IVaultRecordStore vaultRecords, ILostFileNoticeStore notices, IRecoveryLog log, TimeProvider clock) : Startup.IStartupRecovery
{
    public async Task<IReadOnlyList<RecoveredEntry>> RecoverAsync(CancellationToken ct = default)
    {
        var pending = await db.InTransactionAsync(u => journal.GetPendingAsync(u, ct), ct);
        var results = new List<RecoveredEntry>(pending.Count);
        foreach (var e in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add(await RecoverOneAsync(e, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(await HandleFailureAsync(e, ex, ct));
            }
        }
        return results;
    }

    private async Task<RecoveredEntry> RecoverOneAsync(JournalEntry e, CancellationToken ct)
    {
        if (Is(e.SourcePath, e.FileId))
        {
            await db.InTransactionAsync(u => journal.RollBackAsync(u, e.Id, "Recovered: source intact", ct), ct);
            return new(e.Id, RecoveryOutcome.Aborted, e.SourcePath);
        }
        if (Is(e.TargetPath, e.FileId))
        {
            await CommitAsync(e, e.TargetPath, ct);
            return new(e.Id, RecoveryOutcome.CommittedAtCandidate, e.TargetPath);
        }
        if (vault.EnumerateFiles().FirstOrDefault(p => Is(p, e.FileId)) is { } found)
        {
            await CommitAsync(e, found, ct);
            return new(e.Id, RecoveryOutcome.CommittedFromVaultScan, found);
        }
        await db.InTransactionAsync(u => MarkLostAsync(u, e, ct), ct);
        return new(e.Id, RecoveryOutcome.Lost, null);
    }

    private Task CommitAsync(JournalEntry e, string path, CancellationToken ct) =>
        db.InTransactionAsync(async u =>
        {
            await vaultRecords.AddAsync(u, new VaultRecord(Guid.NewGuid(), e.SourcePath, path, e.FileId, clock.GetUtcNow()), ct);
            await journal.CommitAsync(u, e.Id, path, ct);
        }, ct);

    private async Task MarkLostAsync(IUnitOfWork u, JournalEntry e, CancellationToken ct)
    {
        await notices.AddAsync(u, new LostFileNotice(e.Id, e.FileId, e.SourcePath, clock.GetUtcNow()), ct);
        await journal.MarkLostAsync(u, e.Id, ct);
    }

    private async Task<RecoveredEntry> HandleFailureAsync(JournalEntry e, Exception ex, CancellationToken ct)
    {
        var failures = 0;
        var lost = false;
        Exception? secondary = null;
        try
        {
            (failures, lost) = await db.InTransactionAsync(async u =>
            {
                var n = await journal.RecordRecoveryFailureAsync(u, e.Id, ct);
                if (n < JournalEntry.MaxRecoveryFailures) return (n, false);
                await MarkLostAsync(u, e, ct);
                return (n, true);
            }, ct);
        }
        catch (Exception inner) when (inner is not OperationCanceledException)
        {
            // 连失败计数都写不进去：数据保持原样，下次启动再试；两个异常都写进日志
            secondary = inner;
        }
        log.RecoveryFailed(new RecoveryFailedEvent(e.Id, e.JobId, e.FileId, e.SourcePath, e.TargetPath,
            failures, lost, ex.GetType().Name, ex.Message, secondary?.GetType().Name, secondary?.Message));
        return new(e.Id, lost ? RecoveryOutcome.Lost : RecoveryOutcome.Failed, null);
    }

    private bool Is(string path, string fileId) =>
        FileIdEquals(identity.TryGetFileId(path), fileId);

    /// <summary>file_id 是 "卷序列号:128 位 ID" 的十六进制串，按序号不区分大小写比较（不是路径，不走 WinPath）。</summary>
    internal static bool FileIdEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

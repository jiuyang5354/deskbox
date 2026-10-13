using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;

namespace DeskBox.Application.Files;

/// <summary>
/// Journal 包裹的移动：
/// <list type="number">
/// <item>先按属性位判断仅云端文件 → Skipped(CloudOnly)，不打开文件。</item>
/// <item>Begin：读取源文件 file_id，同一 BEGIN IMMEDIATE 事务里查重并写 Pending（读不到 file_id 则不移动，返回 NotFound；已有 Pending 返回 AlreadyInProgress）。</item>
/// <item>每次 MoveFileExW 之前把候选目标写入 Pending 并提交（重试时更新）。Pending 行在 MoveFileExW 之前已提交落盘；MoveFileExW 本身在任何事务之外执行。</item>
/// <item>成功 → 标记完成（journal status Pending→Committed，记录实际路径，在移动之后的独立事务中）；跳过 → 独立事务中 RollBack(原因)。移动后、标记完成前崩溃由 JournalRecovery 按 file_id 在候选处补做。</item>
/// <item>取消或异常（含探测异常）：能证明未移动（尚未发起尝试，或源文件 file_id 仍在原处而候选处不是它）→ 立即 RollBack；
/// 无法证明 → 保留 Pending 交给启动恢复，并写结构化日志。异常继续抛出。</item>
/// </list>
/// </summary>
public sealed class JournaledMove(IUnitOfWorkFactory db, IFileMover mover, IJournal journal, IFileIdentity identity,
    IFileSystemProbe probe, IRecoveryLog log, TimeProvider clock)
{
    public async Task<MoveResult> ExecuteAsync(Guid jobId, string source, string target, CancellationToken ct = default)
    {
        // 仅云端文件只看属性位就跳过：读 file_id 要打开句柄，可能触发 OneDrive 下载，所以必须排在它前面
        if (probe.GetAttributes(source) is { } attributes && Win32FileAttributes.IsCloudOnly(attributes))
            return new MoveResult.Skipped(source, SkipReason.CloudOnly);
        var fileId = identity.TryGetFileId(source);
        if (fileId is null) return new MoveResult.Skipped(source, SkipReason.NotFound);

        var entry = new JournalEntry(Guid.NewGuid(), jobId, fileId, source, target, clock.GetUtcNow());
        // 同一个 BEGIN IMMEDIATE 事务里先查后写：写锁已持有，两个进程同时 Begin 同一文件时只有一个能写入 Pending
        var started = await db.InTransactionAsync(async u =>
        {
            if (await journal.HasPendingAsync(u, entry.FileId, ct)) return false;
            await journal.AppendPendingAsync(u, entry, ct);
            return true;
        }, ct);
        // 同一文件已有进行中的整理：不移动、不写日志、不碰那条 Pending
        if (!started) return new MoveResult.Skipped(source, SkipReason.AlreadyInProgress);

        var attemptStarted = false;
        MoveResult result;
        try
        {
            result = await mover.MoveAsync(source, target, async (candidate, c) =>
            {
                entry.BeginAttempt(candidate);
                await db.InTransactionAsync(u => journal.UpdateCandidateAsync(u, entry.Id, candidate, entry.Attempt, c), c);
                attemptStarted = true;
            }, ct);
        }
        catch (Exception ex)
        {
            await HandleInterruptedAsync(entry, attemptStarted, ex);
            throw;
        }

        if (result is MoveResult.Moved moved) await db.InTransactionAsync(u => journal.CommitAsync(u, entry.Id, moved.TargetPath, ct), ct);
        else if (result is MoveResult.Skipped skipped) await db.InTransactionAsync(u => journal.RollBackAsync(u, entry.Id, skipped.AbortReason, ct), ct);
        return result;
    }

    private async Task HandleInterruptedAsync(JournalEntry entry, bool attemptStarted, Exception ex)
    {
        var kind = ex is OperationCanceledException ? "Cancelled" : $"Interrupted ({ex.GetType().Name})";
        if (!attemptStarted)
        {
            await RollBackAsync(entry.Id, kind);
            return;
        }
        if (Matches(entry.SourcePath, entry.FileId) && !Matches(entry.TargetPath, entry.FileId))
        {
            await RollBackAsync(entry.Id, kind + ", verified not moved");
            return;
        }
        log.UncertainPending(new UncertainPendingEvent(entry.Id, entry.JobId, entry.FileId, entry.SourcePath,
            entry.TargetPath, entry.Attempt, ex.GetType().Name, ex.Message));
    }

    private Task RollBackAsync(Guid id, string reason) =>
        db.InTransactionAsync(u => journal.RollBackAsync(u, id, reason, CancellationToken.None), CancellationToken.None);

    private bool Matches(string path, string fileId)
    {
        try { return string.Equals(identity.TryGetFileId(path), fileId, StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

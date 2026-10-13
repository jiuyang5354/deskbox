using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;

namespace DeskBox.Application.Files;

/// <summary>
/// Journal 包裹的移动：
/// <list type="number">
/// <item>Begin：读取源文件 file_id，写 Pending（读不到 file_id 则不移动，返回 NotFound）。</item>
/// <item>每次 MoveFileExW 之前把候选目标写入 Pending（重试时更新）。</item>
/// <item>成功 → Commit(实际路径)；跳过 → RollBack(原因)。</item>
/// <item>取消或异常（含探测异常）：能证明未移动（尚未发起尝试，或源文件 file_id 仍在原处而候选处不是它）→ 立即 RollBack；
/// 无法证明 → 保留 Pending 交给启动恢复，并写结构化日志。异常继续抛出。</item>
/// </list>
/// </summary>
public sealed class JournaledMove(IUnitOfWorkFactory db, IFileMover mover, IJournal journal, IFileIdentity identity, IRecoveryLog log, TimeProvider clock)
{
    public async Task<MoveResult> ExecuteAsync(Guid jobId, string source, string target, CancellationToken ct = default)
    {
        var fileId = identity.TryGetFileId(source);
        if (fileId is null) return new MoveResult.Skipped(source, SkipReason.NotFound);

        var entry = new JournalEntry(Guid.NewGuid(), jobId, fileId, source, target, clock.GetUtcNow());
        try
        {
            await db.InTransactionAsync(u => journal.AppendPendingAsync(u, entry, ct), ct);
        }
        catch (JournalConflictException)
        {
            // 同一文件已有 Pending：本次什么都没写（事务已回滚），也不动那条 Pending
            return new MoveResult.Skipped(source, SkipReason.AlreadyInProgress);
        }

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

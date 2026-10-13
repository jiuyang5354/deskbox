using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;

namespace DeskBox.Application.Files;

/// <summary>Journal 包裹的移动：先写 Pending，成功后以实际落盘路径 Commit，跳过则 RollBack。</summary>
public sealed class JournaledMove(IFileMover mover, IJournal journal, TimeProvider clock)
{
    public async Task<MoveResult> ExecuteAsync(Guid jobId, string source, string target, CancellationToken ct = default)
    {
        var entry = new JournalEntry(Guid.NewGuid(), jobId, source, target, clock.GetUtcNow());
        await journal.AppendPendingAsync(entry, ct);
        var result = await mover.MoveAsync(source, target, ct);
        if (result is MoveResult.Moved moved) await journal.CommitAsync(entry.Id, moved.TargetPath, ct);
        else await journal.RollBackAsync(entry.Id, ct);
        return result;
    }
}

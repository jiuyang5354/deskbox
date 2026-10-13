using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;

namespace DeskBox.Application.Files;

public enum RecoveryOutcome { Aborted, CommittedAtCandidate, CommittedFromVaultScan, Lost }

public sealed record RecoveredEntry(Guid EntryId, RecoveryOutcome Outcome, string? FinalPath);

/// <summary>
/// 启动时（嵌入桌面层之前）按 file_id 处理所有 Pending：
/// 源路径仍是该文件 → RollBack（Abort）；候选路径是该文件 → 写 vault_record + Commit；
/// 否则扫描收纳库按 file_id 查找（同名但 file_id 不同的已有文件不会被误认）；
/// 仍找不到 → MarkLost，并写入持久的 LostFileNotice，直到用户确认。
/// </summary>
public sealed class JournalRecovery(IJournal journal, IFileIdentity identity, IVaultIndex vault,
    IVaultRecordStore vaultRecords, ILostFileNoticeStore notices, TimeProvider clock)
{
    public async Task<IReadOnlyList<RecoveredEntry>> RecoverAsync(CancellationToken ct = default)
    {
        var results = new List<RecoveredEntry>();
        foreach (var e in await journal.GetPendingAsync(ct))
        {
            if (Is(e.SourcePath, e.FileId))
            {
                await journal.RollBackAsync(e.Id, "Recovered: source intact", ct);
                results.Add(new(e.Id, RecoveryOutcome.Aborted, e.SourcePath));
            }
            else if (Is(e.TargetPath, e.FileId))
            {
                await CommitAsync(e, e.TargetPath, ct);
                results.Add(new(e.Id, RecoveryOutcome.CommittedAtCandidate, e.TargetPath));
            }
            else if (vault.EnumerateFiles().FirstOrDefault(p => Is(p, e.FileId)) is { } found)
            {
                await CommitAsync(e, found, ct);
                results.Add(new(e.Id, RecoveryOutcome.CommittedFromVaultScan, found));
            }
            else
            {
                await journal.MarkLostAsync(e.Id, ct);
                await notices.AddAsync(new LostFileNotice(e.Id, e.FileId, e.SourcePath, clock.GetUtcNow()), ct);
                results.Add(new(e.Id, RecoveryOutcome.Lost, null));
            }
        }
        return results;
    }

    private async Task CommitAsync(JournalEntry e, string path, CancellationToken ct)
    {
        await vaultRecords.AddAsync(new VaultRecord(Guid.NewGuid(), e.SourcePath, path, e.FileId, clock.GetUtcNow()), ct);
        await journal.CommitAsync(e.Id, path, ct);
    }

    private bool Is(string path, string fileId) =>
        string.Equals(identity.TryGetFileId(path), fileId, StringComparison.OrdinalIgnoreCase);
}

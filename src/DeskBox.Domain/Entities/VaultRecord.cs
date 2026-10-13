namespace DeskBox.Domain.Entities;

/// <summary>移入收纳库的文件记录，用于还原。</summary>
public sealed class VaultRecord
{
    public Guid Id { get; }
    public string OriginalPath { get; }
    public string VaultPath { get; }
    public string FileId { get; }
    public DateTimeOffset MovedAt { get; }
    public DateTimeOffset? RestoredAt { get; private set; }
    public bool IsRestored => RestoredAt.HasValue;

    public VaultRecord(Guid id, string originalPath, string vaultPath, string fileId, DateTimeOffset movedAt)
    {
        if (id == Guid.Empty) throw new DomainValidationException(nameof(Id), "不能为空");
        if (string.IsNullOrWhiteSpace(originalPath)) throw new DomainValidationException(nameof(OriginalPath), "不能为空");
        if (string.IsNullOrWhiteSpace(vaultPath)) throw new DomainValidationException(nameof(VaultPath), "不能为空");
        if (string.Equals(originalPath, vaultPath, StringComparison.OrdinalIgnoreCase))
            throw new DomainValidationException(nameof(VaultPath), "不能与原路径相同");
        if (!FenceItem.IsValidFileId(fileId)) throw new DomainValidationException(nameof(FileId), "格式无效");
        (Id, OriginalPath, VaultPath, FileId, MovedAt) = (id, originalPath, vaultPath, fileId.ToUpperInvariant(), movedAt);
    }

    public void MarkRestored(DateTimeOffset at)
    {
        if (IsRestored) throw new InvalidTransitionException(nameof(VaultRecord), "Restored", "Restore");
        if (at < MovedAt) throw new DomainValidationException(nameof(RestoredAt), "不能早于移入时间");
        RestoredAt = at;
    }
}

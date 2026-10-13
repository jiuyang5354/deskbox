namespace DeskBox.Domain.Entities;

/// <summary>恢复时找不回的文件提示。持久保存，直到用户确认（Acknowledge）。</summary>
public sealed class LostFileNotice
{
    public Guid JournalEntryId { get; }
    public string FileId { get; }
    public string FileName { get; }
    public string OriginalPath { get; }
    public DateTimeOffset DetectedAt { get; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public bool IsAcknowledged => AcknowledgedAt.HasValue;

    public LostFileNotice(Guid journalEntryId, string fileId, string originalPath, DateTimeOffset detectedAt)
    {
        if (journalEntryId == Guid.Empty) throw new DomainValidationException(nameof(JournalEntryId), "不能为空");
        if (!FenceItem.IsValidFileId(fileId)) throw new DomainValidationException(nameof(FileId), "格式无效");
        if (string.IsNullOrWhiteSpace(originalPath)) throw new DomainValidationException(nameof(OriginalPath), "不能为空");
        (JournalEntryId, FileId, OriginalPath, DetectedAt) = (journalEntryId, fileId.ToUpperInvariant(), originalPath, detectedAt);
        FileName = Files.WinPath.GetFileName(originalPath);
        if (FileName.Length == 0) FileName = originalPath;
    }

    public void Acknowledge(DateTimeOffset at)
    {
        if (IsAcknowledged) throw new InvalidTransitionException(nameof(LostFileNotice), "Acknowledged", "Acknowledge");
        AcknowledgedAt = at;
    }
}

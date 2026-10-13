namespace DeskBox.Domain.Entities;

/// <summary>方框中的一个桌面项。file_path 全局唯一（一个文件只属于一个方框）。</summary>
public sealed class FenceItem
{
    public Guid Id { get; }
    public Guid FenceId { get; private set; }
    public string FilePath { get; private set; }
    public string FileId { get; private set; }
    public bool IsPublicDesktop { get; }
    public int SortOrder { get; private set; }

    public FenceItem(Guid id, Guid fenceId, string filePath, string fileId, bool isPublicDesktop = false, int sortOrder = 0)
    {
        if (id == Guid.Empty) throw new DomainValidationException(nameof(Id), "不能为空");
        if (fenceId == Guid.Empty) throw new DomainValidationException(nameof(FenceId), "不能为空");
        if (sortOrder < 0) throw new DomainValidationException(nameof(SortOrder), "不能为负");
        Id = id;
        FenceId = fenceId;
        FilePath = ValidatePath(filePath);
        FileId = ValidateFileId(fileId);
        IsPublicDesktop = isPublicDesktop;
        SortOrder = sortOrder;
    }

    /// <summary>公共桌面项可以映射到方框，但不能移入收纳库。</summary>
    public bool CanMoveToVault => !IsPublicDesktop;

    public void MoveToFence(Guid fenceId, int sortOrder)
    {
        if (fenceId == Guid.Empty) throw new DomainValidationException(nameof(FenceId), "不能为空");
        if (sortOrder < 0) throw new DomainValidationException(nameof(SortOrder), "不能为负");
        FenceId = fenceId;
        SortOrder = sortOrder;
    }

    /// <summary>文件被重命名/移动后，按 file_id 跟踪到新路径。</summary>
    public void UpdatePath(string newPath) => FilePath = ValidatePath(newPath);

    /// <summary>file_id 格式：卷序列号(8 hex) + ":" + 128 位文件 ID(32 hex)。</summary>
    public static bool IsValidFileId(string? fileId) =>
        fileId is { Length: 41 } && fileId[8] == ':' &&
        fileId.Remove(8, 1).All(Uri.IsHexDigit);

    private static string ValidatePath(string path) =>
        string.IsNullOrWhiteSpace(path) || path.Length > 32767
            ? throw new DomainValidationException(nameof(FilePath), "路径为空或过长")
            : path;

    private static string ValidateFileId(string fileId) =>
        IsValidFileId(fileId) ? fileId.ToUpperInvariant()
            : throw new DomainValidationException(nameof(FileId), "格式须为 8 位卷序列号:32 位文件 ID（十六进制）");
}

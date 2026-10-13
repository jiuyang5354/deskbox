namespace DeskBox.Application.Fences;

/// <summary>方框界面提示文案（PRD §7A）。</summary>
public static class FenceUiText
{
    public const string StagingCannotDelete = "暂存方框不能删除";
    public const string PublicDesktopCannotVault = "公共桌面上的文件不能移入收纳库";
    public const string NotFound = "方框或文件已不存在";
    public const string NoOtherFence = "没有其他方框";
    public const string NoStagingFence = "数据异常，未删除";

    /// <summary>删除方框前的确认文案（PM 定）。</summary>
    public static string Deleted(int moved) => moved == 0 ? "方框已删除" : $"方框已删除，{moved} 个文件已移到暂存方框";

    public static string ConfirmDelete(int count) => $"方框里的 {count} 个文件会移到暂存方框，磁盘上的文件不动";

    public static string ReadOnly(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "DeskBox 处于只读模式，不移动文件、不改归属" : $"DeskBox 处于只读模式（{reason}），不移动文件、不改归属";
}

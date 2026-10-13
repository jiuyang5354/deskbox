namespace DeskBox.Application.Fences;

/// <summary>方框界面提示文案（PRD §7A）。</summary>
public static class FenceUiText
{
    public const string StagingCannotDelete = "暂存方框不能删除";
    public const string PublicDesktopCannotVault = "公共桌面上的文件不能移入收纳库";
    public const string NotFound = "方框或文件已不存在";
    public const string NoOtherFence = "没有其他方框";

    public static string ReadOnly(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "DeskBox 处于只读模式，不移动文件、不改归属" : $"DeskBox 处于只读模式（{reason}），不移动文件、不改归属";
}

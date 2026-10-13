using DeskBox.Domain.Geometry;

namespace DeskBox.Application.Fences;

/// <summary>方框界面命令的结果（TDD §4.7）。界面只根据它显示提示，不自行判断规则。</summary>
public enum CommandStatus
{
    Ok,
    /// <summary>只读模式：改归属、移文件的命令被拒绝。</summary>
    ReadOnly,
    NotFound,
    /// <summary>领域规则拒绝（暂存方框删除、公共桌面项移入收纳库、非法参数、移动被跳过）。</summary>
    Rejected,
}

public sealed record CommandResult(CommandStatus Status, string? Reason = null)
{
    public static CommandResult Ok { get; } = new(CommandStatus.Ok);
    public bool IsOk => Status == CommandStatus.Ok;
}

/// <summary>拖动方框结束。Screen 为松手时窗口的屏幕物理像素矩形；只取位置，尺寸沿用数据库。</summary>
public sealed record MoveFenceCommand(Guid FenceId, ScreenRect Screen);

/// <summary>调整方框大小结束。Screen 为松手时窗口的屏幕物理像素矩形。</summary>
public sealed record ResizeFenceCommand(Guid FenceId, ScreenRect Screen);

/// <summary>方框间拖文件 / 「移到其他方框」：只改 fence_item 归属。</summary>
public sealed record ReassignItemCommand(Guid ItemId, Guid TargetFenceId);

/// <summary>「从方框移除」：只删映射，不删磁盘文件。</summary>
public sealed record RemoveItemCommand(Guid ItemId);

public sealed record RenameFenceCommand(Guid FenceId, string Title);

public sealed record SetFenceOpacityCommand(Guid FenceId, int Opacity);

public sealed record ToggleCollapseCommand(Guid FenceId);

public sealed record DeleteFenceCommand(Guid FenceId);

/// <summary>「移入收纳库」：唯一会移动文件的命令，走 JournaledMove。</summary>
public sealed record VaultItemCommand(Guid ItemId);

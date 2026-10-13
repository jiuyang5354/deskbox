using DeskBox.Application.Startup;
using DeskBox.Domain.Entities;

namespace DeskBox.Application.Fences;

public enum FenceMenuAction
{
    Rename, Collapse, Expand, Delete, Opacity,
    Open, OpenLocation, MoveToFence, MoveToVault, RemoveFromFence,
}

/// <summary>一个菜单项。禁用时 DisabledReason 给出原因，界面以提示显示（PRD §7A）。TargetFenceId 仅用于「移到其他方框」的子项。</summary>
public sealed record FenceMenuEntry(FenceMenuAction Action, bool IsEnabled, string? DisabledReason = null,
    Guid? TargetFenceId = null, string? TargetTitle = null);

/// <summary>
/// 右键菜单的内容与可用状态（TDD §4.7 第 4、5 条）。规则来自 Domain（<see cref="Fence.CanDelete"/>、<see cref="FenceItem.CanMoveToVault"/>）
/// 与 <see cref="OperatingMode"/>，和 <see cref="FenceCommandHandler"/> 用的是同一份状态。
/// </summary>
public static class FenceMenus
{
    public static readonly IReadOnlyList<int> OpacityLevels = [40, 70, 100];

    public static IReadOnlyList<FenceMenuEntry> ForFence(Fence fence, OperatingMode mode)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(mode);
        string? deleteBlock = mode.IsReadOnly ? FenceUiText.ReadOnly(mode.ReadOnlyReason)
            : fence.IsStaging ? FenceUiText.StagingCannotDelete : null;
        return
        [
            new(FenceMenuAction.Rename, true),
            new(fence.IsCollapsed ? FenceMenuAction.Expand : FenceMenuAction.Collapse, true),
            new(FenceMenuAction.Delete, deleteBlock is null, deleteBlock),
            new(FenceMenuAction.Opacity, true),
        ];
    }

    public static IReadOnlyList<FenceMenuEntry> ForItem(FenceItem item, IReadOnlyList<Fence> allFences, OperatingMode mode)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(allFences);
        ArgumentNullException.ThrowIfNull(mode);
        var ro = mode.IsReadOnly ? FenceUiText.ReadOnly(mode.ReadOnlyReason) : null;
        var others = allFences.Where(f => f.Id != item.FenceId).ToList();
        var list = new List<FenceMenuEntry>
        {
            new(FenceMenuAction.Open, true),
            new(FenceMenuAction.OpenLocation, true),
        };
        if (others.Count == 0)
            list.Add(new(FenceMenuAction.MoveToFence, false, ro ?? FenceUiText.NoOtherFence));
        else
            list.AddRange(others.Select(f => new FenceMenuEntry(FenceMenuAction.MoveToFence, ro is null, ro, f.Id, f.Title)));
        var vaultBlock = ro ?? (item.CanMoveToVault ? null : FenceUiText.PublicDesktopCannotVault);
        list.Add(new(FenceMenuAction.MoveToVault, vaultBlock is null, vaultBlock));
        list.Add(new(FenceMenuAction.RemoveFromFence, ro is null, ro));
        return list;
    }

    public static string Label(FenceMenuAction action) => action switch
    {
        FenceMenuAction.Rename => "重命名",
        FenceMenuAction.Collapse => "折叠",
        FenceMenuAction.Expand => "展开",
        FenceMenuAction.Delete => "删除",
        FenceMenuAction.Opacity => "透明度",
        FenceMenuAction.Open => "打开",
        FenceMenuAction.OpenLocation => "打开文件位置",
        FenceMenuAction.MoveToFence => "移到其他方框",
        FenceMenuAction.MoveToVault => "移入收纳库",
        FenceMenuAction.RemoveFromFence => "从方框移除",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}

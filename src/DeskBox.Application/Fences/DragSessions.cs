using DeskBox.Domain.Geometry;

namespace DeskBox.Application.Fences;

/// <summary>
/// 拖动 / 缩放方框的一次会话（TDD §4.7 第 3 条）。拖动中只回调 <paramref name="applyWindow"/> 移动窗口，不碰数据库；
/// 松手 <see cref="Complete"/> 产生唯一一次落库命令；Esc <see cref="Cancel"/> 把窗口放回原位，不产生命令。
/// </summary>
public sealed class FenceDragSession(Guid fenceId, ScreenRect original, bool resize, Action<ScreenRect> applyWindow)
{
    public Guid FenceId { get; } = fenceId;
    public ScreenRect Original { get; } = original;
    public ScreenRect Current { get; private set; } = original;
    public bool IsResize { get; } = resize;
    public bool IsFinished { get; private set; }

    /// <summary>鼠标相对按下时的位移（物理像素）。</summary>
    public void Update(int dx, int dy)
    {
        if (IsFinished) return;
        Current = IsResize
            ? Original with { Width = Math.Max(1, Original.Width + dx), Height = Math.Max(1, Original.Height + dy) }
            : Original with { X = Original.X + dx, Y = Original.Y + dy };
        applyWindow(Current);
    }

    public void Cancel()
    {
        if (IsFinished) return;
        IsFinished = true;
        Current = Original;
        applyWindow(Original);
    }

    /// <summary>松手：没有移动时返回 null（不写库）。</summary>
    public object? Complete()
    {
        if (IsFinished) return null;
        IsFinished = true;
        if (Current == Original) return null;
        return IsResize ? new ResizeFenceCommand(FenceId, Current) : new MoveFenceCommand(FenceId, Current);
    }
}

/// <summary>
/// 方框间拖文件（PRD §7A 拖放 3、4）。悬停目标用于高亮；拖到原方框、方框外或被 Esc 取消 → 不产生命令，归属不变。
/// </summary>
public sealed class ItemDragSession(Guid itemId, Guid sourceFenceId)
{
    public Guid ItemId { get; } = itemId;
    public Guid SourceFenceId { get; } = sourceFenceId;
    public Guid? HoverFenceId { get; private set; }
    public bool IsFinished { get; private set; }

    /// <summary>当前悬停的方框（null = 方框外）。</summary>
    public void Hover(Guid? fenceId)
    {
        if (!IsFinished) HoverFenceId = fenceId;
    }

    /// <summary>该方框此刻是否应显示"可放下"高亮。</summary>
    public bool IsDropTarget(Guid fenceId) => !IsFinished && HoverFenceId == fenceId && fenceId != SourceFenceId;

    public void Cancel()
    {
        IsFinished = true;
        HoverFenceId = null;
    }

    public ReassignItemCommand? Drop(Guid? targetFenceId)
    {
        if (IsFinished) return null;
        IsFinished = true;
        HoverFenceId = null;
        return targetFenceId is { } t && t != SourceFenceId ? new ReassignItemCommand(ItemId, t) : null;
    }
}

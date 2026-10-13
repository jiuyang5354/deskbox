using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Geometry;

namespace DeskBox.Application.Desktop;

/// <summary>
/// 存储位置（DIP，相对显示器工作区）与屏幕物理像素之间的换算（TDD §4.5.2）。
/// 所属显示器不在了 → 按主显示器的工作区与 DPI 放置，并整体挪进主显示器工作区；不回写数据库，显示器回来后回到原处。
/// </summary>
public static class FenceLayout
{
    public static MonitorInfo Primary(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0) throw new InvalidOperationException("没有可用的显示器");
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>方框应显示的屏幕物理矩形，保证完全落在某个显示器的工作区内。</summary>
    public static ScreenRect Place(Fence fence, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(fence);
        var monitor = monitors.FirstOrDefault(m => m.Fingerprint == fence.MonitorId) ?? Primary(monitors);
        var stored = new ScreenRect(fence.X, fence.Y, fence.Width, fence.Height);
        return DesktopGeometry.ClampInto(DesktopGeometry.ToScreen(stored, monitor.WorkArea, monitor.Dpi), monitor.WorkArea);
    }

    /// <summary>用户拖动 / 缩放结束：按方框中心所在显示器（不在任何显示器上则主显示器）换算回存储位置并更新实体。</summary>
    public static void Capture(Fence fence, ScreenRect screen, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(fence);
        var cx = screen.X + screen.Width / 2;
        var cy = screen.Y + screen.Height / 2;
        var monitor = monitors.FirstOrDefault(m => m.Bounds.ContainsPoint(cx, cy)) ?? Primary(monitors);
        var stored = DesktopGeometry.ToStored(screen, monitor.WorkArea, monitor.Dpi);
        fence.MoveTo(monitor.Fingerprint, stored.X, stored.Y, monitor.Dpi);
        fence.Resize(stored.Width, stored.Height);
    }
}

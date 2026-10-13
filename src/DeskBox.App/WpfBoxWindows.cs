using System.Windows;
using System.Windows.Interop;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;

namespace DeskBox.App;

/// <summary>
/// 每个方框一个无边框 WPF 窗口。Explorer 重启时旧 WorkerW 被销毁，其子窗口一并销毁；
/// 这里按句柄是否仍存活判断，已销毁则新建（位置由 DesktopEmbedder 从数据库读取后设置）。
/// </summary>
public sealed class WpfBoxWindows(IDesktopWindowing win) : IBoxWindows
{
    private readonly Dictionary<Guid, Window> _windows = [];

    public IntPtr Ensure(Fence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (_windows.TryGetValue(fence.Id, out var w) && new WindowInteropHelper(w).Handle is var h && h != IntPtr.Zero && win.IsWindow(h))
            return h;
        var window = new Window
        {
            Title = fence.Title, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false,
            AllowsTransparency = false, ShowActivated = false, Opacity = fence.Opacity / 100.0,
        };
        _windows[fence.Id] = window;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        window.Show();
        return handle;
    }
}

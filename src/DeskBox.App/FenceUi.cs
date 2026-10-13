using System.Windows;
using System.Windows.Threading;
using DeskBox.Application.Desktop;
using DeskBox.Application.Fences;
using DeskBox.Application.Ports;
using DeskBox.Application.Startup;
using DeskBox.Domain.Entities;

namespace DeskBox.App;

/// <summary>
/// 方框界面协调者：每个方框一个 <see cref="FenceWindow"/>（实现 <see cref="IBoxWindows"/>，供 DesktopEmbedder 创建 / 重建窗口）。
/// 所有写操作经 <see cref="FenceCommandHandler"/>，读取经 <see cref="FenceBoard"/>；本类不引用数据库或文件接口（TDD §4.7 第 1 条）。
/// </summary>
public sealed class FenceUi(IDesktopWindowing windowing, IMonitorService monitors, FenceCommandHandler commands, FenceBoard board,
    OperatingMode mode) : IBoxWindows
{
    private readonly Dictionary<Guid, FenceWindow> _windows = [];
    private ItemDragSession? _itemDrag;

    public IDesktopWindowing Windowing { get; } = windowing;
    public IMonitorService Monitors { get; } = monitors;
    public OperatingMode Mode { get; } = mode;

    public IReadOnlyCollection<Window> Windows => _windows.Values;

    public IReadOnlyList<Fence> AllFences() => _windows.Values.Select(w => w.Fence).ToList();

    public IntPtr Ensure(Fence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (_windows.TryGetValue(fence.Id, out var w) && w.Handle is var h && h != IntPtr.Zero && Windowing.IsWindow(h))
            return h;
        var window = new FenceWindow(this, fence);
        _windows[fence.Id] = window;
        var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        window.Show();
        return handle;
    }

    public void BeginItemDrag(Guid itemId, Guid sourceFenceId) => _itemDrag = new ItemDragSession(itemId, sourceFenceId);

    public void CancelItemDrag()
    {
        _itemDrag?.Cancel();
        _itemDrag = null;
        Highlight();
    }

    /// <summary>拖文件悬停：返回该方框是否可以放下（并更新所有方框的高亮）。</summary>
    public bool HoverDropTarget(Guid? fenceId)
    {
        if (_itemDrag is null) return false;
        _itemDrag.Hover(fenceId);
        Highlight();
        return fenceId is { } id && _itemDrag.IsDropTarget(id);
    }

    public async Task DropAsync(Guid fenceId)
    {
        var drag = _itemDrag;
        _itemDrag = null;
        var cmd = drag?.Drop(fenceId);
        Highlight();
        if (cmd is not null) await RunAsync(cmd);
    }

    private void Highlight()
    {
        foreach (var w in _windows.Values) w.SetDropTarget(_itemDrag?.IsDropTarget(w.Fence.Id) == true);
    }

    /// <summary>执行一条界面命令；不成功时提示原因，然后从数据库刷新界面。</summary>
    public async Task RunAsync(object command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = command switch
        {
            MoveFenceCommand c => await commands.ExecuteAsync(c),
            ResizeFenceCommand c => await commands.ExecuteAsync(c),
            ReassignItemCommand c => await commands.ExecuteAsync(c),
            RemoveItemCommand c => await commands.ExecuteAsync(c),
            RenameFenceCommand c => await commands.ExecuteAsync(c),
            SetFenceOpacityCommand c => await commands.ExecuteAsync(c),
            ToggleCollapseCommand c => await commands.ExecuteAsync(c),
            DeleteFenceCommand c => await commands.ExecuteAsync(c),
            VaultItemCommand c => await commands.ExecuteAsync(c),
            _ => throw new ArgumentException($"未知命令 {command.GetType().Name}", nameof(command)),
        };
        if (!result.IsOk) MessageBox.Show(result.Reason ?? result.Status.ToString(), "DeskBox", MessageBoxButton.OK, MessageBoxImage.Information);
        await RefreshAsync();
    }

    /// <summary>从数据库重新读方框与文件：更新窗口内容并按存储位置重新放置；已删除的方框关窗。</summary>
    public async Task RefreshAsync()
    {
        var snapshots = await board.LoadAsync();
        var all = Monitors.GetMonitors();
        foreach (var gone in _windows.Keys.Except(snapshots.Select(s => s.Fence.Id)).ToList())
        {
            _windows[gone].Close();
            _windows.Remove(gone);
        }
        foreach (var s in snapshots)
        {
            if (!_windows.TryGetValue(s.Fence.Id, out var w)) continue;
            w.Apply(s.Fence, s.Items);
            if (all.Count > 0) Windowing.SetBounds(w.Handle, FenceLayout.Place(s.Fence, all));
        }
    }
}

/// <summary>WPF 界面线程调度。</summary>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
}

/// <summary>第二实例唤醒：恢复最小化、显示、激活并置前（PRD §7A 第二实例 2）。必须在界面线程调用。</summary>
public sealed class WpfWindowActivator(Func<IEnumerable<Window>> windows) : IWindowActivator
{
    public void RestoreActivateAndBringToFront()
    {
        foreach (var w in windows())
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            if (!w.IsVisible) w.Show();
            w.Activate();
            w.Topmost = true;
            w.Topmost = false;
        }
    }
}

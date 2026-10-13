using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DeskBox.Application.Desktop;
using DeskBox.Application.Fences;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;

namespace DeskBox.App;

/// <summary>
/// 一个方框窗口。拖动标题栏移动、拖右下角缩放：拖动中只 SetBounds 移窗口，松手时发一次命令落库；Esc 取消并放回原位（TDD §4.7 第 3 条）。
/// 文件项可拖到其他方框（<see cref="FenceUi"/> 统一处理高亮与放下）。右键菜单来自 <see cref="FenceMenus"/>。
/// </summary>
public sealed class FenceWindow : Window
{
    private const string ItemFormat = "DeskBox.FenceItem";
    private readonly FenceUi _ui;
    private readonly FenceView _view = new();
    private FenceDragSession? _drag;
    private Point _dragStart;
    private Point? _itemPress;
    private Guid _pressedItem;

    public FenceWindow(FenceUi ui, Fence fence)
    {
        _ui = ui;
        Fence = fence;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowDrop = true;
        Content = _view;
        Title = fence.Title;
        _view.TitleBar.MouseLeftButtonDown += (_, e) => BeginDrag(e, resize: false);
        _view.ResizeGrip.PreviewMouseLeftButtonDown += (_, e) => BeginDrag(e, resize: true);
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += async (_, _) => await EndDragAsync();
        PreviewKeyDown += OnKeyDown;
        LostMouseCapture += (_, _) => _drag?.Cancel();
        _view.TitleBar.ContextMenuOpening += (_, _) => _view.TitleBar.ContextMenu = BuildFenceMenu();
        _view.TitleBar.ContextMenu = BuildFenceMenu();
        DragEnter += (_, e) => OnItemDragOver(e);
        DragOver += (_, e) => OnItemDragOver(e);
        DragLeave += (_, _) => _ui.HoverDropTarget(null);
        Drop += async (_, e) => { e.Handled = true; await _ui.DropAsync(Fence.Id); };
        Apply(fence, []);
    }

    public Fence Fence { get; private set; }

    public FenceView View => _view;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public void Apply(Fence fence, IReadOnlyList<FenceItem> items)
    {
        Fence = fence;
        Title = fence.Title;
        _view.Title = fence.Title;
        _view.IsCollapsed = fence.IsCollapsed;
        Opacity = fence.Opacity / 100.0;
        _view.SetItems(items.Select(i => new FenceItemView(i.Id, DeskBox.Domain.Files.WinPath.GetFileName(i.FilePath))));
        foreach (var el in _view.ItemElements)
        {
            var item = items.First(i => i.Id == (Guid)el.Tag);
            el.PreviewMouseLeftButtonDown += (_, e) => { _itemPress = e.GetPosition(this); _pressedItem = item.Id; };
            el.PreviewMouseMove += (_, e) => OnItemMouseMove(e);
            el.MouseLeftButtonUp += (_, _) => _itemPress = null;
            el.ContextMenuOpening += (_, _) => el.ContextMenu = BuildItemMenu(item);
            el.ContextMenu = BuildItemMenu(item);
            el.InputBindings.Add(new MouseBinding(new RelayCommand(() => Open(item.FilePath)), new MouseGesture(MouseAction.LeftDoubleClick)));
        }
    }

    public void SetDropTarget(bool on) => _view.IsDropTarget = on;

    private void BeginDrag(MouseButtonEventArgs e, bool resize)
    {
        if (e.ClickCount > 1) return;
        var screen = FenceLayout.Place(Fence, _ui.Monitors.GetMonitors());
        _drag = new FenceDragSession(Fence.Id, screen, resize, r => _ui.Windowing.SetBounds(Handle, r));
        _dragStart = PointToScreen(e.GetPosition(this));
        Activate();
        Focus();
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag is null || _drag.IsFinished) return;
        var p = PointToScreen(e.GetPosition(this));
        _drag.Update((int)(p.X - _dragStart.X), (int)(p.Y - _dragStart.Y));
    }

    private async Task EndDragAsync()
    {
        var drag = _drag;
        if (drag is null) return;
        _drag = null;
        var cmd = drag.Complete();
        ReleaseMouseCapture();
        if (cmd is not null) await _ui.RunAsync(cmd);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _drag is null) return;
        _drag.Cancel();
        _drag = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnItemMouseMove(MouseEventArgs e)
    {
        if (_itemPress is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _itemPress = null;
        _ui.BeginItemDrag(_pressedItem, Fence.Id);
        // 系统拖放循环：Esc 或放到非法目标返回 None，归属不变
        var result = DragDrop.DoDragDrop(this, new DataObject(ItemFormat, _pressedItem.ToString()), DragDropEffects.Move);
        if (result == DragDropEffects.None) _ui.CancelItemDrag();
    }

    private void OnItemDragOver(DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(ItemFormat) && _ui.HoverDropTarget(Fence.Id);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private ContextMenu BuildFenceMenu()
    {
        var menu = new ContextMenu();
        foreach (var entry in FenceMenus.ForFence(Fence, _ui.Mode))
        {
            var mi = FenceView.BuildMenuItem(entry, en => _ = OnFenceActionAsync(en.Action, null));
            if (entry.Action == FenceMenuAction.Opacity)
            {
                foreach (var level in FenceMenus.OpacityLevels)
                {
                    var sub = new MenuItem { Header = $"{level}%", IsChecked = Fence.Opacity == level };
                    sub.Click += (_, e) => { e.Handled = true; _ = OnFenceActionAsync(FenceMenuAction.Opacity, level); };
                    mi.Items.Add(sub);
                }
            }
            menu.Items.Add(mi);
        }
        return menu;
    }

    private ContextMenu BuildItemMenu(FenceItem item)
    {
        var menu = new ContextMenu();
        foreach (var entry in FenceMenus.ForItem(item, _ui.AllFences(), _ui.Mode))
            menu.Items.Add(FenceView.BuildMenuItem(entry, en => _ = OnItemActionAsync(en, item)));
        return menu;
    }

    private async Task OnFenceActionAsync(FenceMenuAction action, int? opacity)
    {
        switch (action)
        {
            case FenceMenuAction.Rename:
                if (RenameDialog.Ask(Fence.Title) is { } title) await _ui.RunAsync(new RenameFenceCommand(Fence.Id, title));
                break;
            case FenceMenuAction.Collapse or FenceMenuAction.Expand:
                await _ui.RunAsync(new ToggleCollapseCommand(Fence.Id));
                break;
            case FenceMenuAction.Delete:
                await _ui.RunAsync(new DeleteFenceCommand(Fence.Id));
                break;
            case FenceMenuAction.Opacity when opacity is { } o:
                await _ui.RunAsync(new SetFenceOpacityCommand(Fence.Id, o));
                break;
            default:
                break;
        }
    }

    private async Task OnItemActionAsync(FenceMenuEntry entry, FenceItem item)
    {
        switch (entry.Action)
        {
            case FenceMenuAction.Open:
                Open(item.FilePath);
                break;
            case FenceMenuAction.OpenLocation:
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
                break;
            case FenceMenuAction.MoveToFence when entry.TargetFenceId is { } target:
                await _ui.RunAsync(new ReassignItemCommand(item.Id, target));
                break;
            case FenceMenuAction.MoveToVault:
                await _ui.RunAsync(new VaultItemCommand(item.Id));
                break;
            case FenceMenuAction.RemoveFromFence:
                await _ui.RunAsync(new RemoveItemCommand(item.Id));
                break;
            default:
                break;
        }
    }

    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private sealed class RelayCommand(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}

/// <summary>重命名输入框。</summary>
internal static class RenameDialog
{
    public static string? Ask(string current)
    {
        var box = new TextBox { Text = current, MaxLength = Fence.TitleMaxLength, Margin = new Thickness(12), MinWidth = 220 };
        var ok = new Button { Content = "确定", IsDefault = true, Width = 72, Margin = new Thickness(12, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right };
        var w = new Window
        {
            Title = "重命名方框", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,
            Content = new StackPanel { Children = { box, ok } },
        };
        ok.Click += (_, _) => w.DialogResult = true;
        box.SelectAll();
        box.Focus();
        return w.ShowDialog() == true ? box.Text : null;
    }
}

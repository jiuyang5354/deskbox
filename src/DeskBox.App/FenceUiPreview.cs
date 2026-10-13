using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DeskBox.Application.Fences;
using DeskBox.Application.Startup;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Geometry;

namespace DeskBox.App;

/// <summary>
/// 把方框界面的交互状态离屏渲染成 PNG（RenderTargetBitmap），供 PR 评审与真机清单对照。使用与运行时相同的
/// <see cref="FenceView"/>、<see cref="FenceMenus"/> 和拖动会话；必须在 STA 线程调用。
/// 弹出层（ContextMenu / ToolTip）是独立窗口，无法离屏渲染，所以菜单与原因提示按同样的 MenuItem 平铺渲染。
/// </summary>
public static class FenceUiPreview
{
    private static readonly string[] SampleFiles = ["报告.docx", "截图_01.png", "预算.xlsx"];

    public static IReadOnlyList<string> RenderAll(string directory)
    {
        Directory.CreateDirectory(directory);
        return
        [
            Save(Hover(), directory, "01-hover.png"),
            Save(DropTarget(), directory, "02-drop-target.png"),
            Save(DisabledReason(), directory, "03-disabled-reason.png"),
            Save(EscCancel(out _), directory, "04-esc-cancel.png"),
        ];
    }

    private static FenceView Fence(string title)
    {
        var v = new FenceView { Title = title, Width = 220, Height = 180 };
        v.SetItems(SampleFiles.Select(n => new FenceItemView(Guid.NewGuid(), n)));
        return v;
    }

    private static FrameworkElement Caption(string text, FrameworkElement content) => new StackPanel
    {
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x6E, 0xA5)),
        Children =
        {
            new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(12, 10, 12, 6) },
            new Border { Padding = new Thickness(16), Child = content },
        },
    };

    public static FrameworkElement Hover()
    {
        var plain = Fence("文档");
        var hover = Fence("文档");
        hover.IsHover = true;
        return Caption("悬停：标题栏变亮（左：普通，右：悬停，可拖动）",
            new StackPanel { Orientation = Orientation.Horizontal, Children = { plain, Spacer(), hover } });
    }

    public static FrameworkElement DropTarget()
    {
        var source = Fence("文档");
        var target = Fence("截图");
        var session = new ItemDragSession(Guid.NewGuid(), Guid.NewGuid());
        var targetId = Guid.NewGuid();
        session.Hover(targetId);
        target.IsDropTarget = session.IsDropTarget(targetId);
        return Caption("拖文件到其他方框：目标方框蓝色高亮（松手只改归属，不移动文件）",
            new StackPanel { Orientation = Orientation.Horizontal, Children = { source, Spacer(), target } });
    }

    public static FrameworkElement DisabledReason()
    {
        var mode = new OperatingMode();
        mode.EnterReadOnly("1 条未完成的整理记录恢复失败");
        var a = new Fence(Guid.NewGuid(), "文档", "M1", 0, 0, 220, 180);
        var staging = new Fence(Guid.NewGuid(), "暂存", "M1", 0, 0, 220, 180, isStaging: true);
        var item = new FenceItem(Guid.NewGuid(), a.Id, @"C:\Users\Public\Desktop\公共.lnk", "1A2B3C4D:000000000000000000000000000000AA", isPublicDesktop: true);
        var normal = new OperatingMode();
        return Caption("禁用项带原因提示（左：暂存方框菜单；中：公共桌面文件菜单；右：只读模式文件菜单）",
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    Menu(FenceMenus.ForFence(staging, normal)), Spacer(),
                    Menu(FenceMenus.ForItem(item, [a, staging], normal)), Spacer(),
                    Menu(FenceMenus.ForItem(item, [a, staging], mode)),
                },
            });
    }

    /// <summary>拖动后按 Esc：窗口回到原位，会话不产生命令（不写库）。<paramref name="command"/> 返回会话的落库命令（应为 null）。</summary>
    public static FrameworkElement EscCancel(out object? command)
    {
        var original = new ScreenRect(20, 20, 220, 180);
        var placed = new List<ScreenRect>();
        var session = new FenceDragSession(Guid.NewGuid(), original, resize: false, placed.Add);
        session.Update(260, 60);
        var dragged = session.Current;
        session.Cancel();
        command = session.Complete();
        var canvas = new Canvas { Width = 540, Height = 280 };
        var ghost = new Rectangle
        {
            Width = dragged.Width, Height = dragged.Height, Stroke = Brushes.White, StrokeThickness = 2,
            StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
        };
        Canvas.SetLeft(ghost, dragged.X);
        Canvas.SetTop(ghost, dragged.Y);
        var label = new TextBlock { Text = "拖动中的位置（Esc 前）", Foreground = Brushes.White };
        Canvas.SetLeft(label, dragged.X + 8);
        Canvas.SetTop(label, dragged.Y + dragged.Height / 2.0);
        var restored = Fence("文档");
        Canvas.SetLeft(restored, placed[^1].X);
        Canvas.SetTop(restored, placed[^1].Y);
        canvas.Children.Add(ghost);
        canvas.Children.Add(label);
        canvas.Children.Add(restored);
        return Caption($"Esc 取消拖动：放回原位置 ({placed[^1].X},{placed[^1].Y})，落库命令 = {(command is null ? "无" : "有")}", canvas);
    }

    private static FrameworkElement Menu(IReadOnlyList<FenceMenuEntry> entries)
    {
        var panel = new StackPanel { Background = SystemColors.MenuBrush, MinWidth = 200 };
        foreach (var e in entries)
        {
            var mi = FenceView.BuildMenuItem(e, _ => { });
            panel.Children.Add(mi);
            if (e.DisabledReason is { } reason)
                panel.Children.Add(new Border
                {
                    Margin = new Thickness(24, 0, 6, 4), Padding = new Thickness(6, 2, 6, 2), BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Gray, Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xE1)),
                    Child = new TextBlock { Text = "提示：" + reason, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 },
                });
        }
        return new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = panel, VerticalAlignment = VerticalAlignment.Top };
    }

    private static FrameworkElement Spacer() => new Border { Width = 24 };

    public static string Save(FrameworkElement element, string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        var path = System.IO.Path.Combine(directory, name);
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }
}

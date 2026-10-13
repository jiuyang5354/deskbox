using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DeskBox.Application.Fences;

namespace DeskBox.App;

/// <summary>一个方框里显示的文件。</summary>
public sealed record FenceItemView(Guid Id, string Name);

/// <summary>
/// 方框的可视部分（标题栏 + 文件列表 + 右下角缩放手柄）。只负责显示与把鼠标事件交给 <see cref="FenceWindow"/>；
/// 不引用数据库或文件接口（TDD §4.7 第 1 条）。交互状态：悬停、可放下高亮、折叠。
/// </summary>
public sealed class FenceView : Border
{
    private static readonly Brush Normal = Frozen(Color.FromArgb(0xCC, 0x20, 0x24, 0x2C));
    private static readonly Brush TitleNormal = Frozen(Color.FromArgb(0xFF, 0x2E, 0x34, 0x40));
    private static readonly Brush TitleHover = Frozen(Color.FromArgb(0xFF, 0x44, 0x4E, 0x60));
    private static readonly Brush DropFill = Frozen(Color.FromArgb(0xCC, 0x1E, 0x4E, 0x8C));
    private static readonly Brush DropBorder = Frozen(Color.FromRgb(0x4D, 0xA3, 0xFF));
    private static readonly Brush BorderNormal = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ItemHover = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private readonly Border _title = new() { Padding = new Thickness(8, 4, 8, 4) };
    private readonly TextBlock _titleText = new() { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13 };
    private readonly StackPanel _items = new() { Margin = new Thickness(6) };
    private readonly ScrollViewer _scroll;
    private bool _hover;
    private bool _dropTarget;
    private bool _collapsed;

    public FenceView()
    {
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        _title.Child = _titleText;
        _title.CornerRadius = new CornerRadius(6, 6, 0, 0);
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _items };
        ResizeGrip = new Thumb
        {
            Width = 12, Height = 12, Cursor = System.Windows.Input.Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.6,
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_scroll, 1);
        Grid.SetRow(ResizeGrip, 1);
        grid.Children.Add(_title);
        grid.Children.Add(_scroll);
        grid.Children.Add(ResizeGrip);
        Child = grid;
        _title.MouseEnter += (_, _) => IsHover = true;
        _title.MouseLeave += (_, _) => IsHover = false;
        Apply();
    }

    public FrameworkElement TitleBar => _title;
    public Thumb ResizeGrip { get; }
    public IReadOnlyList<FrameworkElement> ItemElements => _items.Children.OfType<FrameworkElement>().ToList();

    public string Title { get => _titleText.Text; set => _titleText.Text = value; }

    /// <summary>标题栏悬停（可拖动提示）。</summary>
    public bool IsHover { get => _hover; set { _hover = value; Apply(); } }

    /// <summary>拖文件经过本方框且可以放下。</summary>
    public bool IsDropTarget { get => _dropTarget; set { _dropTarget = value; Apply(); } }

    public bool IsCollapsed { get => _collapsed; set { _collapsed = value; Apply(); } }

    public void SetItems(IEnumerable<FenceItemView> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Children.Clear();
        foreach (var i in items)
        {
            var t = new Border
            {
                Tag = i.Id, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(3), Background = Brushes.Transparent,
                Child = new TextBlock { Text = i.Name, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            t.MouseEnter += (_, _) => t.Background = ItemHover;
            t.MouseLeave += (_, _) => t.Background = Brushes.Transparent;
            _items.Children.Add(t);
        }
    }

    private void Apply()
    {
        Background = _dropTarget ? DropFill : Normal;
        BorderBrush = _dropTarget ? DropBorder : BorderNormal;
        BorderThickness = new Thickness(_dropTarget ? 3 : 1);
        _title.Background = _hover ? TitleHover : TitleNormal;
        _scroll.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        ResizeGrip.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>把 <see cref="FenceMenus"/> 的条目建成 WPF 菜单项：禁用项带原因提示（禁用时也显示）。</summary>
    public static MenuItem BuildMenuItem(FenceMenuEntry entry, Action<FenceMenuEntry> invoke)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(invoke);
        var header = entry.TargetTitle is { } t ? $"{FenceMenus.Label(entry.Action)}：{t}" : FenceMenus.Label(entry.Action);
        var mi = new MenuItem { Header = header, IsEnabled = entry.IsEnabled, Tag = entry };
        if (entry.DisabledReason is { } reason)
        {
            mi.ToolTip = reason;
            ToolTipService.SetShowOnDisabled(mi, true);
            mi.Header = header + "（不可用）";
        }
        mi.Click += (_, _) => invoke(entry);
        return mi;
    }
}

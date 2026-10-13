using DeskBox.Application.Ports;
using DeskBox.Domain.Geometry;

namespace DeskBox.Application.Desktop;

/// <summary>桌面窗口层级（按窗口结构判断，从不看系统版本号或 ProductName）。</summary>
public enum DesktopLayering
{
    /// <summary>找不到 Progman，或旧层级下找不到图标层之后的 WorkerW。</summary>
    NotFound,
    /// <summary>旧层级：WorkerW 是顶层窗口，挂到持有 SHELLDLL_DefView 的顶层窗口之后的那个 WorkerW。</summary>
    Legacy,
    /// <summary>Win11 24H2 起的新层级：WorkerW 是 Progman 的子窗口；挂到 Progman，放在其子窗口最上面（SHELLDLL_DefView 之上）。</summary>
    ProgmanChild,
}

/// <summary>一次查找的结果。Host 为要挂的父窗口；ClassChain 形如 "WorkerW" 或 "Progman"，含祖先链。</summary>
public sealed record DesktopHost(DesktopLayering Layering, IntPtr Progman, IntPtr Host, string ClassChain)
{
    public static DesktopHost None(IntPtr progman) => new(DesktopLayering.NotFound, progman, IntPtr.Zero, "-");
}

/// <summary>TDD v1.0.13 §4.5.1：发 0x052C 后按窗口结构选择挂载点。</summary>
public static class DesktopHostLocator
{
    public const string WorkerWClass = "WorkerW";
    public const string ProgmanClass = "Progman";
    public const string DefViewClass = "SHELLDLL_DefView";

    public static DesktopHost Locate(IDesktopWindowTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var progman = tree.FindProgman();
        if (progman == IntPtr.Zero || !tree.IsWindow(progman)) return DesktopHost.None(IntPtr.Zero);
        tree.SpawnWorkerW(progman);

        // 先查新层级是有意的：24H2 起 Progman 下有子 WorkerW 时，旧的顶层 WorkerW 可能还残留着（隐藏）。
        // 如果先按旧层级找，会挂到那个隐藏的顶层 WorkerW 上——这正是这次 P0（方框看不见）的成因。
        if (tree.FindChild(progman, IntPtr.Zero, WorkerWClass) != IntPtr.Zero)
            return new(DesktopLayering.ProgmanChild, progman, progman, ClassChain(tree, progman));

        // 旧层级，逻辑与 v1.0.12 相同：持有 SHELLDLL_DefView 的顶层窗口之后的那个顶层 WorkerW
        foreach (var top in tree.TopLevelWindows())
        {
            if (tree.FindChild(top, IntPtr.Zero, DefViewClass) == IntPtr.Zero) continue;
            var worker = tree.FindChild(IntPtr.Zero, top, WorkerWClass);
            return worker != IntPtr.Zero && tree.IsWindow(worker)
                ? new(DesktopLayering.Legacy, progman, worker, ClassChain(tree, worker))
                : DesktopHost.None(progman);
        }
        return DesktopHost.None(progman);
    }

    /// <summary>从 hwnd 往上的类名链，例如 "WorkerW &lt; Progman"。</summary>
    public static string ClassChain(IDesktopWindowTree tree, IntPtr hwnd)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var names = new List<string>();
        for (var h = hwnd; h != IntPtr.Zero && names.Count < 16; h = tree.Parent(h))
            names.Add($"{tree.ClassName(h)}(0x{h.ToInt64():X})");
        return names.Count == 0 ? "-" : string.Join(" < ", names);
    }
}

/// <summary>SetParent 之后的校验结果。</summary>
public sealed record EmbedValidation(bool Ok, string Detail)
{
    public static readonly EmbedValidation Passed = new(true, "ok");
}

/// <summary>TDD v1.0.13 §4.5.1 挂载后校验：三条有任何一条不满足就撤回挂载、退回兼容显示。</summary>
public static class EmbedValidator
{
    /// <summary>实际位置与 <see cref="FenceLayout.Place"/> 目标允许的最大偏差（物理像素）。</summary>
    public const int MaxDeviationPx = 1;

    public static EmbedValidation Validate(IDesktopWindowTree tree, IntPtr box, ScreenRect bounds, IReadOnlyList<MonitorInfo> monitors, IntPtr progman)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(monitors);
        for (var p = tree.Parent(box); p != IntPtr.Zero; p = tree.Parent(p))
            if (!tree.IsVisible(p))
                return new(false, $"父窗口不可见：{tree.ClassName(p)}(0x{p.ToInt64():X})");
        // 读窗口实际位置（GetWindowRect），而不是相信传进去的目标值：坐标换算错了也要能发现
        var actual = tree.WindowRect(box);
        if (!monitors.Any(m => m.WorkArea.Intersects(actual)))
            return new(false, $"方框实际位置 {actual} 与所有显示器工作区都没有交集");
        if (Math.Abs(actual.X - bounds.X) > MaxDeviationPx || Math.Abs(actual.Y - bounds.Y) > MaxDeviationPx
            || Math.Abs(actual.Width - bounds.Width) > MaxDeviationPx || Math.Abs(actual.Height - bounds.Height) > MaxDeviationPx)
            return new(false, $"方框实际位置 {actual} 与目标 {bounds} 偏差超过 {MaxDeviationPx}px");
        var root = tree.Root(box);
        var rootOk = root != IntPtr.Zero && (root == progman
            || (tree.ClassName(root) == DesktopHostLocator.WorkerWClass && tree.IsVisible(root)));
        return rootOk ? EmbedValidation.Passed : new(false, $"根窗口不是 Progman 或可见的 WorkerW：{tree.ClassName(root)}(0x{root.ToInt64():X})");
    }
}

/// <summary>每个方框每次挂接的日志记录。</summary>
public sealed record EmbedReport(Guid FenceId, OsVersionInfo Os, DesktopLayering Layering, string ParentChain, string Validation, EmbedPath Path);

/// <summary>最后走的路径。</summary>
public enum EmbedPath
{
    Embedded,
    /// <summary>兼容显示：普通置底窗口（WS_EX_NOACTIVATE，钉在 HWND_BOTTOM）。</summary>
    Fallback,
}

/// <summary>
/// 兼容显示（钉在最底）的窗口登记表。Unpin 或窗口销毁（WM_NCDESTROY）时移除，避免句柄被系统复用后误钉别的窗口。线程安全。
/// </summary>
public sealed class PinnedWindows
{
    public const int WmWindowPosChanging = 0x0046;
    public const int WmNcDestroy = 0x0082;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, byte> _pinned = new();

    public int Count => _pinned.Count;

    public void Pin(IntPtr hwnd) => _pinned[hwnd] = 0;

    /// <summary>返回之前是否钉住。</summary>
    public bool Unpin(IntPtr hwnd) => _pinned.TryRemove(hwnd, out _);

    public bool IsPinned(IntPtr hwnd) => _pinned.ContainsKey(hwnd);

    /// <summary>窗口过程里调用：WM_NCDESTROY 时移除登记。返回该消息是否需要按 <see cref="FallbackZOrder"/> 改写 WINDOWPOS。</summary>
    public bool OnMessage(IntPtr hwnd, int msg)
    {
        if (msg == WmNcDestroy) Unpin(hwnd);
        return msg == WmWindowPosChanging && IsPinned(hwnd);
    }
}

/// <summary>
/// 兼容显示时 WM_WINDOWPOSCHANGING 的处理（修复「降级模式下方框浮到最前面」）：
/// 任何改 Z 序的请求都改成 HWND_BOTTOM；不改 Z 序的请求原样放行。
/// </summary>
public static class FallbackZOrder
{
    public static readonly IntPtr HwndBottom = 1;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    public static (IntPtr InsertAfter, uint Flags) Adjust(bool pinned, IntPtr insertAfter, uint flags)
    {
        if (!pinned || (flags & SwpNoZOrder) != 0) return (insertAfter, flags);
        return (HwndBottom, flags | SwpNoActivate);
    }
}

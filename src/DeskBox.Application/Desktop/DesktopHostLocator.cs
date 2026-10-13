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
    public static EmbedValidation Validate(IDesktopWindowTree tree, IntPtr box, ScreenRect bounds, IReadOnlyList<MonitorInfo> monitors, IntPtr progman)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(monitors);
        for (var p = tree.Parent(box); p != IntPtr.Zero; p = tree.Parent(p))
            if (!tree.IsVisible(p))
                return new(false, $"父窗口不可见：{tree.ClassName(p)}(0x{p.ToInt64():X})");
        if (!monitors.Any(m => m.WorkArea.Intersects(bounds)))
            return new(false, $"方框 {bounds} 与所有显示器工作区都没有交集");
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

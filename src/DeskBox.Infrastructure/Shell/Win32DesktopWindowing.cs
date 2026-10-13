using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBox.Application.Ports;
using DeskBox.Domain.Geometry;
using static DeskBox.Infrastructure.Shell.NativeMethods;

namespace DeskBox.Infrastructure.Shell;

/// <summary>
/// 桌面层 Win32 实现（TDD §4.5.1）：0x052C → WorkerW → SetParent。不注入 DLL、不装钩子。
/// 不缓存任何句柄：每次调用都重新查找。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32DesktopWindowing : IDesktopWindowing
{
    private const uint SpawnTimeoutMs = 1000;
    private const uint GaParent = 1;

    public IntPtr FindProgman() => FindWindow("Progman", null);

    public bool SpawnWorkerW(IntPtr progman)
    {
        // wParam 0xD / lParam 0x1 为常见用法；先发一次 (0,0) 兼容旧版本
        var a = SendMessageTimeout(progman, WmSpawnWorkerW, 0, 0, SmtoNormal, SpawnTimeoutMs, out _) != IntPtr.Zero;
        var b = SendMessageTimeout(progman, WmSpawnWorkerW, 0xD, 0x1, SmtoNormal, SpawnTimeoutMs, out _) != IntPtr.Zero;
        return a || b;
    }

    public IntPtr FindWorkerW(IntPtr progman)
    {
        // 经典布局：持有 SHELLDLL_DefView 的顶层窗口之后的那个 WorkerW
        var worker = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            worker = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return false;
        }, IntPtr.Zero);
        // Win11 24H2 起：WorkerW 是 Progman 的子窗口
        if (worker == IntPtr.Zero && progman != IntPtr.Zero) worker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        return worker;
    }

    bool IDesktopWindowing.IsWindow(IntPtr hwnd) => NativeMethods.IsWindow(hwnd);

    /// <summary>真正的父窗口；顶层窗口（父为桌面根窗口）返回 Zero。</summary>
    public IntPtr GetParent(IntPtr hwnd) => GetAncestor(hwnd, GaParent) is var p && p == GetDesktopWindow() ? IntPtr.Zero : p;

    bool IDesktopWindowing.SetParent(IntPtr child, IntPtr parent)
    {
        NativeMethods.SetParent(child, parent);
        // SetParent 的返回值是旧父窗口（可能本来就是 Zero），以结果为准
        return parent == IntPtr.Zero ? GetParent(child) == IntPtr.Zero : GetAncestor(child, GaParent) == parent;
    }

    public void SetBottomMost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    public void SetBounds(IntPtr hwnd, ScreenRect screen)
    {
        var p = new Point { X = screen.X, Y = screen.Y };
        var parent = GetParent(hwnd);
        if (parent != IntPtr.Zero) MapWindowPoints(IntPtr.Zero, parent, ref p, 1); // 屏幕 → 父窗口客户区
        if (!SetWindowPos(hwnd, IntPtr.Zero, p.X, p.Y, screen.Width, screen.Height, SwpNoZOrder | SwpNoActivate))
            throw new InvalidOperationException($"SetWindowPos 失败（Win32 {Marshal.GetLastPInvokeError()}）");
    }
}

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBox.Application.Desktop;
using DeskBox.Application.Ports;
using DeskBox.Domain.Geometry;
using Microsoft.Win32;
using static DeskBox.Infrastructure.Shell.NativeMethods;

namespace DeskBox.Infrastructure.Shell;

/// <summary>桌面窗口树的 Win32 实现（TDD v1.0.13 §4.5.1）。不缓存任何句柄：每次调用都重新查找。</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32DesktopWindowTree : IDesktopWindowTree
{
    private const uint SpawnTimeoutMs = 1000;

    public IntPtr FindProgman() => FindWindow("Progman", null);

    public bool SpawnWorkerW(IntPtr progman)
    {
        // wParam 0xD / lParam 0x1 为常见用法；先发一次 (0,0) 兼容旧版本
        var a = SendMessageTimeout(progman, WmSpawnWorkerW, 0, 0, SmtoNormal, SpawnTimeoutMs, out _) != IntPtr.Zero;
        var b = SendMessageTimeout(progman, WmSpawnWorkerW, 0xD, 0x1, SmtoNormal, SpawnTimeoutMs, out _) != IntPtr.Zero;
        return a || b;
    }

    public IReadOnlyList<IntPtr> TopLevelWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    public IntPtr FindChild(IntPtr parent, IntPtr after, string className) => FindWindowEx(parent, after, className, null);

    public string ClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        var n = GetClassName(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "?";
    }

    public bool IsVisible(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GwlStyle).ToInt64() & WsVisible) != 0;

    public IntPtr Parent(IntPtr hwnd) => GetAncestor(hwnd, GaParent) is var p && p == GetDesktopWindow() ? IntPtr.Zero : p;

    public IntPtr Root(IntPtr hwnd) => GetAncestor(hwnd, GaRoot);

    public bool IsWindow(IntPtr hwnd) => NativeMethods.IsWindow(hwnd);

    public OsVersionInfo OsVersion { get; } = ReadOsVersion();

    /// <summary>只读 CurrentBuild / UBR / DisplayVersion 记日志；ProductName 在 Win11 上仍写 "Windows 10"，从不读取。</summary>
    private static OsVersionInfo ReadOsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var ubr = key?.GetValue("UBR") is int u ? "." + u.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            return new(build + ubr, key?.GetValue("DisplayVersion") as string ?? "unknown");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException)
        {
            return new(Environment.OSVersion.Version.ToString(), "unknown");
        }
    }
}

/// <summary>
/// 方框窗口的 Win32 操作（TDD v1.0.13 §4.5）。不注入 DLL、不装钩子。
/// 兼容显示的方框记在 <see cref="IsPinned"/> 里，窗口过程在 WM_WINDOWPOSCHANGING 中调用 <see cref="FilterWindowPosChanging"/> 把它钉在最底。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32DesktopWindowing : IDesktopWindowing
{
    private static readonly ConcurrentDictionary<IntPtr, byte> Pinned = new();

    /// <summary>该窗口是否处于兼容显示（钉在最底）。</summary>
    public static bool IsPinned(IntPtr hwnd) => Pinned.ContainsKey(hwnd);

    /// <summary>WM_WINDOWPOSCHANGING：lParam 指向 WINDOWPOS；被钉住的窗口任何改 Z 序的请求都改成 HWND_BOTTOM。</summary>
    public static void FilterWindowPosChanging(IntPtr hwnd, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;
        var pos = Marshal.PtrToStructure<WindowPos>(lParam);
        var (after, flags) = FallbackZOrder.Adjust(IsPinned(hwnd), pos.InsertAfter, pos.Flags);
        if (after == pos.InsertAfter && flags == pos.Flags) return;
        pos.InsertAfter = after;
        pos.Flags = flags;
        Marshal.StructureToPtr(pos, lParam, false);
    }

    bool IDesktopWindowing.IsWindow(IntPtr hwnd) => NativeMethods.IsWindow(hwnd);

    /// <summary>真正的父窗口；顶层窗口（父为桌面根窗口）返回 Zero。</summary>
    public IntPtr GetParent(IntPtr hwnd) => GetAncestor(hwnd, GaParent) is var p && p == GetDesktopWindow() ? IntPtr.Zero : p;

    public bool SetParent(IntPtr child, IntPtr parent)
    {
        var style = GetWindowLongPtr(child, GwlStyle).ToInt64();
        // 先改样式再 SetParent（MSDN：子窗口须带 WS_CHILD；还原顶层时去掉 WS_CHILD）
        var wanted = parent == IntPtr.Zero ? (style & ~WsChild) | WsPopup : (style & ~WsPopup) | WsChild;
        if (wanted != style) SetWindowLongPtr(child, GwlStyle, new IntPtr(wanted));
        NativeMethods.SetParent(child, parent);
        // SetParent 的返回值是旧父窗口（可能本来就是 Zero），以结果为准
        var ok = parent == IntPtr.Zero ? GetParent(child) == IntPtr.Zero : GetAncestor(child, GaParent) == parent;
        if (ok && parent != IntPtr.Zero)
            SetWindowPos(child, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged | SwpShowWindow); // 子窗口最上面：SHELLDLL_DefView 之上
        else if (!ok && wanted != style)
            SetWindowLongPtr(child, GwlStyle, new IntPtr(style));
        return ok;
    }

    public void PinToBottom(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((ex & WsExNoActivate) == 0) SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex | WsExNoActivate));
        Pinned[hwnd] = 0;
        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    public void Unpin(IntPtr hwnd)
    {
        if (!Pinned.TryRemove(hwnd, out _)) return;
        var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex & ~WsExNoActivate));
    }

    public void SetBounds(IntPtr hwnd, ScreenRect screen)
    {
        var target = screen;
        var parent = GetParent(hwnd);
        if (parent != IntPtr.Zero)
        {
            // 屏幕 → 父窗口客户区：Progman 覆盖整个虚拟屏幕，客户区原点可能为负
            var origin = new Point();
            ClientToScreen(parent, ref origin);
            target = screen.RelativeTo(origin.X, origin.Y);
        }
        if (!SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, SwpNoZOrder | SwpNoActivate))
            throw new InvalidOperationException($"SetWindowPos 失败（Win32 {Marshal.GetLastPInvokeError()}）");
    }
}

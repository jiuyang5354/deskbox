using System.IO;
using System.Runtime.InteropServices;
using DeskBox.Application.Desktop;
using DeskBox.Application.Ports;
using DeskBox.Infrastructure.Logging;
using DeskBox.Infrastructure.Shell;

namespace DeskBox.Desktop.Tests;

/// <summary>
/// P0「方框完全看不到」（PR #24，PRD v1.2.7 §6 / TDD v1.0.13 §4.5）的真实桌面用例。
/// 硬规则：方框必须能看见。启动后要么挂在桌面层（GA_ROOT 是 Progman 或可见的 WorkerW），
/// 要么是可见的顶层置底窗口（兼容显示）。不存在第三种结果。
/// </summary>
public sealed partial class RealDesktopTests
{
    /// <summary>真实窗口树，但让选中的父窗口「看起来不可见」，用来强制挂载后校验失败。</summary>
    private sealed class HiddenHostTree(IDesktopWindowTree inner) : IDesktopWindowTree
    {
        public IntPtr FindProgman() => inner.FindProgman();
        public bool SpawnWorkerW(IntPtr progman) => inner.SpawnWorkerW(progman);
        public IReadOnlyList<IntPtr> TopLevelWindows() => inner.TopLevelWindows();
        public IntPtr FindChild(IntPtr parent, IntPtr after, string className) => inner.FindChild(parent, after, className);
        public string ClassName(IntPtr hwnd) => inner.ClassName(hwnd);
        public bool IsVisible(IntPtr hwnd) =>
            inner.ClassName(hwnd) is not (DesktopHostLocator.ProgmanClass or DesktopHostLocator.WorkerWClass) && inner.IsVisible(hwnd);
        public IntPtr Parent(IntPtr hwnd) => inner.Parent(hwnd);
        public IntPtr Root(IntPtr hwnd) => inner.Root(hwnd);
        public bool IsWindow(IntPtr hwnd) => inner.IsWindow(hwnd);
        public OsVersionInfo OsVersion => inner.OsVersion;
    }

    private static void AssertVisibleOnDesktopOrBottomFallback(IntPtr box, EmbedMode mode)
    {
        IsWindowVisible(box).Should().BeTrue("方框必须能看见（IsWindowVisible 检查整条祖先链）");
        var root = Tree.Root(box);
        if (root == box)
        {
            mode.Should().Be(EmbedMode.Degraded, "顶层窗口只允许出现在兼容显示");
            Win.GetParent(box).Should().Be(IntPtr.Zero);
            (GetWindowLongPtr(box, -20).ToInt64() & 0x08000000).Should().NotBe(0, "兼容显示必须带 WS_EX_NOACTIVATE");
            Win32DesktopWindowing.IsPinned(box).Should().BeTrue("兼容显示必须钉在最底");
        }
        else
        {
            mode.Should().Be(EmbedMode.Embedded);
            var ok = root == Tree.FindProgman() || (Tree.ClassName(root) == DesktopHostLocator.WorkerWClass && Tree.IsVisible(root));
            ok.Should().BeTrue($"GA_ROOT 必须是 Progman 或可见的 WorkerW，实际 {DesktopHostLocator.ClassChain(Tree, box)}");
        }
    }

    [SkippableFact]
    public Task After_start_box_is_visible_on_desktop_layer_or_as_bottom_fallback() => OnSta(async () =>
    {
        SkipIfNoDesktop();
        var dir = Path.Combine(Path.GetTempPath(), "deskbox-it-" + Guid.NewGuid().ToString("N"));
        var log = new RollingFileLog(dir);
        using var shell = new Win32ShellNotifications(SynchronizationContext.Current);
        using var sut = new DesktopEmbedder(new NoDb(), new Fences(Box()), Tree, Win, Monitors(), new NativeBoxes(), shell, log);
        try
        {
            await sut.StartAsync();
            AssertVisibleOnDesktopOrBottomFallback(sut.Windows.Values.Single(), sut.Mode);
            var text = File.ReadAllText(log.CurrentPath);
            text.Should().Contain("Embed fence=").And.Contain("os=[build ").And.Contain("layering=").And.Contain("path=");
        }
        finally
        {
            foreach (var h in sut.Windows.Values) DestroyWindow(h);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });

    [SkippableFact]
    public Task Forced_embed_failure_keeps_box_visible_and_logs_fallback_warning() => OnSta(async () =>
    {
        SkipIfNoDesktop();
        var dir = Path.Combine(Path.GetTempPath(), "deskbox-it-" + Guid.NewGuid().ToString("N"));
        var log = new RollingFileLog(dir);
        using var shell = new Win32ShellNotifications(SynchronizationContext.Current);
        using var sut = new DesktopEmbedder(new NoDb(), new Fences(Box()), new HiddenHostTree(Tree), Win, Monitors(), new NativeBoxes(), shell, log);
        try
        {
            await sut.StartAsync();
            sut.Mode.Should().Be(EmbedMode.Degraded);
            sut.Host.Should().Be(IntPtr.Zero);
            var box = sut.Windows.Values.Single();
            Tree.Root(box).Should().Be(box, "校验失败必须撤回挂载，退回顶层窗口");
            AssertVisibleOnDesktopOrBottomFallback(box, sut.Mode);
            File.ReadAllLines(log.CurrentPath).Should().Contain(l => l.Contains("[WARN]") && l.Contains("path=Fallback") && l.Contains("父窗口不可见"));
        }
        finally
        {
            foreach (var h in sut.Windows.Values) DestroyWindow(h);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr h);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr h, int index);
}

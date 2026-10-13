using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBox.Application.Desktop;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Geometry;
using DeskBox.Infrastructure.Shell;

namespace DeskBox.Desktop.Tests;

/// <summary>
/// 真实窗口嵌入与 Explorer 重启（TDD §4.5.4）。需要交互式桌面：没有 Progman / Explorer 时 <b>明确 Skip</b>（不静默通过），
/// 改由真机清单验证。
/// </summary>
[Trait("Category", "WindowsIntegration")]
[SupportedOSPlatform("windows")]
public sealed partial class RealDesktopTests
{
    private static readonly Win32DesktopWindowing Win = new();

    private sealed class Fences(params Fence[] rows) : IFenceStore
    {
        public Task<IReadOnlyList<Fence>> ListAsync(IUnitOfWork uow, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Fence>>(rows);
        public Task<Fence?> GetAsync(IUnitOfWork uow, Guid id, CancellationToken ct = default) => Task.FromResult(rows.FirstOrDefault(f => f.Id == id));
        public Task UpsertAsync(IUnitOfWork uow, Fence fence, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoDb : IUnitOfWorkFactory, IUnitOfWork
    {
        public Task<IUnitOfWork> BeginAsync(CancellationToken ct = default) => Task.FromResult<IUnitOfWork>(this);
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>在专用 STA 线程上创建 / 重建原生 STATIC 窗口（窗口所在线程需要消息循环才能被销毁回收，这里用 PeekMessage 泵）。</summary>
    private sealed class NativeBoxes : IBoxWindows
    {
        private readonly Dictionary<Guid, IntPtr> _h = [];
        public int Created { get; private set; }

        public IntPtr Ensure(Fence fence)
        {
            if (_h.TryGetValue(fence.Id, out var h) && IsWindow(h)) return h;
            Created++;
            h = CreateWindowEx(0x80, "STATIC", fence.Title, 0x10000000 /* WS_VISIBLE */ | 0x80000000 /* WS_POPUP */,
                0, 0, 200, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (h == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx {Marshal.GetLastPInvokeError()}");
            return _h[fence.Id] = h;
        }
    }

    private static void SkipIfNoDesktop()
    {
        var progman = Win.FindProgman();
        Skip.If(progman == IntPtr.Zero,
            "没有交互式桌面（找不到 Progman，托管 runner 无 Explorer 桌面）。该用例需按真机清单 docs/TDD.md §4.5.4 手工验证。");
        Skip.If(Process.GetProcessesByName("explorer").Length == 0, "explorer.exe 未运行，需真机清单验证。");
    }

    private static Fence Box() => new(Guid.NewGuid(), "DeskBox-it", "none", 10, 10, 200, 150);

    private static MonitorProvider Monitors() => new();

    private sealed class MonitorProvider : IMonitorService
    {
        private readonly Win32MonitorService _inner = new(null);
        public IReadOnlyList<MonitorInfo> GetMonitors() => _inner.GetMonitors();
        public event EventHandler? MonitorsChanged { add { } remove { } }
    }

    [SkippableFact]
    public Task Box_window_is_embedded_under_worker_w() => OnSta(async () =>
    {
        SkipIfNoDesktop();
        var boxes = new NativeBoxes();
        using var shell = new Win32ShellNotifications(SynchronizationContext.Current);
        using var sut = new DesktopEmbedder(new NoDb(), new Fences(Box()), Win, Monitors(), boxes, shell);

        await sut.StartAsync();

        sut.Mode.Should().Be(EmbedMode.Embedded);
        var worker = Win.FindWorkerW(Win.FindProgman());
        worker.Should().NotBe(IntPtr.Zero);
        Win.GetParent(sut.Windows.Values.Single()).Should().Be(worker);
        foreach (var h in sut.Windows.Values) DestroyWindow(h);
    });

    [SkippableFact]
    public Task Explorer_restart_reparents_box_to_the_new_worker_w() => OnSta(async () =>
    {
        SkipIfNoDesktop();
        var boxes = new NativeBoxes();
        using var shell = new Win32ShellNotifications(SynchronizationContext.Current);
        var fence = Box();
        using var sut = new DesktopEmbedder(new NoDb(), new Fences(fence), Win, Monitors(), boxes, shell);
        await sut.StartAsync();
        sut.Mode.Should().Be(EmbedMode.Embedded);
        var oldWorker = sut.WorkerW;
        var reattached = new TaskCompletionSource<EmbedMode>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.Attached += (_, m) => reattached.TrySetResult(m);

        try
        {
            foreach (var p in Process.GetProcessesByName("explorer")) { p.Kill(); p.WaitForExit(10_000); }
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            // TaskbarCreated 在监听线程上触发 → DesktopEmbedder 重新挂接
            (await reattached.Task.WaitAsync(TimeSpan.FromSeconds(60))).Should().Be(EmbedMode.Embedded);

            var newWorker = Win.FindWorkerW(Win.FindProgman());
            newWorker.Should().NotBe(IntPtr.Zero).And.NotBe(oldWorker);
            sut.WorkerW.Should().Be(newWorker);
            Win.GetParent(sut.Windows[fence.Id]).Should().Be(newWorker, "必须挂到新的 WorkerW，而不只是收到了消息");
        }
        finally
        {
            if (Process.GetProcessesByName("explorer").Length == 0) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Process.GetProcessesByName("explorer").Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(250);
            Process.GetProcessesByName("explorer").Should().NotBeEmpty("测试结束时 explorer.exe 必须重新运行");
            foreach (var h in sut.Windows.Values) DestroyWindow(h);
        }
    });

    /// <summary>窗口必须在有消息泵的线程上创建；这里在 STA 线程跑一个简易泵直到测试完成。</summary>
    private static Task OnSta(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            var ctx = new PumpContext();
            SynchronizationContext.SetSynchronizationContext(ctx);
            body().ContinueWith(r =>
            {
                if (r.Exception is { } e) done.TrySetException(e.InnerExceptions);
                else done.TrySetResult();
                ctx.Stop();
            }, TaskScheduler.Default);
            ctx.Run();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _q = [];
        private volatile bool _stop;
        public override void Post(SendOrPostCallback d, object? state) => _q.Add((d, state));
        public void Stop() { _stop = true; _q.Add((_ => { }, null)); }

        public void Run()
        {
            while (!_stop)
            {
                while (PeekMessage(out var m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(m); DispatchMessage(m); }
                if (_q.TryTake(out var w, 15)) w.Item1(w.Item2);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr H; public uint M; public IntPtr W, L; public uint T; public int X, Y; }

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out Msg m, IntPtr h, uint a, uint b, uint remove);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Msg m);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessage(in Msg m);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(uint ex, string cls, string? title, uint style, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr l);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr h);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr h);
}

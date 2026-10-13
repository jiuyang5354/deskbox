using System.Runtime.CompilerServices;
using DeskBox.Application.Desktop;
using DeskBox.Application.Fences;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Application.Startup;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Geometry;
using NSubstitute;

namespace DeskBox.Domain.Tests;

/// <summary>P0（Win11 24H2/25H2 方框看不见）：按窗口结构判断层级、挂载后校验、兼容显示钉在最底（TDD v1.0.13 §4.5）。</summary>
public class DesktopLayeringTests
{
    private static readonly MonitorInfo Primary = new("\\\\.\\DISPLAY1", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, true);

    private readonly FakeDesktop _desk = new();
    private readonly InMemoryFences _fences = new();
    private readonly IMonitorService _monitors = Substitute.For<IMonitorService>();
    private readonly RecordingEmbedLog _log = new();
    private readonly Fence _staging = new(Guid.NewGuid(), "暂存", Primary.Fingerprint, 24, 24, 280, 320, isStaging: true);

    public DesktopLayeringTests()
    {
        _monitors.GetMonitors().Returns([Primary]);
        _fences.Rows[_staging.Id] = _staging;
    }

    private DesktopEmbedder Sut() => new(new FakeDb(), _fences, _desk, _desk, _monitors, _desk, _desk, _log);

    private IntPtr Box(DesktopEmbedder sut) => sut.Windows[_staging.Id];

    [Fact]
    public async Task Legacy_layering_parents_to_the_wallpaper_worker_after_the_defview_holder()
    {
        _desk.Layering = FakeLayering.Legacy;
        _desk.StartExplorer(broadcast: false);
        using var sut = Sut();

        await sut.StartAsync();

        sut.Mode.Should().Be(EmbedMode.Embedded);
        sut.Layering.Should().Be(DesktopLayering.Legacy);
        _desk.GetParent(Box(sut)).Should().Be(_desk.Worker);
        _desk.ClassName(_desk.Worker).Should().Be("WorkerW");
        _desk.Root(Box(sut)).Should().Be(_desk.Worker, "旧层级的根是可见的 WorkerW");
        _desk.BottomMost.Should().BeEmpty();
        var r = _log.Reports.Should().ContainSingle().Subject;
        (r.Layering, r.Path, r.Validation).Should().Be((DesktopLayering.Legacy, EmbedPath.Embedded, "ok"));
        r.ParentChain.Should().StartWith("WorkerW(");
    }

    [Fact]
    public async Task Progman_child_layering_parents_to_progman_not_the_child_worker()
    {
        _desk.Layering = FakeLayering.ProgmanChild;
        _desk.StartExplorer(broadcast: false);
        using var sut = Sut();

        await sut.StartAsync();

        sut.Mode.Should().Be(EmbedMode.Embedded);
        sut.Layering.Should().Be(DesktopLayering.ProgmanChild);
        sut.Host.Should().Be(_desk.Progman);
        _desk.GetParent(Box(sut)).Should().Be(_desk.Progman, "24H2 起挂到 Progman，WorkerW 在它下面");
        _desk.Root(Box(sut)).Should().Be(_desk.Progman);
        _desk.Bounds[Box(sut)].Should().Be(new ScreenRect(24, 24, 280, 320));
        var r = _log.Reports.Should().ContainSingle().Subject;
        (r.Layering, r.Path).Should().Be((DesktopLayering.ProgmanChild, EmbedPath.Embedded));
        r.ParentChain.Should().StartWith("Progman(");
        r.Os.Should().Be(new OsVersionInfo("26200.9457", "25H2"), "日志同时记 build 号与 DisplayVersion");
    }

    [Fact]
    public async Task Hidden_worker_only_fails_validation_and_falls_back_to_a_visible_bottom_most_window()
    {
        _desk.Layering = FakeLayering.HiddenWorkerOnly;
        _desk.StartExplorer(broadcast: false);
        using var sut = Sut();

        await sut.StartAsync();

        sut.Mode.Should().Be(EmbedMode.Degraded);
        sut.Host.Should().Be(IntPtr.Zero);
        var box = Box(sut);
        _desk.GetParent(box).Should().Be(IntPtr.Zero, "撤回挂载，还原为顶层窗口");
        _desk.BottomMost.Should().Contain(box);
        _desk.IsVisible(box).Should().BeTrue("方框必须能看见");
        _desk.Bounds[box].Should().Be(new ScreenRect(24, 24, 280, 320));
        var r = _log.Reports.Should().ContainSingle().Subject;
        r.Path.Should().Be(EmbedPath.Fallback);
        r.Validation.Should().Contain("父窗口不可见").And.Contain("WorkerW");
        FenceUiText.TrayTooltip(sut.Mode).Should().Contain("桌面嵌入不可用，已切换为兼容显示");
    }

    [Fact]
    public async Task Taskbar_created_with_new_layering_reruns_detection_and_reparents_under_progman()
    {
        _desk.Layering = FakeLayering.Legacy;
        _desk.StartExplorer(broadcast: false);
        using var sut = Sut();
        await sut.StartAsync();
        var oldWorker = _desk.Worker;
        _desk.GetParent(Box(sut)).Should().Be(oldWorker);
        var spawns = _desk.SpawnCalls;

        // 系统升级到 24H2 后 Explorer 重启
        _desk.KillExplorer();
        _desk.Layering = FakeLayering.ProgmanChild;
        _desk.StartExplorer();

        sut.Mode.Should().Be(EmbedMode.Embedded);
        sut.Layering.Should().Be(DesktopLayering.ProgmanChild);
        _desk.SpawnCalls.Should().Be(spawns + 1, "每次 TaskbarCreated 都重新发 0x052C 并判断层级");
        _desk.GetParent(Box(sut)).Should().Be(_desk.Progman).And.NotBe(oldWorker);
        _log.Reports.Select(r => r.Layering).Should().Equal(DesktopLayering.Legacy, DesktopLayering.ProgmanChild);
    }

    [Fact]
    public async Task Fallback_retries_on_every_taskbar_created_and_restores_normal_tooltip_on_success()
    {
        _desk.Layering = FakeLayering.HiddenWorkerOnly;
        _desk.StartExplorer(broadcast: false);
        using var sut = Sut();
        var modes = new List<EmbedMode>();
        sut.Attached += (_, m) => modes.Add(m);
        await sut.StartAsync();
        var box = Box(sut);

        _desk.Broadcast(); // 仍然只有隐藏的 WorkerW：再试一次，仍兼容显示
        sut.Mode.Should().Be(EmbedMode.Degraded);

        _desk.SetVisible(_desk.Worker, true);
        _desk.Broadcast();

        sut.Mode.Should().Be(EmbedMode.Embedded);
        _desk.GetParent(box).Should().Be(_desk.Worker);
        _desk.BottomMost.Should().NotContain(box, "嵌入成功后取消置底钉住");
        modes.Should().Equal(EmbedMode.Degraded, EmbedMode.Degraded, EmbedMode.Embedded);
        modes.Select(FenceUiText.TrayTooltip).Should().Equal(
            "DeskBox：桌面嵌入不可用，已切换为兼容显示", "DeskBox：桌面嵌入不可用，已切换为兼容显示", "DeskBox");
    }

    /// <summary>「降级模式下方框浮到最前面」：修复前（WM_WINDOWPOSCHANGING 不处理）这条是红的。</summary>
    [Fact]
    public async Task Degraded_box_stays_below_other_windows_after_user_clicks_it()
    {
        using var sut = Sut(); // 没有 Explorer → 兼容显示
        await sut.StartAsync();
        var box = Box(sut);
        _desk.OpenAppWindow();
        _desk.IsBelowAllOtherWindows(box).Should().BeTrue();

        _desk.UserActivates(box);

        _desk.IsBelowAllOtherWindows(box).Should().BeTrue("Explorer 没在运行时方框不能盖在别的窗口上面");
    }

    [Fact]
    public void Fallback_z_order_rewrites_only_pinned_z_order_changes()
    {
        FallbackZOrder.Adjust(false, IntPtr.Zero, 0).Should().Be((IntPtr.Zero, 0u));
        FallbackZOrder.Adjust(true, IntPtr.Zero, FallbackZOrder.SwpNoZOrder).Should().Be((IntPtr.Zero, FallbackZOrder.SwpNoZOrder));
        FallbackZOrder.Adjust(true, (IntPtr)(-1) /* HWND_TOPMOST */, 0x2).Should().Be((FallbackZOrder.HwndBottom, 0x2u | FallbackZOrder.SwpNoActivate));
    }

    [Fact]
    public void Validator_rejects_off_screen_box_and_wrong_root()
    {
        _desk.Layering = FakeLayering.ProgmanChild;
        _desk.StartExplorer(broadcast: false);
        var host = DesktopHostLocator.Locate(_desk);
        var box = _desk.Ensure(_staging);
        _desk.SetParent(box, host.Host);
        IReadOnlyList<MonitorInfo> monitors = [Primary];

        EmbedValidator.Validate(_desk, box, new ScreenRect(10, 10, 100, 100), monitors, host.Progman).Should().Be(EmbedValidation.Passed);
        EmbedValidator.Validate(_desk, box, new ScreenRect(5000, 10, 100, 100), monitors, host.Progman).Detail.Should().Contain("没有交集");

        var other = _desk.OpenAppWindow();
        _desk.SetParent(box, other);
        EmbedValidator.Validate(_desk, box, new ScreenRect(10, 10, 100, 100), monitors, host.Progman).Detail.Should().Contain("根窗口");

        _desk.SetVisible(_desk.Progman, false);
        _desk.SetParent(box, _desk.Progman);
        EmbedValidator.Validate(_desk, box, new ScreenRect(10, 10, 100, 100), monitors, host.Progman).Detail.Should().Contain("Progman");

        FluentActions.Invoking(() => EmbedValidator.Validate(null!, box, default, monitors, host.Progman)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => EmbedValidator.Validate(_desk, box, default, null!, host.Progman)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Locator_reports_not_found_without_progman_or_worker()
    {
        DesktopHostLocator.Locate(_desk).Layering.Should().Be(DesktopLayering.NotFound, "没有 Progman");
        _desk.StartExplorer(broadcast: false);
        _desk.WorkerSpawns = false;
        var r = DesktopHostLocator.Locate(_desk);
        (r.Layering, r.Host, r.ClassChain).Should().Be((DesktopLayering.NotFound, IntPtr.Zero, "-"));
        DesktopHostLocator.ClassChain(_desk, IntPtr.Zero).Should().Be("-");
        FluentActions.Invoking(() => DesktopHostLocator.Locate(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => DesktopHostLocator.ClassChain(null!, IntPtr.Zero)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Locator_legacy_without_trailing_worker_is_not_found()
    {
        var tree = Substitute.For<IDesktopWindowTree>();
        tree.FindProgman().Returns((IntPtr)1);
        tree.IsWindow((IntPtr)1).Returns(true);
        tree.TopLevelWindows().Returns([(IntPtr)7, (IntPtr)1]);
        tree.FindChild((IntPtr)1, IntPtr.Zero, "SHELLDLL_DefView").Returns((IntPtr)2);
        DesktopHostLocator.Locate(tree).Layering.Should().Be(DesktopLayering.NotFound);
        tree.Received(1).SpawnWorkerW((IntPtr)1);
    }

    [Fact]
    public void Screen_rect_intersection_and_negative_virtual_origin()
    {
        var r = new ScreenRect(-1900, 100, 300, 200);
        r.RelativeTo(-1920, -200).Should().Be(new ScreenRect(20, 300, 300, 200), "Progman 客户区原点在虚拟屏幕左上（负坐标）");
        r.Intersects(new ScreenRect(-1920, 0, 1920, 1080)).Should().BeTrue();
        r.Intersects(new ScreenRect(0, 0, 1920, 1080)).Should().BeFalse("只贴边不算");
        new ScreenRect(0, 0, 0, 10).Intersects(new ScreenRect(0, 0, 10, 10)).Should().BeFalse();
        new ScreenRect(0, 0, 10, 10).Intersects(new ScreenRect(0, 20, 10, 10)).Should().BeFalse();
        new OsVersionInfo("26200.9457", "25H2").ToString().Should().Be("build 26200.9457, DisplayVersion 25H2");
    }
}

/// <summary>启动扫描日志：区分「枚举没执行」与「执行了但没写库」。</summary>
public class DesktopScanLogTests
{
    private sealed class Rec : IDesktopScanLog
    {
        public List<string> Lines { get; } = [];
        public void DesktopScanSkipped(string reason) => Lines.Add("skipped:" + reason);
        public void DesktopScanStarted() => Lines.Add("started");
        public void DesktopScanCompleted(int u, int p, int w) => Lines.Add($"completed:{u}/{p}/{w}");
    }

    private sealed class Parts(bool recoveryFails) : ISingleInstance, IDatabaseBootstrapper, IStagingSelfCheck, IStartupRecovery, IDesktopEnumerator, IStartupEmbedder, IStartupNotifier
    {
        public bool TryAcquire() => true;
        public void SignalRunningInstance() { }
        public Task MigrateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> EnsureAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<RecoveredEntry>> RecoverAsync(CancellationToken ct = default) =>
            recoveryFails ? throw new IOException("x") : Task.FromResult<IReadOnlyList<RecoveredEntry>>([]);
        public async IAsyncEnumerable<DesktopItem> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new DesktopItem(@"C:\Users\u\Desktop\a.txt", "01:00", false, false, DateTimeOffset.UnixEpoch);
            yield return new DesktopItem(@"C:\Users\u\Desktop\b.txt", "01:01", false, false, DateTimeOffset.UnixEpoch);
            yield return new DesktopItem(@"C:\Users\Public\Desktop\c.lnk", "01:02", false, true, DateTimeOffset.UnixEpoch);
        }
        public event EventHandler<DesktopChange>? Changed { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ReadOnlyMode(string reason) { }
    }

    [Fact]
    public async Task Ran_enumeration_logs_counts_and_rows_written()
    {
        var p = new Parts(false);
        var log = new Rec();
        await new StartupSequence(p, p, p, p, p, p, p, new OperatingMode(), log).RunAsync();
        log.Lines.Should().Equal("started", "completed:2/1/0");
    }

    [Fact]
    public async Task Read_only_logs_that_enumeration_was_skipped()
    {
        var p = new Parts(true);
        var log = new Rec();
        await new StartupSequence(p, p, p, p, p, p, p, new OperatingMode(), log).RunAsync();
        log.Lines.Should().ContainSingle().Which.Should().StartWith("skipped:").And.Contain("IOException");
    }
}

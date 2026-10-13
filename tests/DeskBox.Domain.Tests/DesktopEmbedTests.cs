using DeskBox.Application.Desktop;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Geometry;
using NSubstitute;

namespace DeskBox.Domain.Tests;

/// <summary>可编程的假 Win32 桌面：Explorer 可以"死掉""重启"，旧 WorkerW 的子窗口随之销毁。</summary>
internal sealed class FakeDesktop : IDesktopWindowing, IShellNotifications, IBoxWindows
{
    private long _next = 100;
    private readonly HashSet<IntPtr> _alive = [];
    private readonly Dictionary<IntPtr, IntPtr> _parent = [];
    private readonly Dictionary<Guid, IntPtr> _boxes = [];

    public IntPtr Progman { get; private set; }
    public IntPtr Worker { get; private set; }
    public bool WorkerSpawns { get; set; } = true;
    public bool SetParentFails { get; set; }
    public int FindProgmanCalls { get; private set; }
    public int SpawnCalls { get; private set; }
    public int Created { get; private set; }
    public Dictionary<IntPtr, ScreenRect> Bounds { get; } = [];
    public HashSet<IntPtr> BottomMost { get; } = [];

    public event EventHandler? TaskbarCreated;

    private IntPtr New()
    {
        var h = (IntPtr)_next++;
        _alive.Add(h);
        return h;
    }

    /// <summary>Explorer 启动：新的 Progman（WorkerW 要等 0x052C 才生成）；然后广播 TaskbarCreated。</summary>
    public void StartExplorer(bool broadcast = true)
    {
        Progman = New();
        Worker = IntPtr.Zero;
        if (broadcast) TaskbarCreated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Explorer 退出：Progman / WorkerW 及其所有子窗口销毁。</summary>
    public void KillExplorer()
    {
        foreach (var (child, parent) in _parent.ToList())
            if (parent == Worker) { _alive.Remove(child); _parent.Remove(child); }
        _alive.Remove(Progman);
        _alive.Remove(Worker);
        (Progman, Worker) = (IntPtr.Zero, IntPtr.Zero);
    }

    public void Broadcast() => TaskbarCreated?.Invoke(this, EventArgs.Empty);

    public IntPtr FindProgman()
    {
        FindProgmanCalls++;
        return Progman;
    }

    public bool SpawnWorkerW(IntPtr progman)
    {
        SpawnCalls++;
        if (progman != Progman || !_alive.Contains(progman)) return false;
        if (WorkerSpawns && Worker == IntPtr.Zero) Worker = New();
        return true;
    }

    public IntPtr FindWorkerW(IntPtr progman) => progman == Progman ? Worker : IntPtr.Zero;
    public bool IsWindow(IntPtr hwnd) => _alive.Contains(hwnd);
    public IntPtr GetParent(IntPtr hwnd) => _parent.GetValueOrDefault(hwnd);

    public bool SetParent(IntPtr child, IntPtr parent)
    {
        if (!_alive.Contains(child)) throw new InvalidOperationException("dead child " + child);
        if (parent == IntPtr.Zero) { _parent.Remove(child); return true; }
        if (SetParentFails || !_alive.Contains(parent)) return false;
        _parent[child] = parent;
        BottomMost.Remove(child);
        return true;
    }

    public void SetBottomMost(IntPtr hwnd) => BottomMost.Add(hwnd);

    public void SetBounds(IntPtr hwnd, ScreenRect screen)
    {
        if (!_alive.Contains(hwnd)) throw new InvalidOperationException("dead window");
        Bounds[hwnd] = screen;
    }

    public IntPtr Ensure(Fence fence)
    {
        if (_boxes.TryGetValue(fence.Id, out var h) && _alive.Contains(h)) return h;
        Created++;
        return _boxes[fence.Id] = New();
    }
}

internal sealed class InMemoryFences : IFenceStore
{
    public Dictionary<Guid, Fence> Rows { get; } = [];
    public Exception? Fail { get; set; }

    public Task<IReadOnlyList<Fence>> ListAsync(IUnitOfWork uow, CancellationToken ct = default) =>
        Fail is { } e ? Task.FromException<IReadOnlyList<Fence>>(e) : Task.FromResult<IReadOnlyList<Fence>>(Rows.Values.Select(Clone).ToList());

    public Task<Fence?> GetAsync(IUnitOfWork uow, Guid id, CancellationToken ct = default) =>
        Task.FromResult(Rows.TryGetValue(id, out var f) ? Clone(f) : null);

    public Task UpsertAsync(IUnitOfWork uow, Fence fence, CancellationToken ct = default)
    {
        Rows[fence.Id] = Clone(fence);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IUnitOfWork uow, Guid id, CancellationToken ct = default)
    {
        Rows.Remove(id);
        return Task.CompletedTask;
    }

    private static Fence Clone(Fence f) =>
        new(f.Id, f.Title, f.MonitorId, f.X, f.Y, f.Width, f.Height, f.Dpi, f.Opacity, f.IsStaging, f.IsCollapsed) { SortOrder = f.SortOrder };
}

public class DesktopEmbedTests
{
    private static readonly MonitorInfo Primary = new("\\\\.\\DISPLAY1", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, true);
    private static readonly MonitorInfo Right = new("\\\\.\\DISPLAY2", new(1920, 0, 2560, 1440), new(1920, 0, 2560, 1400), 144, false);

    private readonly FakeDesktop _desk = new();
    private readonly InMemoryFences _fences = new();
    private readonly IMonitorService _monitors = Substitute.For<IMonitorService>();
    private readonly Fence _a = new(Guid.NewGuid(), "A", Primary.Fingerprint, 100, 50, 300, 200);
    private readonly Fence _b = new(Guid.NewGuid(), "B", Right.Fingerprint, 10, 20, 200, 160, dpi: 144);

    public DesktopEmbedTests()
    {
        _monitors.GetMonitors().Returns([Primary, Right]);
        _fences.Rows[_a.Id] = _a;
        _fences.Rows[_b.Id] = _b;
    }

    private DesktopEmbedder Sut() => new(new FakeDb(), _fences, _desk, _monitors, _desk, _desk);

    [Fact]
    public async Task Embeds_every_box_under_the_spawned_worker_at_db_positions()
    {
        _desk.StartExplorer(broadcast: false);
        using var sut = Sut();

        await sut.StartAsync();

        sut.Mode.Should().Be(EmbedMode.Embedded);
        sut.WorkerW.Should().Be(_desk.Worker).And.NotBe(IntPtr.Zero);
        _desk.SpawnCalls.Should().Be(1, "先发 0x052C 再找 WorkerW");
        foreach (var h in sut.Windows.Values) _desk.GetParent(h).Should().Be(_desk.Worker);
        _desk.Bounds[sut.Windows[_a.Id]].Should().Be(new ScreenRect(100, 50, 300, 200));
        _desk.Bounds[sut.Windows[_b.Id]].Should().Be(new ScreenRect(1920 + 15, 30, 300, 240), "DIP × 1.5，相对工作区");
        _desk.BottomMost.Should().BeEmpty();
    }

    [Fact]
    public async Task No_progman_degrades_without_retrying_and_embeds_after_taskbar_created()
    {
        using var sut = Sut();
        var modes = new List<EmbedMode>();
        sut.Attached += (_, m) => modes.Add(m);

        await sut.StartAsync();

        sut.Mode.Should().Be(EmbedMode.Degraded);
        sut.WorkerW.Should().Be(IntPtr.Zero);
        sut.Windows.Values.Should().HaveCount(2).And.OnlyContain(h => _desk.BottomMost.Contains(h) && _desk.Bounds.ContainsKey(h),
            "降级为普通置底窗口，方框照常显示并放到数据库位置");
        _desk.FindProgmanCalls.Should().Be(1, "没有忙重试 / 轮询");
        await Task.Delay(50);
        _desk.FindProgmanCalls.Should().Be(1);

        _desk.StartExplorer(); // Explorer 起来 → TaskbarCreated

        sut.Mode.Should().Be(EmbedMode.Embedded);
        foreach (var h in sut.Windows.Values) _desk.GetParent(h).Should().Be(_desk.Worker);
        modes.Should().Equal(EmbedMode.Degraded, EmbedMode.Embedded);
        _desk.Created.Should().Be(2, "降级窗口还活着，直接复用");
    }

    [Fact]
    public async Task Progman_without_worker_or_failing_set_parent_degrades()
    {
        _desk.StartExplorer(broadcast: false);
        _desk.WorkerSpawns = false;
        using var sut = Sut();
        await sut.StartAsync();
        sut.Mode.Should().Be(EmbedMode.Degraded);

        _desk.WorkerSpawns = true;
        _desk.SetParentFails = true;
        await sut.AttachAllAsync();
        sut.Mode.Should().Be(EmbedMode.Degraded);
        sut.Windows.Values.Should().OnlyContain(h => _desk.BottomMost.Contains(h) && _desk.GetParent(h) == IntPtr.Zero);

        _desk.SetParentFails = false;
        await sut.AttachAllAsync();
        sut.Mode.Should().Be(EmbedMode.Embedded);
        // 已嵌入的窗口这一轮 SetParent 失败时，要先还原为顶层窗口再置底
        _desk.SetParentFails = true;
        await sut.AttachAllAsync();
        sut.Windows.Values.Should().OnlyContain(h => _desk.GetParent(h) == IntPtr.Zero && _desk.BottomMost.Contains(h));
    }

    [Fact]
    public async Task Explorer_restart_discards_old_handles_and_reparents_recreated_boxes_to_the_new_worker_at_db_positions()
    {
        _desk.StartExplorer(broadcast: false);
        using var sut = Sut();
        await sut.StartAsync();
        var oldWorker = sut.WorkerW;
        var oldA = sut.Windows[_a.Id];
        _desk.Bounds[oldA] = new ScreenRect(999, 999, 500, 500); // 重启前窗口被拖到别处但没保存

        _desk.KillExplorer();
        _desk.IsWindow(oldA).Should().BeFalse("旧 WorkerW 的子窗口随之销毁");
        _fences.Rows[_a.Id].MoveTo(Primary.Fingerprint, 40, 60, 96); // 数据库里是最新的已保存位置
        _desk.StartExplorer();

        sut.Mode.Should().Be(EmbedMode.Embedded);
        sut.WorkerW.Should().Be(_desk.Worker).And.NotBe(oldWorker);
        var newA = sut.Windows[_a.Id];
        newA.Should().NotBe(oldA);
        _desk.GetParent(newA).Should().Be(_desk.Worker, "挂到新的 WorkerW，而不只是收到了消息");
        _desk.GetParent(sut.Windows[_b.Id]).Should().Be(_desk.Worker);
        _desk.Bounds[newA].Should().Be(new ScreenRect(40, 60, 300, 200), "位置来自数据库，不是重启前的窗口状态");
        _desk.Created.Should().Be(4);
    }

    [Fact]
    public async Task Failure_inside_taskbar_created_is_reported_not_thrown_and_dispose_unsubscribes()
    {
        var sut = Sut();
        await sut.StartAsync();
        await sut.StartAsync(); // 重复 Start 不重复订阅
        var errors = new List<Exception>();
        sut.AttachFailed += (_, e) => errors.Add(e);
        _fences.Fail = new InvalidOperationException("db down");

        FluentActions.Invoking(() => _desk.Broadcast()).Should().NotThrow();
        errors.Should().ContainSingle().Which.Message.Should().Be("db down");
        sut.Mode.Should().Be(EmbedMode.Degraded);

        _fences.Fail = null;
        sut.Dispose();
        sut.Dispose();
        var before = _desk.FindProgmanCalls;
        _desk.StartExplorer();
        _desk.FindProgmanCalls.Should().Be(before, "Dispose 后不再响应");
        await FluentActions.Awaiting(() => sut.AttachAllAsync()).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => sut.StartAsync()).Should().ThrowAsync<ObjectDisposedException>();
        new DesktopEmbedder(new FakeDb(), _fences, _desk, _monitors, _desk, _desk).Dispose();
    }

    [Fact]
    public void Constructor_guards()
    {
        var db = new FakeDb();
        FluentActions.Invoking(() => new DesktopEmbedder(null!, _fences, _desk, _monitors, _desk, _desk)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new DesktopEmbedder(db, null!, _desk, _monitors, _desk, _desk)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new DesktopEmbedder(db, _fences, null!, _monitors, _desk, _desk)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new DesktopEmbedder(db, _fences, _desk, null!, _desk, _desk)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new DesktopEmbedder(db, _fences, _desk, _monitors, null!, _desk)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new DesktopEmbedder(db, _fences, _desk, _monitors, _desk, null!)).Should().Throw<ArgumentNullException>();
        new DesktopEmbedder(db, _fences, _desk, _monitors, _desk, _desk).Mode.Should().Be(EmbedMode.None);
    }
}

/// <summary>QA (b)(c)：显示器移除、DPI 往返。</summary>
public class FenceLayoutTests
{
    private static readonly MonitorInfo Primary = new("P", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 120, true);
    private static readonly IReadOnlyList<MonitorInfo> OnlyPrimary = [Primary];

    [Theory]
    [InlineData(2000, 100, 400, 300)]   // 原显示器在右侧：完全在主显示器之外
    [InlineData(-5000, -3000, 400, 300)] // 原显示器在左上：完全在外（负坐标）
    [InlineData(1400, 900, 400, 300)]   // 一半在外（右下角越界）
    [InlineData(-150, 500, 400, 300)]   // 一半在外（左边越界）
    [InlineData(0, 0, 3000, 2000)]      // 比主显示器工作区还大
    public void Box_on_removed_monitor_ends_entirely_inside_primary_work_area(int x, int y, int w, int h)
    {
        var fence = new Fence(Guid.NewGuid(), "F", "gone-monitor", x, y, w, h);

        var r = FenceLayout.Place(fence, OnlyPrimary);

        Primary.WorkArea.Contains(r).Should().BeTrue($"{r} 必须完全在 {Primary.WorkArea} 内");
        (fence.MonitorId, fence.X, fence.Y).Should().Be(("gone-monitor", x, y), "不回写，显示器回来后回到原处");
    }

    [Fact]
    public void Half_off_screen_keeps_size_and_is_shifted_minimally()
    {
        var fence = new Fence(Guid.NewGuid(), "F", "gone", 1400, 900, 400, 160); // 96 DIP × 1.25
        FenceLayout.Place(fence, OnlyPrimary).Should().Be(new ScreenRect(1920 - 500, 1040 - 200, 500, 200));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Stored_position_round_trips_exactly_through_any_dpi(int dpi)
    {
        var monitor = new MonitorInfo("M", new(-2560, -200, 2560, 1440), new(-2560, -160, 2560, 1400), dpi, true);
        IReadOnlyList<MonitorInfo> monitors = [monitor];
        foreach (var (x, y, w, h) in new[] { (0, 0, 120, 120), (1, 3, 121, 333), (37, 41, 401, 203), (777, 333, 333, 177), (5, 7, 123, 125) })
        {
            var fence = new Fence(Guid.NewGuid(), "F", "M", x, y, w, h);
            var screen = FenceLayout.Place(fence, monitors);
            FenceLayout.Capture(fence, screen, monitors);
            (fence.X, fence.Y, fence.Width, fence.Height, fence.MonitorId).Should().Be((x, y, w, h, "M"), $"dpi {dpi}");
            fence.Dpi.Should().Be(dpi);
        }

        // 穷举一段：DIP → 物理 → DIP 精确
        for (var d = -2000; d <= 2000; d++) DesktopGeometry.ToDip(DesktopGeometry.ToPhysical(d, dpi), dpi).Should().Be(d);
    }

    [Fact]
    public void Capture_uses_monitor_under_center_else_primary()
    {
        var right = new MonitorInfo("R", new(1920, 0, 1920, 1080), new(1920, 0, 1920, 1040), 192, false);
        IReadOnlyList<MonitorInfo> monitors = [Primary, right];
        var fence = new Fence(Guid.NewGuid(), "F", "P", 0, 0, 200, 200);

        FenceLayout.Capture(fence, new ScreenRect(1920 + 200, 100, 400, 400), monitors);
        (fence.MonitorId, fence.X, fence.Y, fence.Width, fence.Dpi).Should().Be(("R", 100, 50, 200, 192));

        FenceLayout.Capture(fence, new ScreenRect(-9000, -9000, 240, 240), monitors);
        fence.MonitorId.Should().Be("P");
    }

    [Fact]
    public void Primary_fallback_and_guards()
    {
        var m = new MonitorInfo("X", new(0, 0, 800, 600), new(0, 0, 800, 600), 96, false);
        FenceLayout.Primary([m]).Should().Be(m);
        FluentActions.Invoking(() => FenceLayout.Primary([])).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => FenceLayout.Primary(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => FenceLayout.Place(null!, OnlyPrimary)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => FenceLayout.Capture(null!, default, OnlyPrimary)).Should().Throw<ArgumentNullException>();
        var r = new ScreenRect(1, 2, 3, 4);
        (r.Right, r.Bottom, r.ContainsPoint(1, 2), r.ContainsPoint(4, 2)).Should().Be((4, 6, true, false));
    }
}

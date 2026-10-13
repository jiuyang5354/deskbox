using DeskBox.Application.Ports;
using DeskBox.Application.Startup;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Geometry;
using NSubstitute;

namespace DeskBox.Domain.Tests;

public class StagingSelfCheckTests
{
    private static readonly MonitorInfo Primary = new("M1", new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040), 144, true);
    private readonly FakeDb _db = new();
    private readonly InMemoryFences _fences = new();
    private readonly IStagingSelfCheckLog _log = Substitute.For<IStagingSelfCheckLog>();
    private readonly IMonitorService _monitors = Substitute.For<IMonitorService>();

    public StagingSelfCheckTests() => _monitors.GetMonitors().Returns([Primary]);

    private StagingFenceSelfCheck Sut(IFenceStore? store = null, Guid? id = null) =>
        new(_db, store ?? _fences, _monitors, _log, id is { } g ? () => g : null);

    [Fact]
    public async Task Missing_staging_is_recreated_once_at_default_position_with_warning()
    {
        var normal = new Fence(Guid.NewGuid(), "工作", "M1", 0, 0, 200, 200);
        _fences.Rows[normal.Id] = normal;
        var id = Guid.NewGuid();

        (await Sut(id: id).EnsureAsync()).Should().BeTrue();

        var staging = _fences.Rows.Values.Where(f => f.IsStaging).Should().ContainSingle().Subject;
        (staging.Id, staging.Title, staging.MonitorId, staging.X, staging.Y, staging.Width, staging.Height, staging.Dpi)
            .Should().Be((id, StagingFenceSelfCheck.DefaultTitle, "M1", 24, 24, 280, 320, 144));
        _log.Received(1).StagingFenceRecreated(id, "M1");
        _db.Commits.Should().Be(1, "查与建在同一个事务");
    }

    [Fact]
    public async Task Running_twice_creates_nothing_more_and_logs_once()
    {
        await Sut().EnsureAsync();
        (await Sut().EnsureAsync()).Should().BeFalse();
        _fences.Rows.Values.Count(f => f.IsStaging).Should().Be(1);
        _log.ReceivedWithAnyArgs(1).StagingFenceRecreated(default, default!);
    }

    [Fact]
    public async Task Existing_staging_is_left_alone()
    {
        var s = new Fence(Guid.NewGuid(), "暂存", "M1", 5, 5, 200, 200, isStaging: true);
        _fences.Rows[s.Id] = s;
        (await Sut().EnsureAsync()).Should().BeFalse();
        _fences.Rows.Should().ContainSingle();
        _log.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task No_monitor_uses_fallback_monitor_id()
    {
        _monitors.GetMonitors().Returns([]);
        await Sut().EnsureAsync();
        _fences.Rows.Values.Single().MonitorId.Should().Be(StagingFenceSelfCheck.FallbackMonitorId);
    }

    [Fact]
    public async Task Write_failure_propagates_and_logs_nothing_so_startup_goes_read_only()
    {
        var store = Substitute.For<IFenceStore>();
        store.ListAsync(default!, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Fence>>([]));
        store.UpsertAsync(default!, default!, default).ReturnsForAnyArgs(Task.FromException(new IOException("disk full")));
        await FluentActions.Awaiting(() => Sut(store).EnsureAsync()).Should().ThrowAsync<IOException>();
        _log.ReceivedCalls().Should().BeEmpty();
        _db.Commits.Should().Be(0, "失败回滚");

        // 接到真实启动顺序：进入只读
        var instance = Substitute.For<ISingleInstance>();
        instance.TryAcquire().Returns(true);
        var recovery = Substitute.For<IStartupRecovery>();
        recovery.RecoverAsync(default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<DeskBox.Application.Files.RecoveredEntry>>([]));
        var desktop = Substitute.For<IDesktopEnumerator>();
        var mode = new OperatingMode();
        var r = await new StartupSequence(instance, Substitute.For<IDatabaseBootstrapper>(), Sut(store), recovery, desktop,
            Substitute.For<IStartupEmbedder>(), Substitute.For<IStartupNotifier>(), mode).RunAsync();
        r.Outcome.Should().Be(StartupOutcome.ReadOnly);
        mode.IsReadOnly.Should().BeTrue();
        desktop.ReceivedCalls().Should().BeEmpty("只读时不枚举桌面");
    }
}

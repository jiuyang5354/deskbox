using System.Runtime.CompilerServices;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Application.Startup;
using DeskBox.Domain.Files;
using NSubstitute;

namespace DeskBox.Domain.Tests;

public sealed class StartupSequenceTests
{
    private sealed class Fakes : ISingleInstance, IDatabaseBootstrapper, IStartupRecovery, IDesktopEnumerator, IStartupEmbedder, IStartupNotifier
    {
        public List<string> Calls { get; } = [];
        public bool Acquire { get; set; } = true;
        public Exception? RecoveryThrows { get; set; }
        public IReadOnlyList<RecoveredEntry> Recovered { get; set; } = [];
        public string? NotifiedReason { get; private set; }

        public bool TryAcquire() { Calls.Add("mutex"); return Acquire; }
        public void SignalRunningInstance() => Calls.Add("signal");
        public Task MigrateAsync(CancellationToken ct = default) { Calls.Add("migrate"); return Task.CompletedTask; }
        public Task<IReadOnlyList<RecoveredEntry>> RecoverAsync(CancellationToken ct = default)
        {
            Calls.Add("recover");
            return RecoveryThrows is { } ex ? Task.FromException<IReadOnlyList<RecoveredEntry>>(ex) : Task.FromResult(Recovered);
        }
        public async IAsyncEnumerable<DesktopItem> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Add("enumerate");
            await Task.Yield();
            yield return new DesktopItem(@"C:\Users\u\Desktop\a.txt", "01:00", false, false, DateTimeOffset.UnixEpoch);
        }
        public event EventHandler<DesktopChange>? Changed { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) { Calls.Add("embed"); return Task.CompletedTask; }
        public void ReadOnlyMode(string reason) { Calls.Add("notify-readonly"); NotifiedReason = reason; }
    }

    private static (StartupSequence, Fakes, OperatingMode) Make()
    {
        var f = new Fakes();
        var mode = new OperatingMode();
        return (new StartupSequence(f, f, f, f, f, f, mode), f, mode);
    }

    [Fact]
    public async Task Runs_mutex_then_migrate_then_recovery_then_enumerate_then_embed()
    {
        var (sut, f, mode) = Make();
        var r = await sut.RunAsync();
        f.Calls.Should().Equal("mutex", "migrate", "recover", "enumerate", "embed");
        f.Calls.IndexOf("recover").Should().BeLessThan(f.Calls.IndexOf("enumerate")).And.BeLessThan(f.Calls.IndexOf("embed"));
        r.Outcome.Should().Be(StartupOutcome.Running);
        r.DesktopItems.Should().ContainSingle();
        mode.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task Second_instance_signals_running_one_and_touches_nothing_else()
    {
        var (sut, f, _) = Make();
        f.Acquire = false;
        var r = await sut.RunAsync();
        r.Outcome.Should().Be(StartupOutcome.SecondInstance);
        f.Calls.Should().Equal("mutex", "signal");
    }

    [Fact]
    public async Task Recovery_exception_enters_read_only_skips_enumeration_and_still_shows_boxes()
    {
        var (sut, f, mode) = Make();
        f.RecoveryThrows = new IOException("disk");
        var r = await sut.RunAsync();
        r.Outcome.Should().Be(StartupOutcome.ReadOnly);
        f.Calls.Should().Equal("mutex", "migrate", "recover", "notify-readonly", "embed");
        r.DesktopItems.Should().BeEmpty();
        mode.IsReadOnly.Should().BeTrue();
        f.NotifiedReason.Should().Contain("IOException");
    }

    [Fact]
    public async Task Entry_left_pending_counts_as_recovery_failure()
    {
        var (sut, f, mode) = Make();
        f.Recovered = [new(Guid.NewGuid(), RecoveryOutcome.Failed, null), new(Guid.NewGuid(), RecoveryOutcome.Lost, null)];
        var r = await sut.RunAsync();
        r.Outcome.Should().Be(StartupOutcome.ReadOnly);
        r.ReadOnlyReason.Should().Contain("1 条");
        f.Calls.Should().NotContain("enumerate");
        mode.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Resolved_entries_do_not_block_startup()
    {
        var (sut, f, _) = Make();
        f.Recovered = [new(Guid.NewGuid(), RecoveryOutcome.Aborted, "a"), new(Guid.NewGuid(), RecoveryOutcome.Lost, null)];
        (await sut.RunAsync()).Outcome.Should().Be(StartupOutcome.Running);
    }

    [Fact]
    public async Task Cancellation_from_recovery_is_not_swallowed()
    {
        var (sut, f, mode) = Make();
        f.RecoveryThrows = new OperationCanceledException();
        await FluentActions.Awaiting(() => sut.RunAsync()).Should().ThrowAsync<OperationCanceledException>();
        f.Calls.Should().NotContain("enumerate").And.NotContain("embed");
        mode.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task Read_only_mover_never_calls_inner_mover()
    {
        var inner = Substitute.For<IFileMover>();
        var mode = new OperatingMode();
        var sut = new GatedFileMover(inner, mode);
        await sut.MoveAsync("a", "b", null);
        await inner.Received(1).MoveAsync("a", "b", null, Arg.Any<CancellationToken>());

        mode.EnterReadOnly("恢复失败");
        mode.EnterReadOnly("第二次不覆盖");
        mode.ReadOnlyReason.Should().Be("恢复失败");
        inner.ClearReceivedCalls();
        await FluentActions.Awaiting(() => sut.MoveAsync("a", "b", null)).Should().ThrowAsync<ReadOnlyModeException>()
            .WithMessage("*恢复失败*");
        await inner.DidNotReceiveWithAnyArgs().MoveAsync(default!, default!, default, default);
    }

    [Fact]
    public void Guards_reject_null()
    {
        FluentActions.Invoking(() => new GatedFileMover(null!, new OperatingMode())).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new GatedFileMover(Substitute.For<IFileMover>(), null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new OperatingMode().EnterReadOnly(" ")).Should().Throw<ArgumentException>();
    }
}

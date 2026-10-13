using DeskBox.Domain.Files;
using DeskBox.Domain.Safety;

namespace DeskBox.Domain.Tests;

public class MoveResultTests
{
    [Fact]
    public void Match_dispatches()
    {
        MoveResult moved = new MoveResult.Moved("a", "b");
        MoveResult skipped = new MoveResult.Skipped("a", SkipReason.Locked);
        moved.Match(m => m.TargetPath, _ => "x").Should().Be("b");
        skipped.Match(_ => "x", s => s.Reason.ToString()).Should().Be("Locked");
        moved.SourcePath.Should().Be("a");
    }

    [Theory]
    [InlineData(SkipReason.Locked, "占用")]
    [InlineData(SkipReason.ReadOnly, "只读")]
    [InlineData(SkipReason.CrossVolume, "分区")]
    [InlineData(SkipReason.CloudOnly, "云端")]
    [InlineData(SkipReason.NeedsAdmin, "管理员")]
    [InlineData(SkipReason.NotFound, "不存在")]
    public void Skip_messages(SkipReason r, string fragment) =>
        new MoveResult.Skipped("a", r).Message.Should().Contain(fragment);

    [Fact]
    public void Unknown_reason_throws() =>
        FluentActions.Invoking(() => new MoveResult.Skipped("a", (SkipReason)42).Message).Should().Throw<ArgumentOutOfRangeException>();
}

public class SafeModePolicyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static RunRecord Crash(int startMin, double afterSec) => new(T0.AddMinutes(startMin), T0.AddMinutes(startMin).AddSeconds(afterSec));
    private static RunRecord Clean(int startMin) => new(T0.AddMinutes(startMin), null);

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(30.001, false)]
    [InlineData(-1, false)]
    public void Early_crash_window(double sec, bool expected) =>
        SafeModePolicy.IsEarlyCrash(T0, T0.AddSeconds(sec)).Should().Be(expected);

    [Fact]
    public void Two_consecutive_early_crashes_enter_safe_mode() =>
        SafeModePolicy.ShouldEnterSafeMode([Clean(0), Crash(1, 5), Crash(2, 29)]).Should().BeTrue();

    [Fact]
    public void Single_early_crash_does_not() =>
        SafeModePolicy.ShouldEnterSafeMode([Crash(1, 5)]).Should().BeFalse();

    [Fact]
    public void Clean_run_or_late_crash_resets_streak()
    {
        SafeModePolicy.ShouldEnterSafeMode([Crash(1, 5), Clean(2), Crash(3, 5)]).Should().BeFalse();
        SafeModePolicy.ShouldEnterSafeMode([Crash(1, 5), Crash(2, 31)]).Should().BeFalse();
        SafeModePolicy.ShouldEnterSafeMode([Crash(1, 5), Crash(2, 5), Clean(3)]).Should().BeFalse();
        SafeModePolicy.ShouldEnterSafeMode([]).Should().BeFalse();
    }

    [Fact]
    public void Null_throws() =>
        FluentActions.Invoking(() => SafeModePolicy.ShouldEnterSafeMode(null!)).Should().Throw<ArgumentNullException>();
}

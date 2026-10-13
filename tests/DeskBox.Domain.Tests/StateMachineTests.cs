using DeskBox.Domain;
using DeskBox.Domain.StateMachines;

namespace DeskBox.Domain.Tests;

public class FenceStateMachineTests
{
    public static TheoryData<FenceState, FenceTrigger> IllegalTransitions()
    {
        var data = new TheoryData<FenceState, FenceTrigger>();
        foreach (var s in Enum.GetValues<FenceState>())
            foreach (var t in Enum.GetValues<FenceTrigger>())
                if (!FenceStateMachine.Table.ContainsKey((s, t))) data.Add(s, t);
        return data;
    }

    public static TheoryData<FenceState, FenceTrigger, FenceState> LegalTransitions()
    {
        var data = new TheoryData<FenceState, FenceTrigger, FenceState>();
        foreach (var (f, t, to) in StateMachine<FenceState, FenceTrigger>.Transitions(FenceStateMachine.Table)) data.Add(f, t, to);
        return data;
    }

    // 通过合法路径把状态机驱动到目标状态
    internal static FenceStateMachine At(FenceState s)
    {
        var m = new FenceStateMachine();
        switch (s)
        {
            case FenceState.Collapsed: m.Fire(FenceTrigger.Collapse); break;
            case FenceState.Hidden: m.Fire(FenceTrigger.Hide); break;
            case FenceState.Deleted: m.Fire(FenceTrigger.Delete); break;
        }
        m.State.Should().Be(s);
        return m;
    }

    [Theory, MemberData(nameof(IllegalTransitions))]
    public void Illegal_transition_throws_and_keeps_state(FenceState from, FenceTrigger trigger)
    {
        var m = At(from);
        m.CanFire(trigger).Should().BeFalse();
        var ex = FluentActions.Invoking(() => m.Fire(trigger)).Should().Throw<InvalidTransitionException>().Which;
        (ex.Machine, ex.From, ex.Trigger).Should().Be((nameof(FenceStateMachine), from.ToString(), trigger.ToString()));
        m.State.Should().Be(from);
    }

    [Theory, MemberData(nameof(LegalTransitions))]
    public void Legal_transition_moves_state(FenceState from, FenceTrigger trigger, FenceState to)
    {
        var m = At(from);
        m.CanFire(trigger).Should().BeTrue();
        m.Fire(trigger).Should().Be(to);
        m.State.Should().Be(to);
    }

    [Fact]
    public void Deleted_is_terminal() =>
        Enum.GetValues<FenceTrigger>().Should().OnlyContain(t => !At(FenceState.Deleted).CanFire(t));
}

public class OrganizeJobStateMachineTests
{
    public static TheoryData<OrganizeJobState, OrganizeJobTrigger> IllegalTransitions()
    {
        var data = new TheoryData<OrganizeJobState, OrganizeJobTrigger>();
        foreach (var s in Enum.GetValues<OrganizeJobState>())
            foreach (var t in Enum.GetValues<OrganizeJobTrigger>())
                if (!OrganizeJobStateMachine.Table.ContainsKey((s, t))) data.Add(s, t);
        return data;
    }

    public static TheoryData<OrganizeJobState, OrganizeJobTrigger, OrganizeJobState> LegalTransitions()
    {
        var data = new TheoryData<OrganizeJobState, OrganizeJobTrigger, OrganizeJobState>();
        foreach (var (f, t, to) in StateMachine<OrganizeJobState, OrganizeJobTrigger>.Transitions(OrganizeJobStateMachine.Table)) data.Add(f, t, to);
        return data;
    }

    private static readonly Dictionary<OrganizeJobState, OrganizeJobTrigger[]> Paths = new()
    {
        [OrganizeJobState.Created] = [],
        [OrganizeJobState.Snapshotting] = [OrganizeJobTrigger.Start],
        [OrganizeJobState.Classifying] = [OrganizeJobTrigger.Start, OrganizeJobTrigger.SnapshotSaved],
        [OrganizeJobState.Applying] = [OrganizeJobTrigger.Start, OrganizeJobTrigger.SnapshotSaved, OrganizeJobTrigger.Classified],
        [OrganizeJobState.Completed] = [OrganizeJobTrigger.Start, OrganizeJobTrigger.SnapshotSaved, OrganizeJobTrigger.Classified, OrganizeJobTrigger.AllApplied],
        [OrganizeJobState.PartiallyCompleted] = [OrganizeJobTrigger.Start, OrganizeJobTrigger.SnapshotSaved, OrganizeJobTrigger.Classified, OrganizeJobTrigger.SomeSkipped],
        [OrganizeJobState.RollingBack] = [OrganizeJobTrigger.Start, OrganizeJobTrigger.SnapshotSaved, OrganizeJobTrigger.Classified, OrganizeJobTrigger.Fail],
        [OrganizeJobState.Failed] = [OrganizeJobTrigger.Start, OrganizeJobTrigger.Fail],
        [OrganizeJobState.Cancelled] = [OrganizeJobTrigger.Start, OrganizeJobTrigger.SnapshotSaved, OrganizeJobTrigger.Cancel],
        [OrganizeJobState.Restored] = [OrganizeJobTrigger.Start, OrganizeJobTrigger.SnapshotSaved, OrganizeJobTrigger.Classified, OrganizeJobTrigger.AllApplied, OrganizeJobTrigger.Restore],
    };

    private static OrganizeJobStateMachine At(OrganizeJobState s)
    {
        var m = new OrganizeJobStateMachine();
        foreach (var t in Paths[s]) m.Fire(t);
        m.State.Should().Be(s);
        return m;
    }

    [Theory, MemberData(nameof(IllegalTransitions))]
    public void Illegal_transition_throws_and_keeps_state(OrganizeJobState from, OrganizeJobTrigger trigger)
    {
        var m = At(from);
        FluentActions.Invoking(() => m.Fire(trigger)).Should().Throw<InvalidTransitionException>()
            .Which.From.Should().Be(from.ToString());
        m.State.Should().Be(from);
    }

    [Theory, MemberData(nameof(LegalTransitions))]
    public void Legal_transition_moves_state(OrganizeJobState from, OrganizeJobTrigger trigger, OrganizeJobState to)
    {
        var m = At(from);
        m.Fire(trigger).Should().Be(to);
    }

    [Theory]
    [InlineData(OrganizeJobState.Failed, true)]
    [InlineData(OrganizeJobState.Cancelled, true)]
    [InlineData(OrganizeJobState.Restored, true)]
    [InlineData(OrganizeJobState.Completed, false)]
    [InlineData(OrganizeJobState.Applying, false)]
    public void Terminal_states(OrganizeJobState s, bool terminal) => At(s).IsTerminal.Should().Be(terminal);

    [Fact]
    public void Cancel_only_from_classifying()
    {
        OrganizeJobStateMachine.Table.Where(kv => kv.Key.Item2 == OrganizeJobTrigger.Cancel)
            .Select(kv => kv.Key.Item1).Should().Equal(OrganizeJobState.Classifying);
        At(OrganizeJobState.Applying).CanFire(OrganizeJobTrigger.Cancel).Should().BeFalse();
    }

    [Fact]
    public void Fatal_error_while_applying_rolls_back_then_fails()
    {
        var m = At(OrganizeJobState.Applying);
        m.Fire(OrganizeJobTrigger.Fail).Should().Be(OrganizeJobState.RollingBack);
        m.IsTerminal.Should().BeFalse();
        FluentActions.Invoking(() => m.Fire(OrganizeJobTrigger.Cancel)).Should().Throw<InvalidTransitionException>();
        m.Fire(OrganizeJobTrigger.RolledBack).Should().Be(OrganizeJobState.Failed);
        m.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Cannot_apply_before_snapshot()
    {
        var m = new OrganizeJobStateMachine();
        m.Fire(OrganizeJobTrigger.Start);
        FluentActions.Invoking(() => m.Fire(OrganizeJobTrigger.Classified)).Should().Throw<InvalidTransitionException>();
        FluentActions.Invoking(() => m.Fire(OrganizeJobTrigger.AllApplied)).Should().Throw<InvalidTransitionException>();
    }
}

namespace DeskBox.Domain.StateMachines;

public enum FenceState { Expanded, Collapsed, Hidden, Orphaned, Deleted }

public enum FenceTrigger { Collapse, Expand, Hide, Show, MonitorLost, MonitorRestored, Delete }

/// <summary>方框生命周期（PRD 3.1）。Deleted 为终态。</summary>
public sealed class FenceStateMachine() : StateMachine<FenceState, FenceTrigger>(FenceState.Expanded, Table)
{
    public static readonly IReadOnlyDictionary<(FenceState, FenceTrigger), FenceState> Table =
        new Dictionary<(FenceState, FenceTrigger), FenceState>
        {
            [(FenceState.Expanded, FenceTrigger.Collapse)] = FenceState.Collapsed,
            [(FenceState.Expanded, FenceTrigger.Hide)] = FenceState.Hidden,
            [(FenceState.Expanded, FenceTrigger.MonitorLost)] = FenceState.Orphaned,
            [(FenceState.Expanded, FenceTrigger.Delete)] = FenceState.Deleted,
            [(FenceState.Collapsed, FenceTrigger.Expand)] = FenceState.Expanded,
            [(FenceState.Collapsed, FenceTrigger.Hide)] = FenceState.Hidden,
            [(FenceState.Collapsed, FenceTrigger.MonitorLost)] = FenceState.Orphaned,
            [(FenceState.Collapsed, FenceTrigger.Delete)] = FenceState.Deleted,
            [(FenceState.Hidden, FenceTrigger.Show)] = FenceState.Expanded,
            [(FenceState.Hidden, FenceTrigger.MonitorLost)] = FenceState.Orphaned,
            [(FenceState.Hidden, FenceTrigger.Delete)] = FenceState.Deleted,
            [(FenceState.Orphaned, FenceTrigger.MonitorRestored)] = FenceState.Expanded,
            [(FenceState.Orphaned, FenceTrigger.Delete)] = FenceState.Deleted,
        };
}

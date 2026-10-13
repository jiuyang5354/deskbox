namespace DeskBox.Domain.StateMachines;

public enum FenceState { Expanded, Collapsed, Hidden, Deleted }

public enum FenceTrigger { Collapse, Expand, Hide, Show, Delete }

/// <summary>方框生命周期（PRD 3.1）。Deleted 为终态。v1.0.9 起不再有 Orphaned：显示器消失只是临时挪到主屏显示、不落库，不是状态（TDD §4.5.2）。</summary>
public sealed class FenceStateMachine() : StateMachine<FenceState, FenceTrigger>(FenceState.Expanded, Table)
{
    public static readonly IReadOnlyDictionary<(FenceState, FenceTrigger), FenceState> Table =
        new Dictionary<(FenceState, FenceTrigger), FenceState>
        {
            [(FenceState.Expanded, FenceTrigger.Collapse)] = FenceState.Collapsed,
            [(FenceState.Expanded, FenceTrigger.Hide)] = FenceState.Hidden,
            [(FenceState.Expanded, FenceTrigger.Delete)] = FenceState.Deleted,
            [(FenceState.Collapsed, FenceTrigger.Expand)] = FenceState.Expanded,
            [(FenceState.Collapsed, FenceTrigger.Hide)] = FenceState.Hidden,
            [(FenceState.Collapsed, FenceTrigger.Delete)] = FenceState.Deleted,
            [(FenceState.Hidden, FenceTrigger.Show)] = FenceState.Expanded,
            [(FenceState.Hidden, FenceTrigger.Delete)] = FenceState.Deleted,
        };
}

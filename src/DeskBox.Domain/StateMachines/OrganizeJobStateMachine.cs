namespace DeskBox.Domain.StateMachines;

public enum OrganizeJobState { Created, Snapshotting, Classifying, Applying, RollingBack, Completed, PartiallyCompleted, Failed, Cancelled, Restored }

public enum OrganizeJobTrigger { Start, SnapshotSaved, Classified, AllApplied, SomeSkipped, Fail, RolledBack, Cancel, Restore }

/// <summary>一键整理任务（PRD 3.2）。必须先快照才能动文件；只能在 Classifying 取消；Applying 不可取消，致命错误先 RollingBack 再 Failed；Failed/Cancelled/Restored 为终态。</summary>
public sealed class OrganizeJobStateMachine() : StateMachine<OrganizeJobState, OrganizeJobTrigger>(OrganizeJobState.Created, Table)
{
    public static readonly IReadOnlyDictionary<(OrganizeJobState, OrganizeJobTrigger), OrganizeJobState> Table =
        new Dictionary<(OrganizeJobState, OrganizeJobTrigger), OrganizeJobState>
        {
            [(OrganizeJobState.Created, OrganizeJobTrigger.Start)] = OrganizeJobState.Snapshotting,
            [(OrganizeJobState.Snapshotting, OrganizeJobTrigger.SnapshotSaved)] = OrganizeJobState.Classifying,
            [(OrganizeJobState.Snapshotting, OrganizeJobTrigger.Fail)] = OrganizeJobState.Failed,
            [(OrganizeJobState.Classifying, OrganizeJobTrigger.Classified)] = OrganizeJobState.Applying,
            [(OrganizeJobState.Classifying, OrganizeJobTrigger.Fail)] = OrganizeJobState.Failed,
            [(OrganizeJobState.Classifying, OrganizeJobTrigger.Cancel)] = OrganizeJobState.Cancelled,
            [(OrganizeJobState.Applying, OrganizeJobTrigger.AllApplied)] = OrganizeJobState.Completed,
            [(OrganizeJobState.Applying, OrganizeJobTrigger.SomeSkipped)] = OrganizeJobState.PartiallyCompleted,
            [(OrganizeJobState.Applying, OrganizeJobTrigger.Fail)] = OrganizeJobState.RollingBack,
            [(OrganizeJobState.RollingBack, OrganizeJobTrigger.RolledBack)] = OrganizeJobState.Failed,
            [(OrganizeJobState.Completed, OrganizeJobTrigger.Restore)] = OrganizeJobState.Restored,
            [(OrganizeJobState.PartiallyCompleted, OrganizeJobTrigger.Restore)] = OrganizeJobState.Restored,
        };

    public bool IsTerminal => State is OrganizeJobState.Failed or OrganizeJobState.Cancelled or OrganizeJobState.Restored;
}

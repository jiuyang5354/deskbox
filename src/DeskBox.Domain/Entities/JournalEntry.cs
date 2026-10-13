namespace DeskBox.Domain.Entities;

public enum JournalStatus { Pending, Committed, RolledBack }

/// <summary>文件移动预写日志：先写 Pending，移动成功后 Committed；崩溃恢复时处理 Pending。</summary>
public sealed class JournalEntry
{
    public Guid Id { get; }
    public Guid JobId { get; }
    public string SourcePath { get; }
    public string TargetPath { get; }
    public JournalStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }

    public JournalEntry(Guid id, Guid jobId, string sourcePath, string targetPath, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new DomainValidationException(nameof(Id), "不能为空");
        if (jobId == Guid.Empty) throw new DomainValidationException(nameof(JobId), "不能为空");
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new DomainValidationException(nameof(SourcePath), "不能为空");
        if (string.IsNullOrWhiteSpace(targetPath)) throw new DomainValidationException(nameof(TargetPath), "不能为空");
        (Id, JobId, SourcePath, TargetPath, CreatedAt) = (id, jobId, sourcePath, targetPath, createdAt);
        Status = JournalStatus.Pending;
    }

    public void Commit() => Transition(JournalStatus.Committed, "Commit");
    public void RollBack() => Transition(JournalStatus.RolledBack, "RollBack");

    private void Transition(JournalStatus to, string trigger)
    {
        if (Status != JournalStatus.Pending)
            throw new InvalidTransitionException(nameof(JournalEntry), Status.ToString(), trigger);
        Status = to;
    }
}

namespace DeskBox.Domain.Entities;

public enum JournalStatus { Pending, Committed, RolledBack, Lost }

/// <summary>
/// 文件移动预写日志。Begin 时记录 file_id（卷序列号 + 128 位文件 ID）；
/// 每次调用 MoveFileExW 之前把本次候选目标写入 <see cref="TargetPath"/>（重试时更新），
/// 这样无论在哪一次尝试中崩溃，恢复逻辑都能按 file_id 在源路径或候选路径上找到文件。
/// </summary>
public sealed class JournalEntry
{
    public Guid Id { get; }
    public Guid JobId { get; }
    public string FileId { get; }
    public string SourcePath { get; }

    /// <summary>当前候选目标路径（每次尝试前更新）。</summary>
    public string TargetPath { get; private set; }

    /// <summary>已开始的原生移动尝试次数（第几个候选）。</summary>
    public int Attempt { get; private set; }

    /// <summary>实际落盘路径，Commit 时写入。</summary>
    public string? ActualTargetPath { get; private set; }

    /// <summary>中止原因（含原始错误码），RollBack 时写入。</summary>
    public string? AbortReason { get; private set; }

    public JournalStatus Status { get; private set; }

    /// <summary>启动恢复连续失败次数（达到 <see cref="MaxRecoveryFailures"/> 转 Lost）。</summary>
    public int RecoveryFailures { get; private set; }

    public const int MaxRecoveryFailures = 3;
    public DateTimeOffset CreatedAt { get; }

    public JournalEntry(Guid id, Guid jobId, string fileId, string sourcePath, string targetPath, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new DomainValidationException(nameof(Id), "不能为空");
        if (jobId == Guid.Empty) throw new DomainValidationException(nameof(JobId), "不能为空");
        if (!FenceItem.IsValidFileId(fileId)) throw new DomainValidationException(nameof(FileId), "格式无效");
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new DomainValidationException(nameof(SourcePath), "不能为空");
        if (string.IsNullOrWhiteSpace(targetPath)) throw new DomainValidationException(nameof(TargetPath), "不能为空");
        (Id, JobId, FileId, SourcePath, TargetPath, CreatedAt) = (id, jobId, fileId.ToUpperInvariant(), sourcePath, targetPath, createdAt);
        Status = JournalStatus.Pending;
    }

    /// <summary>
    /// 从持久化存储重建（仅供仓储使用）。校验与构造函数相同，并要求状态与字段一致：
    /// Committed 必须有实际路径，RolledBack 必须有中止原因，计数不能为负。
    /// </summary>
    public static JournalEntry Restore(Guid id, Guid jobId, string fileId, string sourcePath, string targetPath,
        DateTimeOffset createdAt, int attempt, JournalStatus status, string? actualTargetPath, string? abortReason, int recoveryFailures)
    {
        var e = new JournalEntry(id, jobId, fileId, sourcePath, targetPath, createdAt);
        if (attempt < 0) throw new DomainValidationException(nameof(Attempt), "不能为负");
        if (recoveryFailures < 0) throw new DomainValidationException(nameof(RecoveryFailures), "不能为负");
        if (!Enum.IsDefined(status)) throw new DomainValidationException(nameof(Status), "未知状态");
        if (status == JournalStatus.Committed && string.IsNullOrWhiteSpace(actualTargetPath))
            throw new DomainValidationException(nameof(ActualTargetPath), "Committed 必须有实际路径");
        if (status == JournalStatus.RolledBack && string.IsNullOrWhiteSpace(abortReason))
            throw new DomainValidationException(nameof(AbortReason), "RolledBack 必须有中止原因");
        e.Attempt = attempt;
        e.RecoveryFailures = recoveryFailures;
        e.Status = status;
        e.ActualTargetPath = status == JournalStatus.Committed ? actualTargetPath : null;
        e.AbortReason = status == JournalStatus.RolledBack ? abortReason : null;
        return e;
    }

    /// <summary>记录即将尝试的候选目标（仅 Pending 状态）。</summary>
    public void BeginAttempt(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath)) throw new DomainValidationException(nameof(TargetPath), "不能为空");
        EnsurePending("BeginAttempt");
        TargetPath = candidatePath;
        Attempt++;
    }

    public void Commit(string actualTargetPath)
    {
        if (string.IsNullOrWhiteSpace(actualTargetPath))
            throw new DomainValidationException(nameof(ActualTargetPath), "不能为空");
        Transition(JournalStatus.Committed, "Commit");
        ActualTargetPath = actualTargetPath;
    }

    public void RollBack(string abortReason)
    {
        if (string.IsNullOrWhiteSpace(abortReason))
            throw new DomainValidationException(nameof(AbortReason), "不能为空");
        Transition(JournalStatus.RolledBack, "RollBack");
        AbortReason = abortReason;
    }

    /// <summary>记录一次恢复失败（仅 Pending），返回累计连续失败次数。</summary>
    public int RecordRecoveryFailure()
    {
        EnsurePending("RecordRecoveryFailure");
        return ++RecoveryFailures;
    }

    /// <summary>恢复时在源、候选路径和收纳库中都找不到该 file_id。</summary>
    public void MarkLost() => Transition(JournalStatus.Lost, "MarkLost");

    private void EnsurePending(string trigger)
    {
        if (Status != JournalStatus.Pending)
            throw new InvalidTransitionException(nameof(JournalEntry), Status.ToString(), trigger);
    }

    private void Transition(JournalStatus to, string trigger)
    {
        EnsurePending(trigger);
        Status = to;
    }
}

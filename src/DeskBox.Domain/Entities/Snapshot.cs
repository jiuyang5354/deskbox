namespace DeskBox.Domain.Entities;

public enum SnapshotTrigger { Manual, Auto, BeforeRestore }

/// <summary>整理前的桌面快照；最多保留 10 份。</summary>
public sealed class Snapshot
{
    public const int MaxRetained = 10;

    public Guid Id { get; }
    public SnapshotTrigger Trigger { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<SnapshotEntry> Entries { get; }

    public Snapshot(Guid id, SnapshotTrigger trigger, DateTimeOffset createdAt, IEnumerable<SnapshotEntry> entries)
    {
        if (id == Guid.Empty) throw new DomainValidationException(nameof(Id), "不能为空");
        if (!Enum.IsDefined(trigger)) throw new DomainValidationException(nameof(Trigger), "未知触发类型");
        ArgumentNullException.ThrowIfNull(entries);
        Id = id;
        Trigger = trigger;
        CreatedAt = createdAt;
        Entries = entries.ToArray();
    }

    /// <summary>按时间倒序保留最近 <see cref="MaxRetained"/> 份，返回应删除的快照。</summary>
    public static IReadOnlyList<Snapshot> SelectExpired(IEnumerable<Snapshot> all) =>
        all.OrderByDescending(s => s.CreatedAt).Skip(MaxRetained).ToArray();
}

public sealed record SnapshotEntry(string FileId, string FilePath, Guid? FenceId, int X, int Y);

using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;
using DeskBox.Domain.Rules;

namespace DeskBox.Application.Ports;

/// <summary>桌面上的一个项目（用户桌面或公共桌面）。</summary>
public sealed record DesktopItem(string FullPath, string FileId, bool IsDirectory, bool IsPublicDesktop, DateTimeOffset ModifiedAt)
{
    public DesktopEntry ToEntry() => new(Path.GetFileName(FullPath), IsDirectory, ModifiedAt);
}

public enum DesktopChangeKind { Created, Deleted, Renamed, Changed }

public sealed record DesktopChange(DesktopChangeKind Kind, string FullPath, string? OldFullPath);

/// <summary>枚举用户桌面与公共桌面，并监听变化。</summary>
public interface IDesktopEnumerator
{
    IAsyncEnumerable<DesktopItem> EnumerateAsync(CancellationToken ct = default);
    event EventHandler<DesktopChange>? Changed;
}

/// <summary>读取卷序列号 + 128 位文件 ID，用于跟踪重命名/移动。</summary>
public interface IFileIdentity
{
    string? TryGetFileId(string fullPath);
}

/// <summary>安全移动文件：不覆盖、不跨分区；重名自动改名；失败返回 Skipped 而非抛异常。</summary>
public interface IFileMover
{
    Task<MoveResult> MoveAsync(string sourcePath, string targetPath, CancellationToken ct = default);
}

/// <summary>移动操作预写日志，崩溃后可恢复。</summary>
public interface IJournal
{
    Task AppendPendingAsync(JournalEntry entry, CancellationToken ct = default);
    Task CommitAsync(Guid entryId, string actualTargetPath, CancellationToken ct = default);
    Task RollBackAsync(Guid entryId, CancellationToken ct = default);
    Task<IReadOnlyList<JournalEntry>> GetPendingAsync(CancellationToken ct = default);
}

/// <summary>整理快照存储（保留最近 10 份）。</summary>
public interface ISnapshotStore
{
    Task SaveAsync(Snapshot snapshot, CancellationToken ct = default);
    Task<Snapshot?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Snapshot>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed record MonitorInfo(string Fingerprint, int Left, int Top, int Width, int Height, int Dpi, bool IsPrimary);

/// <summary>显示器信息与拔插、DPI 变化通知。</summary>
public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    event EventHandler? MonitorsChanged;
}

/// <summary>桌面层宿主：WorkerW 嵌入、系统图标显隐（FWF_NOICONS）、Explorer 重启自愈。</summary>
public interface IDesktopHost
{
    bool IsAttached { get; }
    bool AreSystemIconsHidden { get; }
    void Attach(IntPtr windowHandle);
    void Detach();
    void SetSystemIconsHidden(bool hidden);
    event EventHandler? ExplorerRestarted;
}

/// <summary>规则引擎端口。</summary>
public interface IRuleEngine
{
    Classification Classify(DesktopEntry entry);
}

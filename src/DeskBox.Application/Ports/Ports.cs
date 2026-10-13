using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;
using DeskBox.Domain.Rules;

namespace DeskBox.Application.Ports;

/// <summary>桌面上的一个项目（用户桌面或公共桌面）。</summary>
public sealed record DesktopItem(string FullPath, string FileId, bool IsDirectory, bool IsPublicDesktop, DateTimeOffset ModifiedAt)
{
    public DesktopEntry ToEntry() => new(WinPath.GetFileName(FullPath), IsDirectory, ModifiedAt);
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
    /// <summary>
    /// beforeAttempt 在每次原生移动尝试之前以候选目标路径调用（含首次与每次改名重试），
    /// 调用方据此把候选路径写入 Journal。
    /// </summary>
    Task<MoveResult> MoveAsync(string sourcePath, string targetPath,
        Func<string, CancellationToken, Task>? beforeAttempt, CancellationToken ct = default);

    Task<MoveResult> MoveAsync(string sourcePath, string targetPath, CancellationToken ct = default) =>
        MoveAsync(sourcePath, targetPath, null, ct);
}

/// <summary>移动操作预写日志，崩溃后可恢复。</summary>
public interface IJournal
{
    Task AppendPendingAsync(JournalEntry entry, CancellationToken ct = default);
    /// <summary>每次原生移动尝试之前持久化候选目标路径与尝试序号。</summary>
    Task UpdateCandidateAsync(Guid entryId, string candidatePath, int attempt, CancellationToken ct = default);
    Task MarkLostAsync(Guid entryId, CancellationToken ct = default);
    Task CommitAsync(Guid entryId, string actualTargetPath, CancellationToken ct = default);
    Task RollBackAsync(Guid entryId, string abortReason, CancellationToken ct = default);
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

/// <summary>收纳库内容枚举（恢复时按 file_id 扫描）。</summary>
public interface IVaultIndex
{
    IEnumerable<string> EnumerateFiles();
}

public interface IVaultRecordStore
{
    Task AddAsync(VaultRecord record, CancellationToken ct = default);
}

/// <summary>找不回文件的提示，持久保存直到用户确认。</summary>
public interface ILostFileNoticeStore
{
    Task AddAsync(LostFileNotice notice, CancellationToken ct = default);
    Task<IReadOnlyList<LostFileNotice>> GetUnacknowledgedAsync(CancellationToken ct = default);
    Task SaveAsync(LostFileNotice notice, CancellationToken ct = default);
}

/// <summary>移动被中断且无法证明文件未被移动时保留 Pending，并写结构化日志。</summary>
public sealed record UncertainPendingEvent(Guid EntryId, Guid JobId, string FileId, string SourcePath,
    string CandidatePath, int Attempt, string ExceptionType, string Message);

public interface IRecoveryLog
{
    void UncertainPending(UncertainPendingEvent e);
}

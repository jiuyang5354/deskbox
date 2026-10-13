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

/// <summary>
/// 一次 SQLite 事务（v1.0.5）。由 <see cref="IUnitOfWorkFactory"/> 打开唯一的连接与事务；
/// Journal 和各 Store 只在调用方传入的工作单元上读写，不得自行打开连接。
/// 未调用 <see cref="CommitAsync"/> 就释放 = 回滚（全有或全无）。
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(CancellationToken ct = default);
}

/// <summary>移动操作预写日志，崩溃后可恢复。所有操作都在调用方的工作单元内执行。</summary>
public interface IJournal
{
    Task AppendPendingAsync(IUnitOfWork uow, JournalEntry entry, CancellationToken ct = default);
    /// <summary>每次原生移动尝试之前持久化候选目标路径与尝试序号。</summary>
    Task UpdateCandidateAsync(IUnitOfWork uow, Guid entryId, string candidatePath, int attempt, CancellationToken ct = default);
    Task MarkLostAsync(IUnitOfWork uow, Guid entryId, CancellationToken ct = default);
    Task CommitAsync(IUnitOfWork uow, Guid entryId, string actualTargetPath, CancellationToken ct = default);
    Task RollBackAsync(IUnitOfWork uow, Guid entryId, string abortReason, CancellationToken ct = default);
    /// <summary>该条恢复失败次数 +1 并持久化，返回连续失败次数（journal.recovery_failures）。</summary>
    Task<int> RecordRecoveryFailureAsync(IUnitOfWork uow, Guid entryId, CancellationToken ct = default);
    Task<IReadOnlyList<JournalEntry>> GetPendingAsync(IUnitOfWork uow, CancellationToken ct = default);
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
    /// <summary>vault_path 唯一；重复写入抛异常（由事务保证恢复时不会重复写）。</summary>
    Task AddAsync(IUnitOfWork uow, VaultRecord record, CancellationToken ct = default);
}

/// <summary>找不回文件的提示，持久保存直到用户确认。</summary>
public interface ILostFileNoticeStore
{
    /// <summary>主键为 journal_entry_id；重复写入抛异常。</summary>
    Task AddAsync(IUnitOfWork uow, LostFileNotice notice, CancellationToken ct = default);
    Task<IReadOnlyList<LostFileNotice>> GetUnacknowledgedAsync(IUnitOfWork uow, CancellationToken ct = default);
    Task SaveAsync(IUnitOfWork uow, LostFileNotice notice, CancellationToken ct = default);
}

/// <summary>移动被中断且无法证明文件未被移动时保留 Pending，并写结构化日志。</summary>
public sealed record UncertainPendingEvent(Guid EntryId, Guid JobId, string FileId, string SourcePath,
    string CandidatePath, int Attempt, string ExceptionType, string Message);

/// <summary>启动恢复时某条 Pending 处理失败（保持 Pending，下次启动重试；连续 3 次转 Lost）。
/// Secondary* 为写失败计数本身也失败时的异常（此时 ConsecutiveFailures 为 0，未持久化）。</summary>
public sealed record RecoveryFailedEvent(Guid EntryId, Guid JobId, string FileId, string SourcePath,
    string CandidatePath, int ConsecutiveFailures, bool MarkedLost, string ExceptionType, string Message,
    string? SecondaryExceptionType = null, string? SecondaryMessage = null);

public interface IRecoveryLog
{
    void UncertainPending(UncertainPendingEvent e);
    void RecoveryFailed(RecoveryFailedEvent e);
}

public static class UnitOfWorkExtensions
{
    /// <summary>在一个新事务里执行 work 并提交；work 抛异常则回滚并继续抛出。</summary>
    public static async Task InTransactionAsync(this IUnitOfWorkFactory factory, Func<IUnitOfWork, Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(work);
        var uow = await factory.BeginAsync(ct);
        await using (uow.ConfigureAwait(false))
        {
            await work(uow);
            await uow.CommitAsync(ct);
        }
    }

    public static async Task<T> InTransactionAsync<T>(this IUnitOfWorkFactory factory, Func<IUnitOfWork, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(work);
        var uow = await factory.BeginAsync(ct);
        await using (uow.ConfigureAwait(false))
        {
            var r = await work(uow);
            await uow.CommitAsync(ct);
            return r;
        }
    }
}

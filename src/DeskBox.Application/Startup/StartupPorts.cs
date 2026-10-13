using DeskBox.Application.Files;

namespace DeskBox.Application.Startup;

/// <summary>按用户命名的单实例锁（TDD §4.6 第 1 步）。实现持有锁直到 Dispose。</summary>
public interface ISingleInstance
{
    /// <summary>尝试取得锁；已有实例在运行时返回 false。</summary>
    bool TryAcquire();

    /// <summary>通知正在运行的实例（例如把方框带到前台），由第二个进程在退出前调用。</summary>
    void SignalRunningInstance();
}

/// <summary>打开数据库并把结构迁移到最新版本（TDD §4.6 第 2 步）。</summary>
public interface IDatabaseBootstrapper
{
    Task MigrateAsync(CancellationToken ct = default);
}

/// <summary>启动时的 Journal 恢复（TDD §4.6 第 3 步），由 <see cref="JournalRecovery"/> 实现。</summary>
public interface IStartupRecovery
{
    Task<IReadOnlyList<RecoveredEntry>> RecoverAsync(CancellationToken ct = default);
}

/// <summary>从数据库读出方框并嵌入桌面层或降级（TDD §4.6 第 5 步），由 <see cref="Desktop.DesktopEmbedder"/> 实现。</summary>
public interface IStartupEmbedder
{
    Task StartAsync(CancellationToken ct = default);
}

/// <summary>启动阶段需要告诉用户的事。</summary>
public interface IStartupNotifier
{
    /// <summary>恢复失败，进入只读模式：不移动任何文件。</summary>
    void ReadOnlyMode(string reason);
}

/// <summary>启动时桌面扫描日志：只记数量，不记文件内容（TDD v1.0.13 §4.5.5）。</summary>
public interface IDesktopScanLog
{
    /// <summary>只读模式跳过了枚举。</summary>
    void DesktopScanSkipped(string reason);

    void DesktopScanStarted();

    void DesktopScanCompleted(int userDesktopItems, int publicDesktopItems, int fenceItemRowsWritten);
}

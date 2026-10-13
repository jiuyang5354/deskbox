using System.Diagnostics;
using System.IO;
using System.Windows;
using DeskBox.Application.Desktop;
using DeskBox.Application.Fences;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Application.Startup;
using DeskBox.Infrastructure.Files;
using DeskBox.Infrastructure.Logging;
using DeskBox.Infrastructure.Shell;
using DeskBox.Infrastructure.Sqlite;
using DeskBox.Infrastructure.Startup;

namespace DeskBox.App;

/// <summary>
/// 组合根（TDD §4.6）：整个进程里只有这里 new 基础设施对象；其余各层只依赖 Application 端口。
/// 启动顺序由 <see cref="StartupSequence"/> 保证：单实例 → 数据库迁移 → Journal 恢复 → 枚举桌面 → 嵌入方框。
/// </summary>
public partial class App : System.Windows.Application
{
    private readonly List<IDisposable> _owned = [];

    /// <summary>只读模式下由 <see cref="GatedFileMover"/> 拒绝移动；后续整理功能从这里取移动器。</summary>
    internal OperatingMode Mode { get; } = new();

    internal IFileMover? FileMover { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var uiContext = SynchronizationContext.Current;
            var instance = Own(new NamedMutexSingleInstance(uiContext));
            // DB/logs 仍在 LocalAppData；收纳库按 PRD 用 %USERPROFILE%\DeskBox\Vault（与桌面更易同盘）。
            // 未正式发布，不做 LocalAppData\DeskBox\Vault → UserProfile 的迁数据；旧路径有文件则忽略。
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBox");
            var vaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskBox", "Vault");
            Directory.CreateDirectory(vaultDir);
            var dbPath = Path.Combine(dataDir, "deskbox.db");
            // 落盘日志：%LocalAppData%\DeskBox\logs，5MB 滚动保留 3 份；现有 Trace 输出一并写入（TDD v1.0.13 §6）
            // 只在拿到单实例锁之后打开（StartupSequence 的 instanceAcquired 回调），第二实例不写 deskbox.log
            var fileLog = new RollingFileLog(Path.Combine(dataDir, "logs"));
            var db = new SqliteUnitOfWorkFactory(dbPath);
            var journal = new SqliteJournal();
            var log = new TraceRecoveryLog();
            var recovery = new JournalRecovery(db, journal, new WindowsFileIdentity(), new VaultDirectoryIndex(vaultDir),
                new SqliteVaultRecordStore(), new SqliteLostFileNoticeStore(), log, TimeProvider.System);
            var shell = Own(new Win32ShellNotifications(uiContext));
            var tray = Own(new Win32TrayIcon(shell));
            var windowing = new Win32DesktopWindowing();
            var monitors = Own(new Win32MonitorService(shell));
            var fenceStore = new SqliteFenceStore();
            var itemStore = new SqliteFenceItemStore();
            FileMover = new GatedFileMover(FileMoverFactory.Create(), Mode);
            // 「移入收纳库」是方框界面唯一会移动文件的命令：走带日志的 JournaledMove，只读时由 GatedFileMover 拒绝（TDD §4.7 第 2、4 条）
            var journaled = new JournaledMove(db, FileMover, journal, new WindowsFileIdentity(), FileMoverFactory.CreateProbe(), log, TimeProvider.System);
            var commands = new FenceCommandHandler(db, fenceStore, itemStore, monitors, new JournaledVaultMover(journaled, vaultDir), Mode,
                new TraceFenceLog());
            var ui = new FenceUi(windowing, monitors, commands, new FenceBoard(db, fenceStore, itemStore), Mode);
            var embedder = Own(new DesktopEmbedder(db, fenceStore, new Win32DesktopWindowTree(), windowing, monitors, ui, shell, fileLog));
            embedder.Attached += async (_, mode) =>
            {
                tray.Show(mode); // 兼容显示 ↔ 重新嵌入成功时恢复正常提示
                await RefreshUiAsync(ui);
            };
            embedder.AttachFailed += (_, ex) =>
            {
                tray.Show(EmbedMode.Degraded);
                fileLog.Error($"EmbedFailed {ex}");
            };
            // 第二实例唤醒：激活信号在线程池到达，置前在界面线程执行（TDD §4.7 第 6 条）
            // 兼容显示时第二实例不激活、不置前方框，只弹托盘气泡（TDD v1.0.13 §4.5.3）
            instance.Activated += new InstanceActivation(new WpfUiDispatcher(Dispatcher),
                new EmbedAwareActivator(new WpfWindowActivator(() => ui.Windows), () => embedder.Mode, tray)).OnSignal;
            var desktop = FileMoverFactory.CreateDesktopEnumerator();
            if (desktop is IDisposable d) Own(d);

            var sequence = new StartupSequence(instance, new SqliteDatabaseBootstrapper(db, dbPath, new TraceMigrationLog()),
                new StagingFenceSelfCheck(db, fenceStore, monitors, new TraceStagingLog()), recovery, desktop, embedder,
                new MessageBoxNotifier(), Mode, fileLog, () =>
                {
                    fileLog.Open();
                    Trace.Listeners.Add(new RollingFileTraceListener(fileLog));
                    Trace.AutoFlush = true;
                    fileLog.Info($"DeskBox 启动 version={typeof(App).Assembly.GetName().Version} pid={Environment.ProcessId}");
                });
            var result = await sequence.RunAsync();
            if (result.Outcome == StartupOutcome.SecondInstance) Shutdown(0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("DeskBox 启动失败：{0}", ex);
            MessageBox.Show($"DeskBox 无法启动：{ex.Message}", "DeskBox", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static async Task RefreshUiAsync(FenceUi ui)
    {
        try
        {
            await ui.RefreshAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("DeskBox 刷新方框失败：{0}", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        base.OnExit(e);
    }

    private T Own<T>(T item) where T : IDisposable
    {
        _owned.Add(item);
        return item;
    }

    private sealed class MessageBoxNotifier : IStartupNotifier
    {
        public void ReadOnlyMode(string reason) =>
            MessageBox.Show($"{reason}。DeskBox 已进入只读模式：方框照常显示，但不会移动任何文件。请重启 DeskBox 再试。",
                "DeskBox", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private sealed class TraceMigrationLog : SqliteSchema.IMigrationLog
    {
        public void StagingFenceDemoted(Guid fenceId, Guid keptStagingFenceId) =>
            Trace.TraceWarning("StagingFenceDemoted: 迁移 v4 把重复的暂存方框 {0} 降为普通方框（保留 {1}）", fenceId, keptStagingFenceId);
    }

    private sealed class TraceStagingLog : IStagingSelfCheckLog
    {
        public void StagingFenceRecreated(Guid fenceId, string monitorId) =>
            Trace.TraceWarning("StagingFenceRecreated: 暂存方框缺失，已在默认位置补建 {0}（显示器 {1}）", fenceId, monitorId);
    }

    private sealed class TraceFenceLog : IFenceLog
    {
        public void StagingFenceMissing(Guid fenceId, int itemCount) =>
            Trace.TraceError("StagingFenceMissing: 删除方框 {0}（{1} 个文件）时找不到暂存方框，已拒绝且未修改", fenceId, itemCount);
    }

    private sealed class TraceRecoveryLog : IRecoveryLog
    {
        public void UncertainPending(UncertainPendingEvent e) => Trace.TraceWarning("UncertainPending {0}", e);
        public void RecoveryFailed(RecoveryFailedEvent e) => Trace.TraceWarning("RecoveryFailed {0}", e);
    }
}

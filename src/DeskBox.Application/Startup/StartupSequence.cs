using DeskBox.Application.Files;
using DeskBox.Application.Ports;

namespace DeskBox.Application.Startup;

public enum StartupOutcome
{
    /// <summary>已有实例在运行：已通知它，本进程应立即退出，没有碰数据库和文件。</summary>
    SecondInstance,
    /// <summary>正常启动。</summary>
    Running,
    /// <summary>恢复失败：只读模式，未枚举桌面，不移动文件；方框照常显示。</summary>
    ReadOnly,
}

public sealed record StartupResult(StartupOutcome Outcome, IReadOnlyList<RecoveredEntry> Recovered,
    IReadOnlyList<DesktopItem> DesktopItems, string? ReadOnlyReason = null);

/// <summary>
/// 启动顺序（TDD §4.6，架构师定）：单实例锁 → 打开数据库并迁移 → 暂存方框自检 → Journal 恢复 → 枚举桌面 → 启动对账（§4.5.5）→ 嵌入方框。
/// 自检补建失败与恢复失败一样进入只读模式（自检失败时仍执行恢复，恢复本身不移动文件；提示用第一个失败原因）。
/// 恢复严格先于枚举和嵌入；恢复抛异常或仍有条目处理失败（保持 Pending）时进入只读模式、提示用户、跳过枚举。
/// 锁、数据库、嵌入失败直接抛出，由启动入口决定退出。
/// </summary>
public sealed class StartupSequence(ISingleInstance instance, IDatabaseBootstrapper database, IStagingSelfCheck staging, IStartupRecovery recovery,
    IDesktopEnumerator desktop, IStartupEmbedder embedder, IStartupNotifier notifier, OperatingMode mode, IDesktopScanLog? scanLog = null,
    Action? instanceAcquired = null, IStartupReconcile? reconcile = null)
{
    public async Task<StartupResult> RunAsync(CancellationToken ct = default)
    {
        if (!instance.TryAcquire())
        {
            instance.SignalRunningInstance();
            return new(StartupOutcome.SecondInstance, [], []);
        }
        // 拿到单实例锁之后才打开落盘日志：第二实例不写 deskbox.log
        instanceAcquired?.Invoke();

        await database.MigrateAsync(ct);

        string? failure = null;
        try
        {
            await staging.EnsureAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = $"暂存方框补建失败（{ex.GetType().Name}）";
        }

        IReadOnlyList<RecoveredEntry> recovered = [];
        try
        {
            recovered = await recovery.RecoverAsync(ct);
            var failed = recovered.Count(r => r.Outcome == RecoveryOutcome.Failed);
            if (failed > 0) failure ??= $"{failed} 条未完成的整理记录恢复失败";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure ??= $"整理记录恢复失败（{ex.GetType().Name}）";
        }

        var items = new List<DesktopItem>();
        if (failure is not null)
        {
            mode.EnterReadOnly(failure);
            notifier.ReadOnlyMode(failure);
            scanLog?.DesktopScanSkipped(failure);
        }
        else
        {
            scanLog?.DesktopScanStarted();
            await foreach (var item in desktop.EnumerateAsync(ct)) items.Add(item);
            // 对账排在枚举之后、嵌入之前（TDD v1.0.14 §4.5.5 第 1 条）；DesktopScanCompleted 由对账写，带真实写入行数
            if (reconcile is not null) await reconcile.RunAsync(ct);
        }

        await embedder.StartAsync(ct);
        return failure is null
            ? new(StartupOutcome.Running, recovered, items)
            : new(StartupOutcome.ReadOnly, recovered, items, failure);
    }
}

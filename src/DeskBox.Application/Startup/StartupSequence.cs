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
/// 启动顺序（TDD §4.6，架构师定）：单实例锁 → 打开数据库并迁移 → Journal 恢复 → 枚举桌面 → 嵌入方框。
/// 恢复严格先于枚举和嵌入；恢复抛异常或仍有条目处理失败（保持 Pending）时进入只读模式、提示用户、跳过枚举。
/// 锁、数据库、嵌入失败直接抛出，由启动入口决定退出。
/// </summary>
public sealed class StartupSequence(ISingleInstance instance, IDatabaseBootstrapper database, IStartupRecovery recovery,
    IDesktopEnumerator desktop, IStartupEmbedder embedder, IStartupNotifier notifier, OperatingMode mode)
{
    public async Task<StartupResult> RunAsync(CancellationToken ct = default)
    {
        if (!instance.TryAcquire())
        {
            instance.SignalRunningInstance();
            return new(StartupOutcome.SecondInstance, [], []);
        }

        await database.MigrateAsync(ct);

        IReadOnlyList<RecoveredEntry> recovered = [];
        string? failure;
        try
        {
            recovered = await recovery.RecoverAsync(ct);
            var failed = recovered.Count(r => r.Outcome == RecoveryOutcome.Failed);
            failure = failed == 0 ? null : $"{failed} 条未完成的整理记录恢复失败";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = $"整理记录恢复失败（{ex.GetType().Name}）";
        }

        var items = new List<DesktopItem>();
        if (failure is not null)
        {
            mode.EnterReadOnly(failure);
            notifier.ReadOnlyMode(failure);
        }
        else
        {
            await foreach (var item in desktop.EnumerateAsync(ct)) items.Add(item);
        }

        await embedder.StartAsync(ct);
        return failure is null
            ? new(StartupOutcome.Running, recovered, items)
            : new(StartupOutcome.ReadOnly, recovered, items, failure);
    }
}

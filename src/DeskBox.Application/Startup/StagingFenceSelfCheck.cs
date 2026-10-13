using DeskBox.Application.Desktop;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;

namespace DeskBox.Application.Startup;

/// <summary>启动自检：确保暂存方框存在（TDD §4.6 第 2.5 步）。</summary>
public interface IStagingSelfCheck
{
    /// <summary>返回是否补建了暂存方框。失败抛出，由 <see cref="StartupSequence"/> 转入只读模式。</summary>
    Task<bool> EnsureAsync(CancellationToken ct = default);
}

public interface IStagingSelfCheckLog
{
    /// <summary>暂存方框缺失，已在默认位置补建（警告级）。</summary>
    void StagingFenceRecreated(Guid fenceId, string monitorId);
}

/// <summary>
/// 在一个工作单元（SQLite 为 BEGIN IMMEDIATE）里查 is_staging=1 的方框；没有则在主显示器默认位置补建一个并记警告。
/// 可重复执行：已有就什么都不写；迁移 v4 的部分唯一索引 <c>ux_fence_single_staging</c> 兜底保证不会出现第二个。
/// </summary>
public sealed class StagingFenceSelfCheck(IUnitOfWorkFactory db, IFenceStore fences, IMonitorService monitors,
    IStagingSelfCheckLog log, Func<Guid>? newId = null) : IStagingSelfCheck
{
    public const string DefaultTitle = "暂存";
    public const string FallbackMonitorId = "PRIMARY";
    public const int DefaultX = 24, DefaultY = 24, DefaultWidth = 280, DefaultHeight = 320;

    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        var created = await db.InTransactionAsync<Fence?>(async u =>
        {
            var all = await fences.ListAsync(u, ct);
            if (all.Any(f => f.IsStaging)) return null;
            var list = monitors.GetMonitors();
            var monitor = list.Count == 0 ? null : FenceLayout.Primary(list);
            var fence = new Fence((newId ?? Guid.NewGuid)(), DefaultTitle, monitor?.Fingerprint ?? FallbackMonitorId,
                DefaultX, DefaultY, DefaultWidth, DefaultHeight, monitor?.Dpi ?? 96, isStaging: true);
            await fences.UpsertAsync(u, fence, ct);
            return fence;
        }, ct);
        if (created is null) return false;
        log.StagingFenceRecreated(created.Id, created.MonitorId);
        return true;
    }
}

using DeskBox.Application.Desktop;
using DeskBox.Application.Ports;
using DeskBox.Application.Resources;
using DeskBox.Application.Startup;
using DeskBox.Domain;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;
using DeskBox.Domain.Geometry;

namespace DeskBox.Application.Fences;

/// <summary>收纳库移动端口（由 <see cref="JournaledVaultMover"/> 用 <see cref="Files.JournaledMove"/> 实现）。</summary>
public interface IVaultItemMover
{
    Task<MoveResult> MoveToVaultAsync(string sourcePath, CancellationToken ct = default);
}

/// <summary>
/// 方框界面的全部写操作（TDD §4.7）。界面只发命令；每条命令在一个工作单元（SQLite 为 BEGIN IMMEDIATE）里读改写。
/// 只读门禁在任何读写之前检查：改归属 / 移文件 / 删方框 → <see cref="CommandStatus.ReadOnly"/>；改布局、重命名、透明度、折叠照常。
/// 改归属与从方框移除只改 fence_item，不调用任何文件移动；只有 <see cref="VaultItemCommand"/> 经 <see cref="IVaultItemMover"/>。
/// </summary>
public sealed class FenceCommandHandler(IUnitOfWorkFactory db, IFenceStore fences, IFenceItemStore items,
    IMonitorService monitors, IVaultItemMover vault, OperatingMode mode)
{
    public Task<CommandResult> ExecuteAsync(MoveFenceCommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        return WithFenceAsync(cmd.FenceId, f => ApplyLayout(f, cmd.Screen, keepSize: true), ct);
    }

    public Task<CommandResult> ExecuteAsync(ResizeFenceCommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        return WithFenceAsync(cmd.FenceId, f => ApplyLayout(f, cmd.Screen, keepSize: false), ct);
    }

    public Task<CommandResult> ExecuteAsync(RenameFenceCommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        return WithFenceAsync(cmd.FenceId, f => f.Rename(cmd.Title), ct);
    }

    public Task<CommandResult> ExecuteAsync(SetFenceOpacityCommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        return WithFenceAsync(cmd.FenceId, f => f.SetOpacity(cmd.Opacity), ct);
    }

    public Task<CommandResult> ExecuteAsync(ToggleCollapseCommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        return WithFenceAsync(cmd.FenceId, f => f.ToggleCollapse(), ct);
    }

    public async Task<CommandResult> ExecuteAsync(DeleteFenceCommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (ReadOnlyResult() is { } ro) return ro;
        return await db.InTransactionAsync(async u =>
        {
            var fence = await fences.GetAsync(u, cmd.FenceId, ct);
            if (fence is null) return NotFound();
            if (!fence.CanDelete) return new CommandResult(CommandStatus.Rejected, FenceUiText.StagingCannotDelete);
            fence.Delete();
            await fences.DeleteAsync(u, fence.Id, ct);
            return CommandResult.Ok;
        }, ct);
    }

    public async Task<CommandResult> ExecuteAsync(ReassignItemCommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (ReadOnlyResult() is { } ro) return ro;
        return await db.InTransactionAsync(async u =>
        {
            var item = await items.GetAsync(u, cmd.ItemId, ct);
            if (item is null || await fences.GetAsync(u, cmd.TargetFenceId, ct) is null) return NotFound();
            if (item.FenceId == cmd.TargetFenceId) return CommandResult.Ok;
            var existing = await items.ListByFenceAsync(u, cmd.TargetFenceId, ct);
            var order = existing.Count == 0 ? 0 : existing.Max(i => i.SortOrder) + 1;
            item.MoveToFence(cmd.TargetFenceId, order);
            await items.UpsertAsync(u, item, ct);
            return CommandResult.Ok;
        }, ct);
    }

    public async Task<CommandResult> ExecuteAsync(RemoveItemCommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (ReadOnlyResult() is { } ro) return ro;
        return await db.InTransactionAsync(async u =>
        {
            if (await items.GetAsync(u, cmd.ItemId, ct) is null) return NotFound();
            await items.DeleteAsync(u, cmd.ItemId, ct);
            return CommandResult.Ok;
        }, ct);
    }

    public async Task<CommandResult> ExecuteAsync(VaultItemCommand cmd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (ReadOnlyResult() is { } ro) return ro;
        var item = await db.InTransactionAsync(u => items.GetAsync(u, cmd.ItemId, ct), ct);
        if (item is null) return NotFound();
        try
        {
            item.EnsureCanMoveToVault();
        }
        catch (DomainValidationException)
        {
            return new CommandResult(CommandStatus.Rejected, FenceUiText.PublicDesktopCannotVault);
        }

        MoveResult result;
        try
        {
            result = await vault.MoveToVaultAsync(item.FilePath, ct);
        }
        catch (ReadOnlyModeException)
        {
            return ReadOnlyResult() ?? new CommandResult(CommandStatus.ReadOnly, FenceUiText.ReadOnly(null));
        }

        if (result is MoveResult.Skipped skipped)
            return new CommandResult(CommandStatus.Rejected, SkipReasonText.Get(skipped.Reason));
        await db.InTransactionAsync(u => items.DeleteAsync(u, item.Id, ct), ct);
        return CommandResult.Ok;
    }

    private CommandResult? ReadOnlyResult() =>
        mode.IsReadOnly ? new CommandResult(CommandStatus.ReadOnly, FenceUiText.ReadOnly(mode.ReadOnlyReason)) : null;

    private static CommandResult NotFound() => new(CommandStatus.NotFound, FenceUiText.NotFound);

    private Task<CommandResult> WithFenceAsync(Guid id, Action<Fence> change, CancellationToken ct) =>
        db.InTransactionAsync(async u =>
        {
            var fence = await fences.GetAsync(u, id, ct);
            if (fence is null) return NotFound();
            try
            {
                change(fence);
            }
            catch (Exception ex) when (ex is DomainValidationException or InvalidTransitionException)
            {
                return new CommandResult(CommandStatus.Rejected, ex.Message);
            }
            await fences.UpsertAsync(u, fence, ct);
            return CommandResult.Ok;
        }, ct);

    /// <summary>屏幕物理像素 → 方框中心所在显示器的工作区相对 DIP，并夹进工作区、保证不小于最小尺寸。</summary>
    private void ApplyLayout(Fence fence, ScreenRect screen, bool keepSize)
    {
        var all = monitors.GetMonitors();
        var cx = screen.X + screen.Width / 2;
        var cy = screen.Y + screen.Height / 2;
        var monitor = all.FirstOrDefault(m => m.Bounds.ContainsPoint(cx, cy)) ?? FenceLayout.Primary(all);
        var stored = DesktopGeometry.ToStored(screen, monitor.WorkArea, monitor.Dpi);
        if (keepSize) stored = stored with { Width = fence.Width, Height = fence.Height };
        var clamped = DesktopGeometry.ClampStored(stored,
            DesktopGeometry.ToDip(monitor.WorkArea.Width, monitor.Dpi), DesktopGeometry.ToDip(monitor.WorkArea.Height, monitor.Dpi), Fence.MinSize);
        fence.MoveTo(monitor.Fingerprint, clamped.X, clamped.Y, monitor.Dpi);
        fence.Resize(clamped.Width, clamped.Height);
    }
}

/// <summary>用现有的带日志移动把文件移入收纳库（同名改名、跨盘跳过等规则都在 JournaledMove / 移动器里）。</summary>
public sealed class JournaledVaultMover(Files.JournaledMove move, string vaultDirectory, Func<Guid>? newJobId = null) : IVaultItemMover
{
    public Task<MoveResult> MoveToVaultAsync(string sourcePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var target = vaultDirectory.TrimEnd('\\') + "\\" + WinPath.GetFileName(sourcePath);
        return move.ExecuteAsync((newJobId ?? Guid.NewGuid)(), sourcePath, target, ct);
    }
}

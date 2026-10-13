namespace DeskBox.Application.Fences;

/// <summary>向用户确认。实现须默认选中「取消」，Esc 等同取消（WPF 为 MessageBox 确定/取消，默认取消）。</summary>
public interface IUserConfirmation
{
    bool Confirm(string message);
}

/// <summary>删除流程结果。Result 为 null 表示用户取消；Notice 为结果提示（含事务内实际移动数）。</summary>
public sealed record FenceDeleteOutcome(CommandResult? Result, string? Notice);

/// <summary>
/// 界面「删除方框」：空方框不确认直接删；否则确认一次（文案用当时的文件数），同意后执行。
/// 实际移动数以命令事务内重新读到的为准，数目变了也照常执行、不二次确认，结果提示显示实际数。
/// </summary>
public sealed class FenceDeleteFlow(FenceCommandHandler commands, IUserConfirmation confirmation)
{
    public async Task<FenceDeleteOutcome> RunAsync(Guid fenceId, CancellationToken ct = default)
    {
        var shown = await commands.CountItemsAsync(fenceId, ct);
        if (shown > 0 && !confirmation.Confirm(FenceUiText.ConfirmDelete(shown))) return new(null, null);
        var r = await commands.ExecuteAsync(new DeleteFenceCommand(fenceId), ct);
        return new(r, r.IsOk ? FenceUiText.Deleted(r.Count) : r.Reason);
    }
}

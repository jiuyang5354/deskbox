using System.Globalization;
using DeskBox.Application.Ports;
using DeskBox.Application.Resources;

namespace DeskBox.Application.Files;

/// <summary>每次启动都显示未确认的找不回文件提示，直到用户点"知道了"。</summary>
public sealed class LostFileNotices(ILostFileNoticeStore store, TimeProvider clock)
{
    public async Task<string?> GetMessageAsync(CultureInfo? culture = null, CancellationToken ct = default)
    {
        var pending = await store.GetUnacknowledgedAsync(ct);
        return pending.Count == 0 ? null : UiText.LostFiles(pending.Select(n => n.FileName).ToArray(), culture);
    }

    public async Task AcknowledgeAllAsync(CancellationToken ct = default)
    {
        foreach (var n in await store.GetUnacknowledgedAsync(ct))
        {
            n.Acknowledge(clock.GetUtcNow());
            await store.SaveAsync(n, ct);
        }
    }
}

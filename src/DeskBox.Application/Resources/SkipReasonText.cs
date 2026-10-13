using System.Globalization;
using System.Resources;
using DeskBox.Domain.Files;

namespace DeskBox.Application.Resources;

/// <summary>SkipReason → 界面提示文案（Strings.resx，键为枚举名，默认 zh-CN）。日志仍使用 Skipped.AbortReason。</summary>
public static class SkipReasonText
{
    private static readonly ResourceManager Resources =
        new("DeskBox.Application.Resources.Strings", typeof(SkipReasonText).Assembly);

    public static string Get(SkipReason reason, CultureInfo? culture = null) =>
        Resources.GetString(reason.ToString(), culture ?? CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Strings.resx 缺少键 {reason}");
}

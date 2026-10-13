using System.Globalization;
using System.Resources;

namespace DeskBox.Application.Resources;

/// <summary>其他界面文案（Strings.resx）。</summary>
public static class UiText
{
    private static readonly ResourceManager Resources =
        new("DeskBox.Application.Resources.Strings", typeof(UiText).Assembly);

    public static string LostFiles(IReadOnlyList<string> fileNames, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        if (fileNames.Count == 0) throw new ArgumentException("至少需要一个文件名", nameof(fileNames));
        var c = culture ?? CultureInfo.CurrentUICulture;
        var format = Resources.GetString("LostFilesNotice", c)
            ?? throw new MissingManifestResourceException("Strings.resx 缺少键 LostFilesNotice");
        return string.Format(c, format, fileNames.Count, string.Join("、", fileNames));
    }
}

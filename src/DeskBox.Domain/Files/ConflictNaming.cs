namespace DeskBox.Domain.Files;

/// <summary>重名自动改名：name.ext → name (n).ext，n = 2..999；先去掉名字末尾的点和空格再拆扩展名。</summary>
public static class ConflictNaming
{
    public const int FirstIndex = 2;
    public const int MaxIndex = 999;

    public static string WithIndex(string path, int n, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (n is < FirstIndex or > MaxIndex) throw new ArgumentOutOfRangeException(nameof(n));
        var dir = WinPath.GetDirectoryName(path);
        // Win32 会隐式裁掉文件名末尾的点和空格；生成改名目标时先裁掉，避免 "report. " → "report. (2)" 这类怪名。
        // 源文件访问不经过这里，仍使用原始名字（配合 \\?\ 前缀）。
        var original = WinPath.GetFileName(path);
        var trimmed = original.TrimEnd('.', ' ');
        var file = trimmed.Length == 0 ? original : trimmed;
        var ext = isDirectory ? string.Empty : WinPath.GetExtension(file);
        var stem = file[..^ext.Length];
        if (stem.Length == 0) (stem, ext) = (file, string.Empty); // ".gitignore" 这类无主名文件
        return WinPath.Combine(dir, $"{stem} ({n}){ext}");
    }
}

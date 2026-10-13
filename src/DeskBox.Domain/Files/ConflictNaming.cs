namespace DeskBox.Domain.Files;

/// <summary>重名自动改名：name.ext → name (n).ext，n = 2..999。</summary>
public static class ConflictNaming
{
    public const int FirstIndex = 2;
    public const int MaxIndex = 999;

    public static string WithIndex(string path, int n, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (n is < FirstIndex or > MaxIndex) throw new ArgumentOutOfRangeException(nameof(n));
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var file = Path.GetFileName(path);
        var ext = isDirectory ? string.Empty : Path.GetExtension(file);
        var stem = file[..^ext.Length];
        if (stem.Length == 0) (stem, ext) = (file, string.Empty); // ".gitignore" 这类无主名文件
        return Path.Combine(dir, $"{stem} ({n}){ext}");
    }
}

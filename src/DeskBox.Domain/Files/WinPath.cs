namespace DeskBox.Domain.Files;

/// <summary>
/// 始终按 Windows 规则处理路径（与宿主系统无关）：'\' 与 '/' 都是分隔符，识别盘符 "C:"、
/// 根 "\"、UNC "\\server\share" 与 "\\?\" 前缀；比较不区分大小写。拼接一律使用 '\'。
/// Domain/Application 层禁止直接使用 System.IO.Path，保证 Linux 与 Windows 上结果一致。
/// </summary>
public static class WinPath
{
    public const char Separator = '\\';

    /// <summary>路径比较器（OrdinalIgnoreCase，与 NTFS 默认行为一致）。</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static bool IsSeparator(char c) => c is '\\' or '/';

    public static bool PathEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>根部长度："C:\"=3，"C:"=2，"\"=1，"\\server\share\"=到 share 后的分隔符，"\\?\C:\"=7。</summary>
    public static int RootLength(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var i = 0;
        if (path.Length >= 4 && IsSeparator(path[0]) && IsSeparator(path[1]) && path[2] is '?' or '.' && IsSeparator(path[3]))
        {
            i = 4;
            if (path.Length >= i + 4 && path.AsSpan(i, 3).Equals("UNC", StringComparison.OrdinalIgnoreCase) && IsSeparator(path[i + 3]))
                return UncRoot(path, i + 4);
        }
        else if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return UncRoot(path, 2);
        }
        if (path.Length >= i + 2 && char.IsAsciiLetter(path[i]) && path[i + 1] == ':')
            return path.Length >= i + 3 && IsSeparator(path[i + 2]) ? i + 3 : i + 2;
        if (i == 0 && path.Length >= 1 && IsSeparator(path[0])) return 1;
        return i;
    }

    private static int UncRoot(string path, int start)
    {
        var seps = 0;
        for (var k = start; k < path.Length; k++)
        {
            if (!IsSeparator(path[k])) continue;
            if (++seps == 2) return k + 1;
        }
        return path.Length;
    }

    private static int LastSeparator(string path)
    {
        for (var k = path.Length - 1; k >= 0; k--) if (IsSeparator(path[k])) return k;
        return -1;
    }

    /// <summary>最后一个分隔符之后的部分；"C:a" 返回 "a"。</summary>
    public static string GetFileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = RootLength(path);
        var cut = Math.Max(LastSeparator(path) + 1, root);
        return path[cut..];
    }

    /// <summary>父目录；只有根或没有目录部分时返回根（可能为空串）。</summary>
    public static string GetDirectoryName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = RootLength(path);
        var sep = LastSeparator(path);
        if (sep < root) return path[..root];
        var end = sep;
        while (end > root && IsSeparator(path[end - 1])) end--;
        return path[..end];
    }

    /// <summary>文件名中最后一个 '.' 起的扩展名（含点）；没有或点在末尾时返回空串。</summary>
    public static string GetExtension(string path)
    {
        var name = GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? string.Empty : name[dot..];
    }

    /// <summary>用 '\' 拼接目录与名字；目录为空返回名字，名字是绝对路径时返回名字。</summary>
    public static string Combine(string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(name);
        if (directory.Length == 0 || RootLength(name) > 0) return name;
        if (name.Length == 0) return directory;
        var last = directory[^1];
        return IsSeparator(last) || (last == ':' && RootLength(directory) == directory.Length)
            ? directory + name
            : directory + Separator + name;
    }

    /// <summary>
    /// 两侧均能识别卷根且不同卷时返回 true（同盘断言失败 → CrossVolume）。
    /// 同卷、或任一侧无绝对根（相对路径）时返回 false，交由原生移动决定。
    /// 盘符与 <c>\\?\</c> / <c>\\.\</c> 前缀等价；UNC 按 server\share 比较；不区分大小写，'/' 与 '\' 等价。
    /// </summary>
    public static bool IsCrossVolume(string pathA, string pathB)
    {
        ArgumentNullException.ThrowIfNull(pathA);
        ArgumentNullException.ThrowIfNull(pathB);
        return VolumeKey(pathA) is { } a && VolumeKey(pathB) is { } b && a != b;
    }

    /// <summary>卷标识：盘符为 "C:"；UNC 为 "\\SERVER\SHARE"。无法识别时返回 null。</summary>
    public static string? VolumeKey(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var len = RootLength(path);
        if (len == 0) return null;

        Span<char> buf = stackalloc char[len];
        var n = 0;
        for (var i = 0; i < len; i++)
            buf[n++] = path[i] == '/' ? '\\' : path[i];
        while (n > 0 && buf[n - 1] == '\\') n--;
        if (n == 0) return null;
        var root = new string(buf[..n]);

        if (root.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + root[8..].ToUpperInvariant();
        if (root.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            root = root[4..];

        if (root.Length >= 2 && char.IsAsciiLetter(root[0]) && root[1] == ':')
            return char.ToUpperInvariant(root[0]) + ":";
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return root.ToUpperInvariant();
        return null;
    }
}

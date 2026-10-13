using System.Text;

namespace DeskBox.Application.Files;

/// <summary>
/// 纯字符串的 Win32 路径规范化（不依赖当前工作目录、不访问文件系统，可在 Linux 上测试）：
/// <list type="bullet">
/// <item>C:\a\b → \\?\C:\a\b；\\server\share\a → \\?\UNC\server\share\a</item>
/// <item>已带 \\?\ 或 \\.\ 前缀的路径原样返回（Win32 本就不再解析它们）</item>
/// <item>拒绝相对路径（a\b、C:a、\a）</item>
/// <item>"/" 统一为 "\"，合并重复分隔符，解析 "." 和 ".." 段</item>
/// <item>保留文件名末尾的空格和点（如 "report. "、"a ."），不做 Win32 的隐式裁剪</item>
/// </list>
/// </summary>
public static class Win32PathNormalizer
{
    public const string LongPrefix = @"\\?\";
    public const string UncPrefix = @"\\?\UNC\";
    private const string DevicePrefix = @"\\.\";

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.StartsWith(LongPrefix, StringComparison.Ordinal) || path.StartsWith(DevicePrefix, StringComparison.Ordinal))
            return path;

        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = Split(p[2..]);
            if (parts.Count < 2 || parts[0] is "." or ".." || parts[1] is "." or "..")
                throw new ArgumentException($"UNC 路径须包含服务器和共享名：{path}", nameof(path));
            var root = parts[0] + "\\" + parts[1];
            return UncPrefix + Join(root, Resolve(parts.Skip(2), path));
        }

        if (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\')
            return LongPrefix + Join(char.ToUpperInvariant(p[0]) + ":", Resolve(Split(p[3..]), path), alwaysSeparator: true);

        throw new ArgumentException($"只接受绝对路径（盘符或 UNC），不依赖当前目录：{path}", nameof(path));
    }

    public static bool TryNormalize(string? path, out string normalized)
    {
        try { normalized = Normalize(path!); return true; }
        catch (ArgumentException) { normalized = string.Empty; return false; }
    }

    private static List<string> Split(string s) => s.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static List<string> Resolve(IEnumerable<string> segments, string original)
    {
        var stack = new List<string>();
        foreach (var seg in segments)
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count == 0) throw new ArgumentException($"路径越过了根目录：{original}", nameof(original));
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg); // 原样保留，包括末尾的空格和点
        }
        return stack;
    }

    private static string Join(string root, List<string> segments, bool alwaysSeparator = false)
    {
        var sb = new StringBuilder(root);
        if (segments.Count == 0) return alwaysSeparator ? sb.Append('\\').ToString() : sb.ToString();
        foreach (var s in segments) sb.Append('\\').Append(s);
        return sb.ToString();
    }
}

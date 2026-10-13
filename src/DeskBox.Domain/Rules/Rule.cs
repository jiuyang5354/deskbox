using System.Text.RegularExpressions;

namespace DeskBox.Domain.Rules;

/// <summary>正则匹配超时时间，超时视为不匹配。</summary>
public static class RuleDefaults
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
}

/// <summary>待分类的桌面项描述。</summary>
public sealed record DesktopEntry(string Name, bool IsDirectory, DateTimeOffset ModifiedAt)
{
    public string Extension => IsDirectory ? string.Empty : DeskBox.Domain.Files.WinPath.GetExtension(Name).ToLowerInvariant();
}

/// <summary>规则条件：所有已设置的条件需同时满足（AND）。</summary>
public sealed class RuleCondition
{
    private readonly HashSet<string> _extensions;
    private readonly Regex? _regex;

    public IReadOnlyCollection<string> Extensions => _extensions;
    public IReadOnlyList<string> Keywords { get; }
    public string? NamePattern { get; }
    public DateTimeOffset? ModifiedAfter { get; }
    public DateTimeOffset? ModifiedBefore { get; }
    public bool? IsDirectory { get; }

    public RuleCondition(IEnumerable<string>? extensions = null, IEnumerable<string>? keywords = null,
        string? namePattern = null, DateTimeOffset? modifiedAfter = null, DateTimeOffset? modifiedBefore = null,
        bool? isDirectory = null)
    {
        _extensions = new HashSet<string>(
            (extensions ?? []).Select(NormalizeExtension), StringComparer.OrdinalIgnoreCase);
        Keywords = (keywords ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        if (modifiedAfter is { } a && modifiedBefore is { } b && a > b)
            throw new DomainValidationException(nameof(ModifiedAfter), "起始时间不能晚于结束时间");
        if (namePattern is not null)
        {
            try { _regex = new Regex(namePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RuleDefaults.RegexTimeout); }
            catch (ArgumentException ex) { throw new DomainValidationException(nameof(NamePattern), ex.Message); }
        }
        NamePattern = namePattern;
        ModifiedAfter = modifiedAfter;
        ModifiedBefore = modifiedBefore;
        IsDirectory = isDirectory;
    }

    public bool Matches(DesktopEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (IsDirectory is { } dir && entry.IsDirectory != dir) return false;
        if (_extensions.Count > 0 && !_extensions.Contains(entry.Extension)) return false;
        if (Keywords.Count > 0 && !Keywords.Any(k => entry.Name.Contains(k, StringComparison.OrdinalIgnoreCase))) return false;
        if (ModifiedAfter is { } after && entry.ModifiedAt < after) return false;
        if (ModifiedBefore is { } before && entry.ModifiedAt > before) return false;
        if (_regex is not null)
        {
            try { if (!_regex.IsMatch(entry.Name)) return false; }
            catch (RegexMatchTimeoutException) { return false; }
        }
        return true;
    }

    private static string NormalizeExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) throw new DomainValidationException(nameof(Extensions), "扩展名不能为空");
        var e = ext.Trim().ToLowerInvariant();
        return e.StartsWith('.') ? e : "." + e;
    }
}

/// <summary>归类规则。Priority 越小越先匹配。</summary>
public sealed class Rule
{
    public Guid Id { get; }
    public string Name { get; }
    public string Category { get; }
    public int Priority { get; }
    public bool Enabled { get; }
    public bool IsBuiltIn { get; }
    public RuleCondition Condition { get; }

    public Rule(Guid id, string name, string category, int priority, RuleCondition condition,
        bool enabled = true, bool isBuiltIn = false)
    {
        if (id == Guid.Empty) throw new DomainValidationException(nameof(Id), "不能为空");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32) throw new DomainValidationException(nameof(Name), "长度须为 1-32");
        if (string.IsNullOrWhiteSpace(category) || category.Length > 32) throw new DomainValidationException(nameof(Category), "长度须为 1-32");
        if (priority < 0) throw new DomainValidationException(nameof(Priority), "不能为负");
        ArgumentNullException.ThrowIfNull(condition);
        (Id, Name, Category, Priority, Condition, Enabled, IsBuiltIn) = (id, name, category, priority, condition, enabled, isBuiltIn);
    }
}

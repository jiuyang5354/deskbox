namespace DeskBox.Domain.Rules;

public static class Categories
{
    public const string Shortcuts = "快捷方式";
    public const string Screenshots = "截图";
    public const string Images = "图片";
    public const string Documents = "文档";
    public const string Archives = "压缩包";
    public const string Installers = "安装包";
    public const string Folders = "文件夹";
    public const string Other = "其他";
}

public sealed record Classification(string Category, Rule? MatchedRule);

/// <summary>规则引擎：按 Priority 升序，首条命中即返回，全部不中归入"其他"。</summary>
public sealed class RuleEngine
{
    private readonly Rule[] _rules;

    public RuleEngine(IEnumerable<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.Where(r => r.Enabled).OrderBy(r => r.Priority).ToArray();
    }

    public static RuleEngine CreateDefault() => new(BuiltInRules.All);

    public IReadOnlyList<Rule> Rules => _rules;

    public Classification Classify(DesktopEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (var rule in _rules)
            if (rule.Condition.Matches(entry))
                return new Classification(rule.Category, rule);
        return new Classification(Categories.Other, null);
    }
}

public static class BuiltInRules
{
    private static Rule Make(string id, string name, string category, int priority, RuleCondition c) =>
        new(Guid.Parse(id), name, category, priority, c, enabled: true, isBuiltIn: true);

    public static readonly IReadOnlyList<Rule> All =
    [
        Make("00000000-0000-0000-0000-000000000001", "快捷方式", Categories.Shortcuts, 10,
            new RuleCondition(extensions: [".lnk", ".url"], isDirectory: false)),
        Make("00000000-0000-0000-0000-000000000002", "截图", Categories.Screenshots, 20,
            new RuleCondition(extensions: [".png", ".jpg", ".jpeg"], keywords: ["Screenshot", "截图", "屏幕截图"], isDirectory: false)),
        Make("00000000-0000-0000-0000-000000000003", "图片", Categories.Images, 30,
            new RuleCondition(extensions: [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".heic"], isDirectory: false)),
        Make("00000000-0000-0000-0000-000000000004", "文档", Categories.Documents, 40,
            new RuleCondition(extensions: [".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".csv", ".wps", ".et", ".dps"], isDirectory: false)),
        Make("00000000-0000-0000-0000-000000000005", "压缩包", Categories.Archives, 50,
            new RuleCondition(extensions: [".zip", ".rar", ".7z", ".tar", ".gz", ".xz"], isDirectory: false)),
        Make("00000000-0000-0000-0000-000000000006", "安装包", Categories.Installers, 60,
            new RuleCondition(extensions: [".exe", ".msi"], isDirectory: false)),
        Make("00000000-0000-0000-0000-000000000007", "文件夹", Categories.Folders, 70,
            new RuleCondition(isDirectory: true)),
    ];
}

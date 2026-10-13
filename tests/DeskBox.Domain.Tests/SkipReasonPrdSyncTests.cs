using System.Globalization;
using System.Text.RegularExpressions;
using DeskBox.Application.Resources;
using DeskBox.Domain.Files;

namespace DeskBox.Domain.Tests;

/// <summary>docs/PRD.md「跳过原因用户文案」表与 Strings.resx 必须逐条一致；任一侧改动而另一侧未跟上即失败。</summary>
public class SkipReasonPrdSyncTests
{
    private const string TableMarker = "**跳过原因用户文案";
    private static readonly Regex Row = new(@"^\|\s*(?<enum>[A-Za-z]+)\s*\|\s*(?<key>[A-Za-z]+)\s*\|\s*(?<text>[^|]+?)\s*\|\s*$", RegexOptions.Compiled);

    private static string FindPrd()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "PRD.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException($"从 {AppContext.BaseDirectory} 向上未找到 docs/PRD.md");
    }

    private static IReadOnlyList<(string Enum, string Key, string Text)> ReadTable()
    {
        var lines = File.ReadAllLines(FindPrd());
        var start = Array.FindIndex(lines, l => l.StartsWith(TableMarker, StringComparison.Ordinal));
        start.Should().BeGreaterThanOrEqualTo(0, "PRD 中应有「跳过原因用户文案」一节");
        var rows = new List<(string, string, string)>();
        var inTable = false;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('|'))
            {
                if (inTable) break;
                continue;
            }
            inTable = true;
            var m = Row.Match(line);
            if (m.Success && m.Groups["enum"].Value != "枚举")
            {
                rows.Add((m.Groups["enum"].Value, m.Groups["key"].Value, m.Groups["text"].Value));
            }
        }
        return rows;
    }

    [Fact]
    public void Prd_table_row_count_equals_enum_count() =>
        ReadTable().Should().HaveCount(Enum.GetValues<SkipReason>().Length);

    [Fact]
    public void Every_enum_value_is_in_prd_with_plain_key()
    {
        var table = ReadTable();
        foreach (var reason in Enum.GetValues<SkipReason>())
        {
            var row = table.Should().ContainSingle(r => r.Enum == reason.ToString(), $"PRD 应有 {reason} 一行").Subject;
            row.Key.Should().Be(reason.ToString(), "资源键就是枚举名本身，不加前缀");
        }
    }

    [Fact]
    public void Resx_texts_equal_prd_texts()
    {
        foreach (var (name, _, text) in ReadTable())
        {
            Enum.TryParse<SkipReason>(name, out var reason).Should().BeTrue($"PRD 中的 {name} 应是 SkipReason 枚举值");
            SkipReasonText.Get(reason, CultureInfo.InvariantCulture).Should().Be(text, $"{name} 的 resx 文案应与 PRD 一致");
        }
    }
}

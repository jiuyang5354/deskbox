using DeskBox.Application.Ports;
using DeskBox.Domain;
using DeskBox.Domain.Rules;
using NSubstitute;

namespace DeskBox.Domain.Tests;

public class RuleEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.FromHours(8));
    private static DesktopEntry F(string name, bool dir = false) => new(name, dir, Now);
    private readonly RuleEngine _engine = RuleEngine.CreateDefault();

    // Gherkin: Feature 一键整理 / Scenario 按内置规则归类
    // Given 桌面有 a.lnk、Screenshot_01.png、report.docx
    // When 用户点击"一键整理"
    // Then 分别进入"快捷方式""截图""文档"方框
    [Theory]
    [InlineData("a.lnk", Categories.Shortcuts)]
    [InlineData("Screenshot_01.png", Categories.Screenshots)]
    [InlineData("report.docx", Categories.Documents)]
    public void Gherkin_organize_classification(string name, string expected) =>
        _engine.Classify(F(name)).Category.Should().Be(expected);

    [Theory]
    [InlineData("site.URL", Categories.Shortcuts)]
    [InlineData("截图20261013.jpg", Categories.Screenshots)]
    [InlineData("屏幕截图 2026-10-13.png", Categories.Screenshots)]
    [InlineData("screenshot.jpeg", Categories.Screenshots)]
    [InlineData("photo.png", Categories.Images)]
    [InlineData("Screenshot.gif", Categories.Images)]
    [InlineData("a.pdf", Categories.Documents)]
    [InlineData("a.7z", Categories.Archives)]
    [InlineData("setup.exe", Categories.Installers)]
    [InlineData("setup.MSI", Categories.Installers)]
    [InlineData("noext", Categories.Other)]
    [InlineData("weird.xyz", Categories.Other)]
    public void Built_in_rules(string name, string expected) => _engine.Classify(F(name)).Category.Should().Be(expected);

    [Fact]
    public void Folders_including_dotted_names()
    {
        _engine.Classify(F("项目资料", dir: true)).Category.Should().Be(Categories.Folders);
        _engine.Classify(F("archive.zip", dir: true)).Category.Should().Be(Categories.Folders);
    }

    [Fact]
    public void Unmatched_has_no_rule() => _engine.Classify(F("x.abc")).MatchedRule.Should().BeNull();

    [Fact]
    public void Lowest_priority_wins_regardless_of_input_order()
    {
        var low = new Rule(Guid.NewGuid(), "低", "B", 5, new RuleCondition(extensions: ["png"]));
        var high = new Rule(Guid.NewGuid(), "高", "A", 1, new RuleCondition(extensions: [".PNG"]));
        var engine = new RuleEngine([low, high]);
        engine.Rules.Select(r => r.Category).Should().Equal("A", "B");
        engine.Classify(F("x.png")).MatchedRule.Should().BeSameAs(high);
    }

    [Fact]
    public void Disabled_rules_are_ignored()
    {
        var off = new Rule(Guid.NewGuid(), "关", "Off", 0, new RuleCondition(), enabled: false);
        new RuleEngine([off]).Classify(F("a.txt")).Category.Should().Be(Categories.Other);
    }

    [Fact]
    public void Keyword_and_regex_conditions()
    {
        var c = new RuleCondition(keywords: ["发票", " "], namePattern: @"^\d{4}-");
        c.Keywords.Should().Equal("发票");
        c.Matches(F("2026-发票.pdf")).Should().BeTrue();
        c.Matches(F("发票.pdf")).Should().BeFalse();
        c.Matches(F("2026-合同.pdf")).Should().BeFalse();
        c.NamePattern.Should().Be(@"^\d{4}-");
    }

    [Fact]
    public void Modified_time_range()
    {
        var c = new RuleCondition(modifiedAfter: Now.AddDays(-1), modifiedBefore: Now.AddDays(1));
        c.Matches(F("a")).Should().BeTrue();
        c.Matches(new DesktopEntry("a", false, Now.AddDays(-2))).Should().BeFalse();
        c.Matches(new DesktopEntry("a", false, Now.AddDays(2))).Should().BeFalse();
        (c.ModifiedAfter, c.ModifiedBefore).Should().Be((Now.AddDays(-1), Now.AddDays(1)));
        FluentActions.Invoking(() => new RuleCondition(modifiedAfter: Now, modifiedBefore: Now.AddSeconds(-1)))
            .Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Regex_timeout_is_treated_as_no_match()
    {
        RuleDefaults.RegexTimeout.Should().Be(TimeSpan.FromMilliseconds(100));
        var evil = new RuleCondition(namePattern: "^(a+)+$");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        evil.Matches(F(new string('a', 40) + "!")).Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Invalid_regex_and_extension_rejected()
    {
        FluentActions.Invoking(() => new RuleCondition(namePattern: "(")).Should().Throw<DomainValidationException>().Which.Field.Should().Be("NamePattern");
        FluentActions.Invoking(() => new RuleCondition(extensions: [" "])).Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Rule_validation()
    {
        var c = new RuleCondition();
        FluentActions.Invoking(() => new Rule(Guid.Empty, "n", "c", 0, c)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new Rule(Guid.NewGuid(), "", "c", 0, c)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new Rule(Guid.NewGuid(), new string('n', 33), "c", 0, c)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new Rule(Guid.NewGuid(), "n", "", 0, c)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new Rule(Guid.NewGuid(), "n", "c", -1, c)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new Rule(Guid.NewGuid(), "n", "c", 0, null!)).Should().Throw<ArgumentNullException>();
        BuiltInRules.All.Should().OnlyContain(r => r.IsBuiltIn && r.Enabled);
    }

    [Fact]
    public void Null_guards()
    {
        FluentActions.Invoking(() => new RuleEngine(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => _engine.Classify(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new RuleCondition().Matches(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Adapter_delegates_and_port_is_mockable()
    {
        new RuleEngineAdapter(_engine).Classify(F("a.lnk")).Category.Should().Be(Categories.Shortcuts);
        var port = Substitute.For<IRuleEngine>();
        port.Classify(Arg.Any<DesktopEntry>()).Returns(new Classification("X", null));
        port.Classify(F("q")).Category.Should().Be("X");
        port.Received(1).Classify(Arg.Is<DesktopEntry>(e => e.Name == "q"));
    }
}

using System.Globalization;
using DeskBox.Application.Files;
using DeskBox.Application.Resources;
using DeskBox.Domain.Files;

namespace DeskBox.Domain.Tests;

public class Win32PathNormalizerTests
{
    [Theory]
    [InlineData(@"C:\Users\a\Desktop\report. ", @"\\?\C:\Users\a\Desktop\report. ")]
    [InlineData(@"C:\Users\a\Desktop\a .", @"\\?\C:\Users\a\Desktop\a .")]
    [InlineData(@"C:\dir. \file.txt...", @"\\?\C:\dir. \file.txt...")]
    [InlineData(@"c:/Users//a/./Desktop/../Desktop/x.lnk", @"\\?\C:\Users\a\Desktop\x.lnk")]
    [InlineData(@"C:\", @"\\?\C:\")]
    [InlineData(@"D:\照片\😀 生日.png", @"\\?\D:\照片\😀 生日.png")]
    [InlineData(@"\\server\share\docs\a.txt", @"\\?\UNC\server\share\docs\a.txt")]
    [InlineData(@"//server/share", @"\\?\UNC\server\share")]
    [InlineData(@"\\server\share\x\..\y ", @"\\?\UNC\server\share\y ")]
    public void Normalizes(string input, string expected) => Win32PathNormalizer.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData(@"\\?\C:\a\..\b. ")]
    [InlineData(@"\\?\UNC\server\share\a")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void Already_prefixed_unchanged(string input) => Win32PathNormalizer.Normalize(input).Should().BeSameAs(input);

    [Fact]
    public void Long_path_over_260_chars()
    {
        var input = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('长', 50), 8)) + @"\file.txt";
        input.Length.Should().BeGreaterThan(260);
        var r = Win32PathNormalizer.Normalize(input);
        r.Should().Be(@"\\?\" + input);
    }

    [Theory]
    [InlineData(@"report.docx")]
    [InlineData(@"Desktop\a.lnk")]
    [InlineData(@".\a.txt")]
    [InlineData(@"..\a.txt")]
    [InlineData(@"C:a.txt")]
    [InlineData(@"\Windows\a.txt")]
    [InlineData(@"/home/a.txt")]
    [InlineData(@"1:\a")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\..")]
    [InlineData(@"\\..\share")]
    [InlineData(@"C:\..\a")]
    public void Relative_or_invalid_rejected(string input)
    {
        FluentActions.Invoking(() => Win32PathNormalizer.Normalize(input)).Should().Throw<ArgumentException>();
        Win32PathNormalizer.TryNormalize(input, out var n).Should().BeFalse();
        n.Should().BeEmpty();
    }

    [Fact]
    public void Null_or_empty_rejected()
    {
        FluentActions.Invoking(() => Win32PathNormalizer.Normalize("")).Should().Throw<ArgumentException>();
        Win32PathNormalizer.TryNormalize(null, out _).Should().BeFalse();
        Win32PathNormalizer.TryNormalize(@"C:\a", out var ok).Should().BeTrue();
        ok.Should().Be(@"\\?\C:\a");
    }
}

public class SkipReasonTextTests
{
    public static TheoryData<SkipReason> AllReasons() => new(Enum.GetValues<SkipReason>());

    [Theory, MemberData(nameof(AllReasons))]
    public void Every_reason_has_text(SkipReason reason) =>
        SkipReasonText.Get(reason, CultureInfo.InvariantCulture).Should().NotBeNullOrWhiteSpace();

    [Theory]
    [InlineData(SkipReason.Locked, "文件正被其他程序使用")]
    [InlineData(SkipReason.CloudOnly, "这是 OneDrive 云端文件，未下载到本地")]
    [InlineData(SkipReason.NeedsAdmin, "公共桌面文件需要管理员权限")]
    [InlineData(SkipReason.NotFound, "文件已被删除或移动")]
    [InlineData(SkipReason.CrossVolume, "收纳库和桌面不在同一个磁盘")]
    [InlineData(SkipReason.ReadOnly, "文件所在磁盘处于写保护状态")]
    [InlineData(SkipReason.NameConflict, "同名文件过多，无法自动改名")]
    [InlineData(SkipReason.IoError, "未知错误，详见日志")]
    public void Exact_texts(SkipReason reason, string expected)
    {
        SkipReasonText.Get(reason, CultureInfo.GetCultureInfo("zh-CN")).Should().Be(expected);
        SkipReasonText.Get(reason, CultureInfo.GetCultureInfo("en-US")).Should().Be(expected);
        SkipReasonText.Get(reason).Should().Be(expected);
    }

    [Fact]
    public void Unknown_value_throws() =>
        FluentActions.Invoking(() => SkipReasonText.Get((SkipReason)42)).Should().Throw<System.Resources.MissingManifestResourceException>();

    [Fact]
    public void Log_format_unchanged() =>
        new MoveResult.Skipped("a", SkipReason.NeedsAdmin, 5).AbortReason.Should().Be("NeedsAdmin (Win32 5)");
}

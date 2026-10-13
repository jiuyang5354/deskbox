using DeskBox.Domain.Files;

namespace DeskBox.Domain.Tests;

/// <summary>WinPath 在任何宿主上都按 Windows 规则工作（CI 在 Linux 与 windows-2022 上结果必须一致）。</summary>
public class WinPathTests
{
    [Theory]
    [InlineData(@"C:\v\notes.txt", @"C:\v", "notes.txt", ".txt")]
    [InlineData(@"C:/v/notes.txt", @"C:/v", "notes.txt", ".txt")]
    [InlineData(@"C:\notes.txt", @"C:\", "notes.txt", ".txt")]
    [InlineData(@"C:notes", "C:", "notes", "")]
    [InlineData(@"C:\", @"C:\", "", "")]
    [InlineData(@"\a.b", @"\", "a.b", ".b")]
    [InlineData(@"\\srv\share\x\y.z", @"\\srv\share\x", "y.z", ".z")]
    [InlineData(@"\\srv\share\y", @"\\srv\share\", "y", "")]
    [InlineData(@"\\?\C:\v\a.tar.gz", @"\\?\C:\v", "a.tar.gz", ".gz")]
    [InlineData(@"\\?\UNC\srv\share\f.txt", @"\\?\UNC\srv\share\", "f.txt", ".txt")]
    [InlineData(@"C:\v\\sub\\f", @"C:\v\\sub", "f", "")]
    [InlineData("name.", "", "name.", "")]
    [InlineData(".gitignore", "", ".gitignore", ".gitignore")]
    [InlineData("plain", "", "plain", "")]
    public void Splits(string path, string dir, string name, string ext)
    {
        WinPath.GetDirectoryName(path).Should().Be(dir);
        WinPath.GetFileName(path).Should().Be(name);
        WinPath.GetExtension(path).Should().Be(ext);
    }

    [Theory]
    [InlineData(@"C:\v", "a.txt", @"C:\v\a.txt")]
    [InlineData(@"C:\v\", "a.txt", @"C:\v\a.txt")]
    [InlineData(@"C:/v/", "a.txt", @"C:/v/a.txt")]
    [InlineData(@"C:\", "a", @"C:\a")]
    [InlineData("C:", "a", "C:a")]
    [InlineData("", "a", "a")]
    [InlineData(@"C:\v", "", @"C:\v")]
    [InlineData(@"C:\v", @"D:\x", @"D:\x")]
    [InlineData(@"\\srv\share", "f", @"\\srv\share\f")]
    public void Combines(string dir, string name, string expected) =>
        WinPath.Combine(dir, name).Should().Be(expected);

    [Theory]
    [InlineData("", 0)]
    [InlineData("rel", 0)]
    [InlineData(@"\\srv", 5)]
    [InlineData(@"\\.\C:\x", 7)]
    [InlineData(@"\\?\UNC\srv", 11)]
    public void Root_length(string path, int expected) => WinPath.RootLength(path).Should().Be(expected);

    [Fact]
    public void Compare_is_case_insensitive()
    {
        WinPath.PathEquals(@"C:\V\A.TXT", @"c:\v\a.txt").Should().BeTrue();
        WinPath.PathEquals(@"C:\v\a", @"C:\v\b").Should().BeFalse();
        WinPath.Comparer.Equals("ABC", "abc").Should().BeTrue();
        WinPath.IsSeparator('/').Should().BeTrue();
        WinPath.IsSeparator(':').Should().BeFalse();
    }

    [Fact]
    public void Guards()
    {
        FluentActions.Invoking(() => WinPath.GetFileName(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WinPath.GetDirectoryName(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WinPath.RootLength(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WinPath.Combine(null!, "a")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WinPath.Combine("a", null!)).Should().Throw<ArgumentNullException>();
    }
}

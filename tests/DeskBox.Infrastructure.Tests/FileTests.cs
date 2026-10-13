using DeskBox.Infrastructure.Files;

namespace DeskBox.Infrastructure.Tests;

public sealed class FileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deskbox-tests", Guid.NewGuid().ToString("N"));

    public FileTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Vault_index_enumerates_files_and_folders_recursively()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub", "deep"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "sub", "deep", "b (2).txt"), "b");
        new VaultDirectoryIndex(_root).EnumerateFiles().Select(p => Path.GetRelativePath(_root, p)).Should().BeEquivalentTo(
            "a.txt", "sub", Path.Combine("sub", "deep"), Path.Combine("sub", "deep", "b (2).txt"));
    }

    [Fact]
    public void Vault_index_missing_root_is_empty_and_guards()
    {
        new VaultDirectoryIndex(Path.Combine(_root, "nope")).EnumerateFiles().Should().BeEmpty();
        FluentActions.Invoking(() => new VaultDirectoryIndex(" ")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void File_id_format_matches_domain_rule()
    {
        var id = FileIdFormat.Format(0x1_2345_ABCD, 0x1, 0xFF);
        id.Should().Be("2345ABCD:000000000000000100000000000000FF");
        Domain.Entities.FenceItem.IsValidFileId(id).Should().BeTrue();
    }

    [WindowsFact]
    public void Windows_file_id_is_stable_across_rename_and_unique_per_file()
    {
        if (OperatingSystem.IsWindows()) RunWindows();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void RunWindows()
    {
        var a = Path.Combine(_root, "a.txt");
        var b = Path.Combine(_root, "b.txt");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        var sut = new WindowsFileIdentity();
        var idA = sut.TryGetFileId(a);
        Domain.Entities.FenceItem.IsValidFileId(idA).Should().BeTrue();
        sut.TryGetFileId(b).Should().NotBe(idA);
        var renamed = Path.Combine(_root, "renamed. ");
        File.Move(a, @"\\?\" + renamed);
        sut.TryGetFileId(renamed).Should().Be(idA, "改名（含末尾空格/点）后 file_id 不变");
        sut.TryGetFileId(a).Should().BeNull();
        sut.TryGetFileId(_root).Should().NotBeNull("目录也有 file_id");
        FluentActions.Invoking(() => sut.TryGetFileId("")).Should().Throw<ArgumentException>();
    }
}

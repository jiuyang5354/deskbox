using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DeskBox.Application.Ports;
using DeskBox.Infrastructure.Files;

namespace DeskBox.Infrastructure.Tests;

/// <summary>任务 3 的真实文件系统测试。Windows 用例都很快且确定，跑在常规 CI（windows-2022）里。</summary>
public sealed class DesktopFsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deskbox-fs", Guid.NewGuid().ToString("N"));

    public DesktopFsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // 先删联接点本身（不跟随），再删其余内容
        var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (var d in Directory.GetDirectories(_root, "*", opts))
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d);
        Directory.Delete(_root, recursive: true);
    }

    // ---------- 跨平台：解析与监听 ----------
    private static byte[] Entry(string name, uint attributes, ulong low, ulong high, long lastWrite, int next)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var b = new byte[next == 0 ? 88 + nameBytes.Length : next];
        BinaryPrimitives.WriteInt32LittleEndian(b, next);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), lastWrite);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(56), attributes);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(60), nameBytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(72), low);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(80), high);
        nameBytes.CopyTo(b, 88);
        return b;
    }

    [Fact]
    public void Parses_file_id_extd_dir_info_and_skips_dot_entries()
    {
        var t = new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero).ToFileTime();
        var buf = Entry(".", 0x10, 1, 0, t, 96).Concat(Entry("照片😀.png", 0x400020, 0xFF, 0x1, t, 0)).ToArray();
        var list = new List<DirectoryItem>();
        DirectoryInfoParser.Parse(buf, @"C:\Users\u\Desktop", 0x2345ABCD, list);
        list.Should().ContainSingle().Which.Should().Be(new DirectoryItem(@"C:\Users\u\Desktop\照片😀.png", 0x400020,
            "2345ABCD:000000000000000100000000000000FF", DateTimeOffset.FromFileTime(t).ToUniversalTime()));
        list[0].IsCloudOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Watcher_reports_direct_children_and_missing_dir_is_noop()
    {
        var seen = new List<DesktopChange>();
        var got = new TaskCompletionSource();
        using (new FileSystemDirectoryWatcher().Watch(_root, c => { lock (seen) seen.Add(c); got.TrySetResult(); }))
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "x");
            await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        lock (seen) seen.Should().Contain(c => c.FullPath.EndsWith("new.txt", StringComparison.Ordinal));
        using var none = new FileSystemDirectoryWatcher().Watch(Path.Combine(_root, "missing"), _ => { });
        FluentActions.Invoking(() => new FileSystemDirectoryWatcher().Watch(" ", _ => { })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new FileSystemDirectoryWatcher().Watch(_root, null!)).Should().Throw<ArgumentNullException>();
    }

    // ---------- Windows 真实文件系统 ----------
    [WindowsFact]
    public void Junction_on_desktop_is_listed_but_its_files_are_not()
    {
        if (OperatingSystem.IsWindows()) JunctionWindows();
    }

    [SupportedOSPlatform("windows")]
    private void JunctionWindows()
    {
        var desk = Directory.CreateDirectory(Path.Combine(_root, "Desktop")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(target, "secret.txt"), "s");
        File.WriteAllText(Path.Combine(desk, "a.txt"), "a");
        var link = Path.Combine(desk, "link");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            p.WaitForExit();
            p.ExitCode.Should().Be(0);
        }

        var items = new WindowsDirectoryLister().List(desk);

        items.Select(i => Path.GetFileName(i.FullPath)).Should().BeEquivalentTo("a.txt", "link");
        items.Should().NotContain(i => i.FullPath.Contains("secret", StringComparison.OrdinalIgnoreCase));
        var j = items.Single(i => i.FullPath.EndsWith("link", StringComparison.Ordinal));
        (j.IsReparsePoint, j.IsDirectory).Should().Be((true, true));
        var a = items.Single(i => i.FullPath.EndsWith("a.txt", StringComparison.Ordinal));
        a.FileId.Should().Be(new WindowsFileIdentity().TryGetFileId(Path.Combine(desk, "a.txt")), "目录项里的 file_id 与按句柄读取的一致");
        j.FileId.Should().Be(new WindowsFileIdentity().TryGetFileId(link), "联接点自身的 file_id，而不是目标目录的");
        new VaultDirectoryIndex(desk).EnumerateFiles().Should().NotContain(p => p.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [WindowsFact]
    public void Long_paths_over_260_chars_list_identify_and_move()
    {
        if (OperatingSystem.IsWindows()) LongPathWindows();
    }

    [SupportedOSPlatform("windows")]
    private void LongPathWindows()
    {
        var deep = _root;
        while (deep.Length < 300) deep = Path.Combine(deep, new string('d', 50));
        Directory.CreateDirectory(deep);
        var src = Path.Combine(deep, "长文件名😀.txt");
        File.WriteAllText(src, "x");
        (src.Length > 260).Should().BeTrue();

        var item = new WindowsDirectoryLister().List(deep).Single();
        item.FullPath.Should().Be(src);
        item.FileId.Should().Be(new WindowsFileIdentity().TryGetFileId(src));
        new WindowsFileSystemProbe().GetAttributes(src).Should().NotBeNull();
        new WindowsFileSystemProbe().IsDirectory(deep).Should().BeTrue();

        var dst = Path.Combine(deep, "moved.txt");
        var mover = new Win32NativeFileMove();
        mover.Move(src, dst).Should().Be(Win32Errors.Success);
        File.WriteAllText(src, "again");
        mover.Move(src, dst).Should().BeOneOf(Win32Errors.FileExists, Win32Errors.AlreadyExists);
        File.ReadAllText(dst).Should().Be("x", "不覆盖已有文件");
    }

    [WindowsFact]
    public void Open_file_without_sharing_yields_sharing_violation_from_move_itself()
    {
        if (OperatingSystem.IsWindows()) LockedWindows();
    }

    [SupportedOSPlatform("windows")]
    private void LockedWindows()
    {
        var src = Path.Combine(_root, "locked.txt");
        File.WriteAllText(src, "x");
        using (new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            new Win32NativeFileMove().Move(src, Path.Combine(_root, "out.txt")).Should().Be(Win32Errors.SharingViolation);
        File.Exists(src).Should().BeTrue();
        new Win32NativeFileMove().Move(Path.Combine(_root, "missing.txt"), Path.Combine(_root, "x.txt")).Should().Be(Win32Errors.FileNotFound);
    }

    [SkippableFact]
    public void Cross_volume_move_fails_with_17_and_does_not_copy()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "需要 Windows");
        // GitHub windows-2022：TEMP 在 C:，工作区在 D:
        var other = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        Skip.If(other is null || string.Equals(Path.GetPathRoot(other), Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase),
            $"没有第二个卷可测（TEMP={Path.GetPathRoot(_root)}, GITHUB_WORKSPACE={other ?? "<unset>"}）");
        if (OperatingSystem.IsWindows()) CrossVolumeWindows(other!);
    }

    [SupportedOSPlatform("windows")]
    private void CrossVolumeWindows(string other)
    {
        var src = Path.Combine(_root, "x.txt");
        File.WriteAllText(src, "x");
        var dst = Path.Combine(other, $"deskbox-xvol-{Guid.NewGuid():N}.txt");
        new Win32NativeFileMove().Move(src, dst).Should().Be(Win32Errors.NotSameDevice);
        File.Exists(dst).Should().BeFalse("没有 MOVEFILE_COPY_ALLOWED，不会复制");
        File.Exists(src).Should().BeTrue();
    }

    [WindowsFact]
    public void Known_folders_and_lister_guards()
    {
        if (OperatingSystem.IsWindows()) FoldersWindows();
    }

    [SupportedOSPlatform("windows")]
    private void FoldersWindows()
    {
        var f = new WindowsKnownFolders();
        f.UserDesktop.Should().NotBeNullOrWhiteSpace();
        f.PublicDesktop.Should().NotBeNullOrWhiteSpace();
        FluentActions.Invoking(() => new WindowsDirectoryLister().List(Path.Combine(_root, "missing"))).Should().Throw<IOException>();
        FluentActions.Invoking(() => new WindowsDirectoryLister().List(" ")).Should().Throw<ArgumentException>();
        new WindowsDirectoryLister().List(Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName).Should().BeEmpty();
        new WindowsFileSystemProbe().GetAttributes(Path.Combine(_root, "missing")).Should().BeNull();
    }
}

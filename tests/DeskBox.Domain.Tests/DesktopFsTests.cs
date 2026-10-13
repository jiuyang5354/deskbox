using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;
using NSubstitute;

namespace DeskBox.Domain.Tests;

/// <summary>TDD 任务 3：桌面枚举、安全移动、占用与云端文件检测（QA 必测项）。</summary>
public class DesktopFsTests
{
    private const string Id = "1a2b3c4d:000000000000000000000000000000aa";
    private const string Src = @"C:\Users\u\Desktop\a.txt";
    private const string Dst = @"C:\Users\u\DeskBox\Vault\a.txt";

    private readonly FakeDb _db = new();
    private readonly InMemoryJournal _journal = new();
    private readonly IRecoveryLog _log = Substitute.For<IRecoveryLog>();

    private static IKnownFolders Folders()
    {
        var f = Substitute.For<IKnownFolders>();
        f.PublicDesktop.Returns(@"C:\Users\Public\Desktop");
        f.UserDesktop.Returns(@"C:\Users\u\Desktop");
        return f;
    }

    private static IFileSystemProbe Probe(uint attributes)
    {
        var p = Substitute.For<IFileSystemProbe>();
        p.GetAttributes(Arg.Any<string>()).Returns(attributes);
        return p;
    }

    private static IFileIdentity Identity()
    {
        var i = Substitute.For<IFileIdentity>();
        i.TryGetFileId(Src).Returns(Id);
        return i;
    }

    /// <summary>任何"打开文件"的接口被调用就让测试失败。</summary>
    private static IFileIdentity OpenMustNotHappen()
    {
        var i = Substitute.For<IFileIdentity>();
        i.TryGetFileId(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("不允许打开云端文件"));
        return i;
    }

    private static INativeFileMove MoveMustNotHappen()
    {
        var n = Substitute.For<INativeFileMove>();
        n.Move(Arg.Any<string>(), Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("不允许移动云端文件"));
        return n;
    }

    // ---------- (1) 云端文件：只看属性位，绝不打开 ----------
    [Theory]
    [InlineData(Win32FileAttributes.RecallOnDataAccess)]
    [InlineData(Win32FileAttributes.RecallOnOpen)]
    [InlineData(Win32FileAttributes.Offline)]
    public async Task Cloud_attribute_bit_skips_without_opening_or_moving(uint bit)
    {
        var probe = Probe(bit | 0x20);
        var sut = new JournaledMove(_db, new RenamingFileMover(MoveMustNotHappen(), probe, Folders()), _journal,
            OpenMustNotHappen(), probe, _log, TimeProvider.System);

        var r = await sut.ExecuteAsync(Guid.NewGuid(), Src, Dst);

        r.Should().Be(new MoveResult.Skipped(Src, SkipReason.CloudOnly));
        _journal.Entries.Should().BeEmpty("跳过云端文件不写 Journal");
        _db.Commits.Should().Be(0);
        Win32FileAttributes.IsCloudOnly(bit).Should().BeTrue();
    }

    [Theory]
    [InlineData(0x20u)]          // ARCHIVE
    [InlineData(0x01u | 0x02u)]  // READONLY | HIDDEN
    [InlineData(0x0400u)]        // REPARSE_POINT（本身不是云端）
    [InlineData(0x0010u)]        // DIRECTORY
    public void Non_cloud_attributes_are_not_cloud_only(uint a) => Win32FileAttributes.IsCloudOnly(a).Should().BeFalse();

    [Fact]
    public async Task Enumerating_a_cloud_placeholder_never_opens_it()
    {
        var lister = Substitute.For<IDirectoryLister>();
        lister.List(@"C:\Users\u\Desktop").Returns([new DirectoryItem(Src, Win32FileAttributes.RecallOnDataAccess, Id, default)]);
        var identity = OpenMustNotHappen(); // 枚举根本不依赖它；保留以防将来误用
        var sut = new DesktopEnumerator(Folders(), lister, Substitute.For<IDirectoryWatcher>());

        var items = await Collect(sut);

        items.Should().ContainSingle().Which.FileId.Should().Be(Id.ToUpperInvariant());
        identity.ReceivedCalls().Should().BeEmpty();
    }

    // ---------- (2) 占用：只依赖 MoveFileExW 的 32 / 33，Journal 以 Abort 结束 ----------
    [Theory]
    [InlineData(Win32Errors.SharingViolation)]
    [InlineData(Win32Errors.LockViolation)]
    public async Task Locked_error_maps_to_locked_and_pending_finishes_as_abort(int error)
    {
        var native = Substitute.For<INativeFileMove>();
        native.Move(Src, Dst).Returns(error);
        var sut = new JournaledMove(_db, new RenamingFileMover(native, Probe(0x20), Folders()), _journal, Identity(), Probe(0x20), _log, TimeProvider.System);

        var r = await sut.ExecuteAsync(Guid.NewGuid(), Src, Dst);

        r.Should().Be(new MoveResult.Skipped(Src, SkipReason.Locked, error));
        var e = _journal.Entries.Values.Single();
        (e.Status, e.AbortReason).Should().Be((JournalStatus.RolledBack, $"Locked (Win32 {error})"));
        _journal.Entries.Values.Should().NotContain(x => x.Status == JournalStatus.Pending);
        native.Received(1).Move(Src, Dst); // 不预先打开探测，第一次调用就是移动本身
    }

    // ---------- (3) 跨分区：17 立即跳过，不复制、不重试 ----------
    [Fact]
    public async Task Cross_volume_skips_immediately_without_copy_or_retry()
    {
        var native = Substitute.For<INativeFileMove>();
        native.Move(Src, @"D:\Vault\a.txt").Returns(Win32Errors.NotSameDevice);
        var sut = new JournaledMove(_db, new RenamingFileMover(native, Probe(0x20), Folders()), _journal, Identity(), Probe(0x20), _log, TimeProvider.System);

        var r = await sut.ExecuteAsync(Guid.NewGuid(), Src, @"D:\Vault\a.txt");

        r.Should().Be(new MoveResult.Skipped(Src, SkipReason.CrossVolume, 17));
        native.ReceivedCalls().Should().ContainSingle("只尝试一次，不改名重试，也没有复制回退");
        _journal.Entries.Values.Single().AbortReason.Should().Be("CrossVolume (Win32 17)");
    }

    // ---------- (4) 顺序：查重 + 写 Pending（同一事务）→ 写候选 → 移动 → Commit ----------
    [Fact]
    public async Task Order_is_precheck_insert_then_candidate_then_move_then_commit()
    {
        var journal = Substitute.For<IJournal>();
        var native = Substitute.For<INativeFileMove>();
        native.Move(Src, Dst).Returns(Win32Errors.Success);
        var db = new FakeDb();
        var sut = new JournaledMove(db, new RenamingFileMover(native, Probe(0x20), Folders()), journal, Identity(), Probe(0x20), _log, TimeProvider.System);

        (await sut.ExecuteAsync(Guid.NewGuid(), Src, Dst)).Should().BeOfType<MoveResult.Moved>();

        Received.InOrder(() =>
        {
            journal.HasPendingAsync(Arg.Any<IUnitOfWork>(), Id.ToUpperInvariant(), Arg.Any<CancellationToken>());
            journal.AppendPendingAsync(Arg.Any<IUnitOfWork>(), Arg.Any<JournalEntry>(), Arg.Any<CancellationToken>());
            journal.UpdateCandidateAsync(Arg.Any<IUnitOfWork>(), Arg.Any<Guid>(), Dst, 1, Arg.Any<CancellationToken>());
            native.Move(Src, Dst);
            journal.CommitAsync(Arg.Any<IUnitOfWork>(), Arg.Any<Guid>(), Dst, Arg.Any<CancellationToken>());
        });
        db.Commits.Should().Be(3, "Begin（查重 + 插入）、候选、Commit 各一个事务");
    }

    // ---------- 枚举 ----------
    private static async Task<List<DesktopItem>> Collect(DesktopEnumerator e, CancellationToken ct = default)
    {
        var list = new List<DesktopItem>();
        await foreach (var i in e.EnumerateAsync(ct)) list.Add(i);
        return list;
    }

    [Fact]
    public async Task Enumerates_both_desktops_without_descending_into_directories_or_reparse_points()
    {
        var lister = Substitute.For<IDirectoryLister>();
        var junction = new DirectoryItem(@"C:\Users\u\Desktop\link", Win32FileAttributes.Directory | Win32FileAttributes.ReparsePoint,
            "1a2b3c4d:000000000000000000000000000000b1", default);
        var folder = new DirectoryItem(@"C:\Users\u\Desktop\folder", Win32FileAttributes.Directory, "1a2b3c4d:000000000000000000000000000000b2", default);
        lister.List(@"C:\Users\u\Desktop").Returns([junction, folder, new DirectoryItem(Src, 0x20, Id, DateTimeOffset.UnixEpoch)]);
        lister.List(@"C:\Users\Public\Desktop").Returns([new DirectoryItem(@"C:\Users\Public\Desktop\app.lnk", 0x20, "1a2b3c4d:000000000000000000000000000000c1", default)]);
        var sut = new DesktopEnumerator(Folders(), lister, Substitute.For<IDirectoryWatcher>());

        var items = await Collect(sut);

        items.Select(i => (i.FullPath, i.IsDirectory, i.IsPublicDesktop)).Should().Equal(
            (@"C:\Users\u\Desktop\link", true, false), (@"C:\Users\u\Desktop\folder", true, false), (Src, false, false),
            (@"C:\Users\Public\Desktop\app.lnk", false, true));
        lister.ReceivedCalls().Select(c => (string)c.GetArguments()[0]!).Should().Equal(@"C:\Users\u\Desktop", @"C:\Users\Public\Desktop");
        junction.IsReparsePoint.Should().BeTrue();
        folder.IsReparsePoint.Should().BeFalse();
    }

    [Fact]
    public async Task Duplicate_or_invalid_file_ids_are_skipped_and_failing_desktop_does_not_block_the_other()
    {
        var lister = Substitute.For<IDirectoryLister>();
        lister.List(@"C:\Users\u\Desktop").Returns(_ => throw new UnauthorizedAccessException("denied"));
        lister.List(@"C:\Users\Public\Desktop").Returns([
            new DirectoryItem(@"C:\Users\Public\Desktop\a.lnk", 0x20, Id, default),
            new DirectoryItem(@"C:\Users\Public\Desktop\dup.lnk", 0x20, Id.ToUpperInvariant(), default),
            new DirectoryItem(@"C:\Users\Public\Desktop\bad.lnk", 0x20, "nope", default)]);
        var sut = new DesktopEnumerator(Folders(), lister, Substitute.For<IDirectoryWatcher>());
        (await Collect(sut)).Should().ContainSingle().Which.FullPath.Should().Be(@"C:\Users\Public\Desktop\a.lnk");

        lister.List(@"C:\Users\Public\Desktop").Returns(_ => throw new IOException("gone"));
        (await Collect(sut)).Should().BeEmpty();
    }

    [Fact]
    public async Task Blank_desktop_paths_are_ignored_and_cancellation_is_honoured()
    {
        var f = Substitute.For<IKnownFolders>();
        f.UserDesktop.Returns("");
        f.PublicDesktop.Returns(" ");
        var lister = Substitute.For<IDirectoryLister>();
        (await Collect(new DesktopEnumerator(f, lister, Substitute.For<IDirectoryWatcher>()))).Should().BeEmpty();
        lister.ReceivedCalls().Should().BeEmpty();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await FluentActions.Awaiting(() => Collect(new DesktopEnumerator(Folders(), lister, Substitute.For<IDirectoryWatcher>()), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Start_watches_both_desktops_once_forwards_changes_and_dispose_stops()
    {
        var watcher = Substitute.For<IDirectoryWatcher>();
        var subs = new List<IDisposable>();
        Action<DesktopChange>? sink = null;
        watcher.Watch(Arg.Any<string>(), Arg.Any<Action<DesktopChange>>()).Returns(c =>
        {
            sink = c.Arg<Action<DesktopChange>>();
            var d = Substitute.For<IDisposable>();
            subs.Add(d);
            return d;
        });
        var sut = new DesktopEnumerator(Folders(), Substitute.For<IDirectoryLister>(), watcher);
        var seen = new List<DesktopChange>();
        sut.Changed += (_, c) => seen.Add(c);

        sut.Start();
        sut.Start();
        watcher.ReceivedCalls().Should().HaveCount(2);
        var change = new DesktopChange(DesktopChangeKind.Created, Src, null);
        sink!(change);
        seen.Should().Equal(change);

        sut.Dispose();
        foreach (var d in subs) d.Received(1).Dispose();
        var bare = new DesktopEnumerator(Folders(), Substitute.For<IDirectoryLister>(), watcher);
        FluentActions.Invoking(() => sink(change)).Should().NotThrow();
        bare.Dispose();
    }

    [Fact]
    public void Constructor_guards_and_directory_item_flags()
    {
        FluentActions.Invoking(() => new DesktopEnumerator(null!, Substitute.For<IDirectoryLister>(), Substitute.For<IDirectoryWatcher>())).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new DesktopEnumerator(Folders(), null!, Substitute.For<IDirectoryWatcher>())).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new DesktopEnumerator(Folders(), Substitute.For<IDirectoryLister>(), null!)).Should().Throw<ArgumentNullException>();
        var cloud = new DirectoryItem("p", Win32FileAttributes.Offline, Id, default);
        (cloud.IsCloudOnly, cloud.IsDirectory, cloud.IsReparsePoint).Should().Be((true, false, false));
    }
}

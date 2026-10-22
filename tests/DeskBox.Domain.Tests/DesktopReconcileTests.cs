using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Application.Startup;
using DeskBox.Domain.Entities;
using NSubstitute;

namespace DeskBox.Domain.Tests;

/// <summary>启动对账（TDD v1.0.14 §4.5.5，PRD v1.2.8 §7B）。</summary>
public sealed class DesktopReconcileTests
{
    private const string User = @"C:\Users\u\Desktop";
    private const string Public = @"C:\Users\Public\Desktop";

    private sealed class Folders : IKnownFolders
    {
        public string UserDesktop { get; set; } = User;
        public string PublicDesktop { get; set; } = Public;
    }

    /// <summary>假目录：每个目录一组项，或者读取时抛异常。</summary>
    private sealed class Lister : IDirectoryLister
    {
        public Dictionary<string, List<DirectoryItem>> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase) { [User] = [], [Public] = [] };
        public Dictionary<string, Exception> Fails { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<DirectoryItem> List(string directory) =>
            Fails.TryGetValue(directory, out var ex) ? throw ex : Dirs[directory];
    }

    private sealed class Log : IDesktopScanLog
    {
        public List<string> Lines { get; } = [];
        public void DesktopScanSkipped(string reason) => Lines.Add("skipped:" + reason);
        public void DesktopScanStarted() => Lines.Add("started");
        public void DesktopScanCompleted(int found, int written, int pruned, bool skipped, int invalid) => Lines.Add($"completed found={found} written={written} pruned={pruned} skipped={skipped} skippedInvalid={invalid}");
        public void FenceItemPruned(string fileName) => Lines.Add("pruned:" + fileName);
        public void DesktopScanPruneSkipped(string reason) => Lines.Add("pruneSkipped:" + reason);
        public void StagingFenceMissing() => Lines.Add("stagingMissing");
        public void DesktopReconcileFailed(string error) => Lines.Add("failed:" + error);
        public void DesktopItemSkippedInvalidFileId(string fileName) => Lines.Add("invalidId:" + fileName);
    }

    private sealed class Tray : ITrayNotifier
    {
        public List<string> Notices { get; } = [];
        public void ShowNotice(string text) => Notices.Add(text);
    }

    private readonly FakeDb _db = new();
    private readonly InMemoryFences _fences = new();
    private readonly InMemoryFenceItems _items = new();
    private readonly Folders _folders = new();
    private readonly Lister _lister = new();
    private readonly Log _log = new();
    private readonly Tray _tray = new();
    private readonly OperatingMode _mode = new();
    private readonly INativeFileMove _native = Substitute.For<INativeFileMove>();
    private readonly Fence _staging = new(Guid.NewGuid(), "暂存", "M1", 24, 24, 280, 320, isStaging: true);
    private readonly Fence _other = new(Guid.NewGuid(), "工作", "M1", 400, 24, 280, 320);
    private int _nextId;

    public DesktopReconcileTests()
    {
        _fences.Rows[_staging.Id] = _staging;
        _fences.Rows[_other.Id] = _other;
    }

    private DesktopReconcile Sut() => new(new DesktopScanner(_folders, _lister), new DesktopReconciler(_db, _fences, _items, _log), _mode, _log, _tray);

    private string NewFileId() => $"1a2b3c4d:{++_nextId:x32}";

    private DirectoryItem File(string dir, string name, uint attrs = 0x20) => new(Path.Combine(dir, name).Replace('/', '\\'), attrs, NewFileId(), DateTimeOffset.UnixEpoch);

    private void OnDisk(string dir, params string[] names)
    {
        foreach (var n in names) _lister.Dirs[dir].Add(File(dir, n));
    }

    private FenceItem Existing(Fence fence, string path, int sort)
    {
        var item = new FenceItem(Guid.NewGuid(), fence.Id, path, NewFileId(), path.StartsWith(Public, StringComparison.OrdinalIgnoreCase), sort);
        _items.Rows[item.Id] = item;
        return item;
    }

    private List<string> StagingNames() => _items.Rows.Values.Where(i => i.FenceId == _staging.Id).OrderBy(i => i.SortOrder)
        .Select(i => DeskBox.Domain.Files.WinPath.GetFileName(i.FilePath)).ToList();

    // 1
    [Fact]
    public async Task First_start_collects_n_files_into_staging_in_natural_order_and_notifies_once()
    {
        OnDisk(User, "b.txt", "A.lnk", "工作资料");
        OnDisk(Public, "c.url");

        var r = await Sut().RunAsync();

        r!.Written.Should().Be(4);
        StagingNames().Should().Equal("A.lnk", "b.txt", "c.url", "工作资料");
        _items.Rows.Values.Select(i => i.SortOrder).Order().Should().Equal(0, 1, 2, 3);
        _items.Rows.Values.Single(i => i.FilePath.EndsWith("c.url")).IsPublicDesktop.Should().BeTrue("公共桌面只记归属");
        _log.Lines.Should().ContainSingle(l => l.StartsWith("completed")).Which.Should().Be("completed found=4 written=4 pruned=0 skipped=False skippedInvalid=0");
        _tray.Notices.Should().Equal("已收纳 4 个桌面文件");
        _db.Commits.Should().Be(1, "一个事务");
    }

    // 2
    [Fact]
    public async Task Natural_sort_is_case_insensitive_and_numeric()
    {
        OnDisk(User, "file10", "file2", "File1");
        await Sut().RunAsync();
        StagingNames().Should().Equal("File1", "file2", "file10");
    }

    // 3
    [Fact]
    public async Task New_files_append_after_current_max_sort_order_of_staging()
    {
        Existing(_staging, User + @"\old.txt", 7);
        OnDisk(User, "old.txt", "new.txt");
        await Sut().RunAsync();
        _items.Rows.Values.Single(i => i.FilePath.EndsWith("new.txt")).SortOrder.Should().Be(8);
    }

    // 4
    [Fact]
    public async Task Restart_does_not_collect_twice()
    {
        OnDisk(User, "a.txt", "b.txt");
        await Sut().RunAsync();
        _tray.Notices.Clear();

        var r = await Sut().RunAsync();

        r!.Written.Should().Be(0);
        _items.Rows.Should().HaveCount(2);
        _tray.Notices.Should().BeEmpty("写入 0 行不提示");
        _log.Lines.Last().Should().Be("completed found=2 written=0 pruned=0 skipped=False skippedInvalid=0");
    }

    // 5
    [Fact]
    public async Task File_already_in_another_fence_keeps_its_fence_and_position_and_is_not_added_to_staging()
    {
        var mine = Existing(_other, User + @"\report.docx", 3);
        OnDisk(User, "REPORT.docx", "x.txt");

        await Sut().RunAsync();

        var after = _items.Rows[mine.Id];
        (after.FenceId, after.SortOrder, after.FilePath).Should().Be((_other.Id, 3, User + @"\report.docx"));
        _items.Rows.Values.Where(i => i.FilePath.EndsWith("report.docx", StringComparison.OrdinalIgnoreCase)).Should().ContainSingle();
        StagingNames().Should().Equal("x.txt");
    }

    // 6
    [Fact]
    public async Task Rows_for_files_deleted_from_disk_are_pruned_and_logged_by_name_only()
    {
        Existing(_other, User + @"\gone.txt", 0);
        Existing(_staging, Public + @"\gone2.lnk", 0);
        OnDisk(User, "kept.txt");

        var r = await Sut().RunAsync();

        r!.Pruned.Should().Be(2);
        _items.Rows.Values.Select(i => i.FilePath).Should().Equal(User + @"\kept.txt");
        _log.Lines.Where(l => l.StartsWith("pruned:")).Should().BeEquivalentTo("pruned:gone.txt", "pruned:gone2.lnk");
        _log.Lines.Last().Should().Be("completed found=1 written=1 pruned=2 skipped=False skippedInvalid=0");
    }

    // 7 / 8：一个桌面读失败、另一个成功 → 一行不删，并写明是哪个桌面
    [Theory]
    [InlineData(true, "用户桌面读取失败")]
    [InlineData(false, "公共桌面读取失败")]
    public async Task One_desktop_read_failure_deletes_nothing_and_names_the_failed_desktop(bool userFails, string expected)
    {
        Existing(_other, User + @"\a.txt", 0);
        Existing(_other, Public + @"\b.lnk", 1);
        OnDisk(userFails ? Public : User, "new.txt");
        _lister.Fails[userFails ? User : Public] = new IOException("access");

        var r = await Sut().RunAsync();

        r!.Pruned.Should().Be(0);
        r.PruneSkipped.Should().BeTrue();
        _items.Rows.Values.Count(i => i.FenceId == _other.Id).Should().Be(2, "一行都不删");
        _log.Lines.Should().ContainSingle(l => l.StartsWith("pruneSkipped:")).Which.Should().Contain(expected).And.Contain("IOException");
        _log.Lines.Should().NotContain(l => l.StartsWith("pruned:"));
        StagingNames().Should().Equal(new[] { "new.txt" }, "读成功的桌面照常收");
    }

    // 9
    [Fact]
    public async Task Desktop_directory_read_failure_on_both_deletes_nothing()
    {
        Existing(_other, User + @"\a.txt", 0);
        _lister.Fails[User] = new UnauthorizedAccessException("denied");
        _lister.Fails[Public] = new IOException("gone");

        var r = await Sut().RunAsync();

        (r!.Pruned, r.PruneSkipped).Should().Be((0, true));
        _items.Rows.Should().HaveCount(1);
        _log.Lines.Single(l => l.StartsWith("pruneSkipped:")).Should().Contain("用户桌面读取失败").And.Contain("公共桌面读取失败");
    }

    // 10
    [Fact]
    public async Task Error_in_the_middle_of_scanning_deletes_nothing()
    {
        Existing(_other, User + @"\a.txt", 0);
        Existing(_other, User + @"\b.txt", 1);
        OnDisk(User, "a.txt");
        _lister.Fails[Public] = new InvalidDataException("bad buffer");

        var r = await Sut().RunAsync();

        (r!.Pruned, r.PruneSkipped).Should().Be((0, true));
        _items.Rows.Should().HaveCount(2, "b.txt 不在扫描结果里，但扫描不完整，不能删");
        _log.Lines.Single(l => l.StartsWith("pruneSkipped:")).Should().Contain("扫描中途出错").And.Contain("InvalidDataException");
    }

    // 11
    [Fact]
    public async Task Zero_items_found_while_db_has_rows_deletes_nothing()
    {
        Existing(_other, User + @"\a.txt", 0);
        Existing(_staging, User + @"\b.txt", 0);

        var r = await Sut().RunAsync();

        (r!.Found, r.Pruned, r.PruneSkipped).Should().Be((0, 0, true));
        _items.Rows.Should().HaveCount(2);
        _log.Lines.Single(l => l.StartsWith("pruneSkipped:")).Should().Contain("扫描到 0 项但库里有 2 行");
    }

    // 12
    [Fact]
    public async Task Missing_staging_fence_writes_nothing_and_logs_staging_fence_missing()
    {
        _fences.Rows.Remove(_staging.Id);
        var mine = Existing(_other, User + @"\gone.txt", 0);
        OnDisk(User, "new.txt");
        var writes = _items.Writes;

        var r = await Sut().RunAsync();

        r!.StagingMissing.Should().BeTrue();
        _items.Writes.Should().Be(writes, "整个事务不写");
        _items.Rows.Keys.Should().Equal(mine.Id);
        _fences.Rows.Values.Should().NotContain(f => f.IsStaging, "不在这里重建暂存方框");
        _log.Lines.Should().Equal("stagingMissing");
        _tray.Notices.Should().BeEmpty();
    }

    // 13
    [Fact]
    public async Task Read_only_mode_skips_reconcile_and_logs_desktop_scan_skipped()
    {
        OnDisk(User, "a.txt");
        _mode.EnterReadOnly("1 条未完成的整理记录恢复失败");

        var r = await Sut().RunAsync();

        r.Should().BeNull();
        _items.Rows.Should().BeEmpty();
        _db.Commits.Should().Be(0);
        _log.Lines.Should().Equal("skipped:1 条未完成的整理记录恢复失败");
    }

    // 14
    [Fact]
    public async Task Hidden_and_system_items_are_not_collected_but_folders_shortcuts_and_cloud_only_are()
    {
        _lister.Dirs[User].Add(File(User, "desktop.ini", 0x2 | 0x4 | 0x20));
        _lister.Dirs[User].Add(File(User, "secret.txt", 0x2));
        _lister.Dirs[User].Add(File(User, "sys.dat", 0x4));
        _lister.Dirs[User].Add(File(User, "文件夹", 0x10));
        _lister.Dirs[User].Add(File(User, "app.lnk"));
        _lister.Dirs[User].Add(File(User, "cloud.docx", 0x0040_0000 | 0x20));

        await Sut().RunAsync();

        StagingNames().Should().Equal("app.lnk", "cloud.docx", "文件夹");
    }

    // 15
    [Fact]
    public async Task Same_file_name_on_user_and_public_desktop_are_two_different_paths_and_both_collected()
    {
        OnDisk(User, "a.txt");
        OnDisk(Public, "a.txt");

        await Sut().RunAsync();

        _items.Rows.Values.Select(i => (i.FilePath, i.IsPublicDesktop)).Should().BeEquivalentTo(new[]
        {
            (User + @"\a.txt", false), (Public + @"\a.txt", true),
        });
    }

    // 16
    [Fact]
    public async Task Identical_full_path_appearing_twice_with_different_case_is_collected_once()
    {
        _lister.Dirs[User].Add(File(User, "a.txt"));
        _lister.Dirs[User].Add(new DirectoryItem(@"C:\USERS\U\DESKTOP\A.TXT", 0x20, NewFileId(), DateTimeOffset.UnixEpoch));

        var r = await Sut().RunAsync();

        r!.Written.Should().Be(1);
        _items.Rows.Should().ContainSingle();
    }

    // 17
    [Fact]
    public async Task Reconcile_never_moves_files()
    {
        OnDisk(User, "a.txt");
        Existing(_other, User + @"\gone.txt", 0);

        await Sut().RunAsync();

        _native.ReceivedCalls().Should().BeEmpty("对账从不调用 MoveFileExW");
        // 结构上保证：对账的三个类都不依赖任何文件移动接口
        foreach (var t in new[] { typeof(DesktopReconcile), typeof(DesktopReconciler), typeof(DesktopScanner) })
            t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .Should().NotContain(new[] { typeof(INativeFileMove), typeof(IFileMover) }, t.Name);
    }

    [Fact]
    public async Task Item_with_invalid_file_id_is_neither_collected_nor_pruned()
    {
        var keep = Existing(_other, User + @"\odd.txt", 0);
        _lister.Dirs[User].Add(new DirectoryItem(User + @"\odd.txt", 0x20, "not-an-id", DateTimeOffset.UnixEpoch));
        _lister.Dirs[User].Add(new DirectoryItem(User + @"\odd2.txt", 0x20, "not-an-id", DateTimeOffset.UnixEpoch));

        var r = await Sut().RunAsync();

        (r!.Written, r.Pruned, r.SkippedInvalid).Should().Be((0, 0, 2));
        _items.Rows.Keys.Should().Equal(keep.Id);
        _log.Lines.Last().Should().EndWith("skippedInvalid=2");
        _log.Lines.Where(l => l.StartsWith("invalidId:")).Should().Equal(new[] { "invalidId:odd.txt", "invalidId:odd2.txt" }, "每项一条警告，只写文件名");
    }

    [Fact]
    public async Task Empty_desktop_path_counts_as_read_failure()
    {
        _folders.PublicDesktop = "";
        Existing(_other, User + @"\a.txt", 0);
        var r = await Sut().RunAsync();
        r!.PruneSkipped.Should().BeTrue();
        _log.Lines.Single(l => l.StartsWith("pruneSkipped:")).Should().Contain("公共桌面读取失败（路径为空）");
    }

    [Fact]
    public async Task Database_failure_is_logged_and_does_not_throw()
    {
        _db.CrashAtStep = 1;
        OnDisk(User, "a.txt");
        var r = await Sut().RunAsync();
        r.Should().BeNull();
        _log.Lines.Should().ContainSingle().Which.Should().StartWith("failed:");
    }

    [Fact]
    public async Task Database_error_logs_reconcile_failed_and_never_shows_collected_tray_notice()
    {
        OnDisk(User, "a.txt", "b.txt");
        _db.CrashAtStep = 4; // 事务已开始、写了部分行之后数据库出错 → 回滚

        var r = await Sut().RunAsync();

        r.Should().BeNull();
        _log.Lines.Should().ContainSingle(l => l.StartsWith("failed:"));
        _log.Lines.Should().NotContain(l => l.StartsWith("completed"));
        _tray.Notices.Should().BeEmpty("对账失败时不提示「已收纳」");
        _items.Rows.Should().BeEmpty("事务回滚，一行都没写");
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await FluentActions.Awaiting(() => Sut().RunAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Natural_comparer_edge_cases()
    {
        var c = NaturalNameComparer.Instance;
        c.Compare(null, null).Should().Be(0);
        c.Compare(null, "a").Should().BeNegative();
        c.Compare("a", null).Should().BePositive();
        c.Compare("a01", "a1").Should().NotBe(0, "相同数值时按序数区分，保证稳定");
        c.Compare("a", "ab").Should().BeNegative();
        c.Compare("x9", "x10").Should().BeNegative();
        c.Compare("B", "a").Should().BePositive();
        FluentActions.Awaiting(() => new DesktopReconciler(_db, _fences, _items, _log).ReconcileAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    private sealed class Shell : IShellNotifications
    {
        public event EventHandler? TaskbarCreated;
        public int Subscribers => TaskbarCreated?.GetInvocationList().Length ?? 0;
        public void Raise() => TaskbarCreated?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Throwing(Exception ex) : IStartupReconcile
    {
        public int Calls;
        public Task<ReconcileResult?> RunAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromException<ReconcileResult?>(ex);
        }
    }

    [Fact]
    public async Task Rows_under_an_old_desktop_path_are_not_pruned_and_skip_count_is_logged()
    {
        const string old = @"D:\OldProfile\Desktop";
        Existing(_other, old + @"\a.txt", 0);
        Existing(_other, old + @"\b.txt", 1);
        Existing(_staging, old + @"\c.lnk", 0);
        Existing(_staging, User + @"\gone.txt", 1);
        OnDisk(User, "new.txt");

        var r = await Sut().RunAsync();

        r!.Pruned.Should().Be(1, "只删当前用户桌面下已不存在的 gone.txt");
        _items.Rows.Values.Count(i => i.FilePath.StartsWith(old, StringComparison.Ordinal)).Should().Be(3, "旧桌面路径下的 3 行全部保留");
        _log.Lines.Should().ContainSingle(l => l.StartsWith("pruneSkipped:")).Which.Should().Be("pruneSkipped:3 行不在当前桌面目录下，未删除");
        _log.Lines.Should().ContainSingle(l => l.StartsWith("pruned:")).Which.Should().Be("pruned:gone.txt");
    }

    [Fact]
    public async Task Parent_directory_match_is_case_insensitive_and_ignores_subfolders()
    {
        Existing(_other, User.ToUpperInvariant() + @"\gone.txt", 0);
        Existing(_other, User + @"\sub\deep.txt", 1);
        OnDisk(User, "kept.txt");

        var r = await Sut().RunAsync();

        r!.Pruned.Should().Be(1);
        _items.Rows.Values.Select(i => i.FilePath).Should().Contain(User + @"\sub\deep.txt");
        _log.Lines.Should().Contain("pruneSkipped:1 行不在当前桌面目录下，未删除");
    }

    [Fact]
    public async Task TaskbarCreated_reruns_reconcile_once_without_duplicating_rows()
    {
        OnDisk(User, "a.txt", "b.txt");
        var shell = new Shell();
        var refreshed = 0;
        await Sut().RunAsync(); // 启动时一次
        using var sut = new TaskbarCreatedReconcile(shell, Sut(), () => { refreshed++; return Task.CompletedTask; }, _log);

        await sut.HandleAsync();

        _items.Rows.Should().HaveCount(2, "重跑不重复收");
        _log.Lines.Count(l => l.StartsWith("completed")).Should().Be(2);
        _log.Lines.Last().Should().Be("completed found=2 written=0 pruned=0 skipped=False skippedInvalid=0");
        refreshed.Should().Be(1);
        _tray.Notices.Should().ContainSingle("第二次写入 0 行，不再提示");
        sut.Dispose();
        shell.Subscribers.Should().Be(0);
    }

    [Fact]
    public async Task TaskbarCreated_event_triggers_reconcile_and_read_only_mode_skips()
    {
        OnDisk(User, "a.txt");
        _mode.EnterReadOnly("恢复失败");
        var shell = new Shell();
        var done = new TaskCompletionSource();
        using var sut = new TaskbarCreatedReconcile(shell, Sut(), () => { done.SetResult(); return Task.CompletedTask; }, _log);

        shell.Raise();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _log.Lines.Should().Equal("skipped:恢复失败");
        _items.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task TaskbarCreated_overlapping_signal_runs_again_once_after_current_run()
    {
        OnDisk(User, "a.txt");
        var shell = new Shell();
        var gate = new TaskCompletionSource();
        var refreshes = 0;
        using var sut = new TaskbarCreatedReconcile(shell, Sut(), async () => { if (++refreshes == 1) await gate.Task; }, _log);

        var first = sut.HandleAsync();
        await sut.HandleAsync(); // 正在跑：只记待补跑
        await sut.HandleAsync();
        gate.SetResult();
        await first;

        refreshes.Should().Be(2, "跑完后只补跑一次，不并发");
        _items.Rows.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Throwing_reconcile_on_TaskbarCreated_does_not_propagate(bool cancelled)
    {
        Exception ex = cancelled ? new OperationCanceledException() : new InvalidOperationException("boom");
        var shell = new Shell();
        var reconcile = new Throwing(ex);
        using var sut = new TaskbarCreatedReconcile(shell, reconcile, () => Task.CompletedTask, _log);

        await FluentActions.Awaiting(sut.HandleAsync).Should().NotThrowAsync();
        shell.Raise(); // async void 处理器：异常若外抛会打到同步上下文/进程
        await Task.Delay(50);

        reconcile.Calls.Should().Be(2);
        _log.Lines.Should().HaveCount(2).And.OnlyContain(l => l.StartsWith(cancelled ? "skipped:TaskbarCreated 对账已取消" : "failed:TaskbarCreated InvalidOperationException: boom"));
    }

    [Fact]
    public void TaskbarCreated_handler_rejects_null_dependencies()
    {
        var shell = new Shell();
        FluentActions.Invoking(() => new TaskbarCreatedReconcile(null!, Sut(), () => Task.CompletedTask, _log)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new TaskbarCreatedReconcile(shell, null!, () => Task.CompletedTask, _log)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new TaskbarCreatedReconcile(shell, Sut(), null!, _log)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new TaskbarCreatedReconcile(shell, Sut(), () => Task.CompletedTask, null!)).Should().Throw<ArgumentNullException>();
    }
}

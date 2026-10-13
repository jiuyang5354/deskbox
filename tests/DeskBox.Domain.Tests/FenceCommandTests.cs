using DeskBox.Application.Fences;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Application.Startup;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;
using DeskBox.Domain.Geometry;
using NSubstitute;

namespace DeskBox.Domain.Tests;

/// <summary>fence_item 内存仓储：写入经 FakeDb 工作单元暂存，提交才生效。file_path 不区分大小写唯一。</summary>
internal sealed class InMemoryFenceItems : IFenceItemStore
{
    public Dictionary<Guid, FenceItem> Rows { get; } = [];
    public int Writes { get; private set; }

    public Task<FenceItem?> GetAsync(IUnitOfWork uow, Guid id, CancellationToken ct = default)
    {
        FakeDb.Of(uow).Read();
        return Task.FromResult(Rows.TryGetValue(id, out var i) ? Clone(i) : null);
    }

    public Task<IReadOnlyList<FenceItem>> ListByFenceAsync(IUnitOfWork uow, Guid fenceId, CancellationToken ct = default)
    {
        FakeDb.Of(uow).Read();
        return Task.FromResult<IReadOnlyList<FenceItem>>(Rows.Values.Where(i => i.FenceId == fenceId).OrderBy(i => i.SortOrder).Select(Clone).ToList());
    }

    public Task UpsertAsync(IUnitOfWork uow, FenceItem item, CancellationToken ct = default)
    {
        if (Rows.Values.Any(r => r.Id != item.Id && string.Equals(r.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("UNIQUE fence_item.file_path");
        var copy = Clone(item);
        FakeDb.Of(uow).Stage(() => { Rows[copy.Id] = copy; Writes++; });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IUnitOfWork uow, Guid id, CancellationToken ct = default)
    {
        FakeDb.Of(uow).Stage(() => { Rows.Remove(id); Writes++; });
        return Task.CompletedTask;
    }

    private static FenceItem Clone(FenceItem i) => new(i.Id, i.FenceId, i.FilePath, i.FileId, i.IsPublicDesktop, i.SortOrder);
}

public class FenceCommandTests
{
    private const string FileId = "1a2b3c4d:000000000000000000000000000000aa";
    private const string Src = @"C:\Users\u\Desktop\a.txt";
    private const string Vault = @"C:\Users\u\DeskBox\Vault";
    private static readonly MonitorInfo Primary = new("M1", new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040), 96, true);
    private static readonly MonitorInfo Hi = new("M2", new ScreenRect(1920, 0, 2880, 1620), new ScreenRect(1920, 0, 2880, 1560), 144, false);

    private readonly FakeDb _db = new();
    private readonly InMemoryFences _fences = new();
    private readonly InMemoryFenceItems _items = new();
    private readonly OperatingMode _mode = new();
    private readonly INativeFileMove _native = Substitute.For<INativeFileMove>();
    private readonly InMemoryJournal _journal = new();
    private readonly Fence _a = new(Guid.NewGuid(), "A", "M1", 10, 10, 200, 200);
    private readonly Fence _b = new(Guid.NewGuid(), "B", "M1", 400, 10, 200, 200);
    private readonly Fence _staging = new(Guid.NewGuid(), "暂存", "M1", 700, 10, 200, 200, isStaging: true);
    private readonly FenceItem _item;
    private readonly FenceItem _public;
    private readonly FenceCommandHandler _sut;

    public FenceCommandTests()
    {
        foreach (var f in new[] { _a, _b, _staging }) _fences.Rows[f.Id] = f;
        _item = new FenceItem(Guid.NewGuid(), _a.Id, Src, FileId);
        _public = new FenceItem(Guid.NewGuid(), _a.Id, @"C:\Users\Public\Desktop\p.lnk", "1A2B3C4D:000000000000000000000000000000BB", isPublicDesktop: true, sortOrder: 1);
        _items.Rows[_item.Id] = _item;
        _items.Rows[_public.Id] = _public;

        var monitors = Substitute.For<IMonitorService>();
        monitors.GetMonitors().Returns([Primary, Hi]);
        var probe = Substitute.For<IFileSystemProbe>();
        probe.GetAttributes(Src).Returns(0x20u);
        var folders = Substitute.For<IKnownFolders>();
        folders.PublicDesktop.Returns(@"C:\Users\Public\Desktop");
        folders.UserDesktop.Returns(@"C:\Users\u\Desktop");
        var identity = Substitute.For<IFileIdentity>();
        identity.TryGetFileId(Src).Returns(FileId);
        // 真实的 JournaledMove → GatedFileMover → RenamingFileMover → INativeFileMove（即 MoveFileExW 的端口）
        var mover = new GatedFileMover(new RenamingFileMover(_native, probe, folders), _mode);
        var journaled = new JournaledMove(_db, mover, _journal, identity, probe, Substitute.For<IRecoveryLog>(), TimeProvider.System);
        _sut = new FenceCommandHandler(_db, _fences, _items, monitors, new JournaledVaultMover(journaled, Vault), _mode);
    }

    private void ReadOnly() => _mode.EnterReadOnly("恢复失败");

    // ---------- 改归属：一个事务，只改 fence_item，不调 MoveFileExW ----------
    [Fact]
    public async Task Reassign_changes_only_fence_item_in_one_transaction_and_never_calls_MoveFileExW()
    {
        var r = await _sut.ExecuteAsync(new ReassignItemCommand(_item.Id, _b.Id));
        r.Should().Be(CommandResult.Ok);
        _items.Rows[_item.Id].FenceId.Should().Be(_b.Id);
        _items.Rows[_item.Id].FilePath.Should().Be(Src, "桌面上的真实路径不变");
        _db.Commits.Should().Be(1, "读、算排序、写在同一个事务里");
        _native.DidNotReceiveWithAnyArgs().Move(default!, default!);
        _journal.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Reassign_appends_after_existing_items_and_same_fence_is_noop()
    {
        await _sut.ExecuteAsync(new ReassignItemCommand(_public.Id, _b.Id));
        await _sut.ExecuteAsync(new ReassignItemCommand(_item.Id, _b.Id));
        _items.Rows[_item.Id].SortOrder.Should().Be(1, "排在目标方框已有项之后");
        var writes = _items.Writes;
        (await _sut.ExecuteAsync(new ReassignItemCommand(_item.Id, _b.Id))).Should().Be(CommandResult.Ok);
        _items.Writes.Should().Be(writes, "拖回原方框不写库");
        _native.DidNotReceiveWithAnyArgs().Move(default!, default!);
    }

    [Fact]
    public async Task Reassign_public_desktop_item_between_fences_is_allowed()
    {
        (await _sut.ExecuteAsync(new ReassignItemCommand(_public.Id, _b.Id))).IsOk.Should().BeTrue();
        _native.DidNotReceiveWithAnyArgs().Move(default!, default!);
    }

    [Fact]
    public async Task Reassign_to_missing_fence_or_missing_item_is_not_found()
    {
        (await _sut.ExecuteAsync(new ReassignItemCommand(_item.Id, Guid.NewGuid()))).Status.Should().Be(CommandStatus.NotFound);
        (await _sut.ExecuteAsync(new ReassignItemCommand(Guid.NewGuid(), _b.Id))).Status.Should().Be(CommandStatus.NotFound);
        _items.Rows[_item.Id].FenceId.Should().Be(_a.Id);
    }

    [Fact]
    public async Task Remove_deletes_only_the_mapping_and_never_calls_MoveFileExW()
    {
        (await _sut.ExecuteAsync(new RemoveItemCommand(_item.Id))).IsOk.Should().BeTrue();
        _items.Rows.Should().NotContainKey(_item.Id);
        _native.DidNotReceiveWithAnyArgs().Move(default!, default!);
        (await _sut.ExecuteAsync(new RemoveItemCommand(_item.Id))).Status.Should().Be(CommandStatus.NotFound);
    }

    // ---------- 只读门禁 ----------
    [Fact]
    public async Task Read_only_rejects_ownership_and_file_commands_before_touching_the_db()
    {
        ReadOnly();
        var steps = _db.Steps;
        (await _sut.ExecuteAsync(new ReassignItemCommand(_item.Id, _b.Id))).Status.Should().Be(CommandStatus.ReadOnly);
        (await _sut.ExecuteAsync(new RemoveItemCommand(_item.Id))).Status.Should().Be(CommandStatus.ReadOnly);
        var vault = await _sut.ExecuteAsync(new VaultItemCommand(_item.Id));
        vault.Status.Should().Be(CommandStatus.ReadOnly);
        vault.Reason.Should().Contain("只读").And.Contain("恢复失败");
        (await _sut.ExecuteAsync(new DeleteFenceCommand(_a.Id))).Status.Should().Be(CommandStatus.ReadOnly);
        _db.Steps.Should().Be(steps, "门禁在任何读写之前");
        _items.Rows[_item.Id].FenceId.Should().Be(_a.Id);
        _fences.Rows.Should().ContainKey(_a.Id);
        _native.DidNotReceiveWithAnyArgs().Move(default!, default!);
    }

    [Fact]
    public async Task Read_only_still_allows_layout_rename_opacity_and_collapse()
    {
        ReadOnly();
        (await _sut.ExecuteAsync(new MoveFenceCommand(_a.Id, new ScreenRect(50, 60, 200, 200)))).IsOk.Should().BeTrue();
        (await _sut.ExecuteAsync(new ResizeFenceCommand(_a.Id, new ScreenRect(50, 60, 300, 250)))).IsOk.Should().BeTrue();
        (await _sut.ExecuteAsync(new RenameFenceCommand(_a.Id, "工作"))).IsOk.Should().BeTrue();
        (await _sut.ExecuteAsync(new SetFenceOpacityCommand(_a.Id, 40))).IsOk.Should().BeTrue();
        (await _sut.ExecuteAsync(new ToggleCollapseCommand(_a.Id))).IsOk.Should().BeTrue();
        var f = _fences.Rows[_a.Id];
        (f.X, f.Y, f.Width, f.Height, f.Title, f.Opacity, f.IsCollapsed).Should().Be((50, 60, 300, 250, "工作", 40, true));
    }

    // ---------- 暂存方框 / 公共桌面 ----------
    [Fact]
    public async Task Delete_fence_happy_path_removes_row()
    {
        (await _sut.ExecuteAsync(new DeleteFenceCommand(_b.Id))).IsOk.Should().BeTrue();
        _fences.Rows.Should().NotContainKey(_b.Id);
        (await _sut.ExecuteAsync(new DeleteFenceCommand(_b.Id))).Status.Should().Be(CommandStatus.NotFound);
    }

    [Fact]
    public async Task Delete_staging_fence_is_rejected_by_domain()
    {
        var r = await _sut.ExecuteAsync(new DeleteFenceCommand(_staging.Id));
        r.Should().Be(new CommandResult(CommandStatus.Rejected, FenceUiText.StagingCannotDelete));
        _fences.Rows.Should().ContainKey(_staging.Id);
    }

    [Fact]
    public async Task Vault_public_desktop_item_is_rejected_without_moving()
    {
        var r = await _sut.ExecuteAsync(new VaultItemCommand(_public.Id));
        r.Should().Be(new CommandResult(CommandStatus.Rejected, FenceUiText.PublicDesktopCannotVault));
        _native.DidNotReceiveWithAnyArgs().Move(default!, default!);
        _journal.Entries.Should().BeEmpty();
        _items.Rows.Should().ContainKey(_public.Id);
    }

    [Fact]
    public async Task Vault_moves_through_JournaledMove_and_drops_mapping()
    {
        _native.Move(Src, Vault + @"\a.txt").Returns(Win32Errors.Success);
        var r = await _sut.ExecuteAsync(new VaultItemCommand(_item.Id));
        r.IsOk.Should().BeTrue();
        _native.Received(1).Move(Src, Vault + @"\a.txt");
        _journal.Entries.Values.Single().Status.Should().Be(JournalStatus.Committed);
        _items.Rows.Should().NotContainKey(_item.Id);
    }

    [Fact]
    public async Task Vault_skipped_move_keeps_mapping_and_reports_reason()
    {
        _native.Move(Src, Vault + @"\a.txt").Returns(Win32Errors.SharingViolation);
        var r = await _sut.ExecuteAsync(new VaultItemCommand(_item.Id));
        r.Status.Should().Be(CommandStatus.Rejected);
        r.Reason.Should().NotBeNullOrWhiteSpace();
        _items.Rows.Should().ContainKey(_item.Id);
        (await _sut.ExecuteAsync(new VaultItemCommand(Guid.NewGuid()))).Status.Should().Be(CommandStatus.NotFound);
    }

    [Fact]
    public async Task Vault_mover_reporting_read_only_maps_to_read_only()
    {
        var vault = Substitute.For<IVaultItemMover>();
        vault.MoveToVaultAsync(default!, default).ReturnsForAnyArgs<Task<MoveResult>>(_ => throw new ReadOnlyModeException("x"));
        var sut = new FenceCommandHandler(_db, _fences, _items, Substitute.For<IMonitorService>(), vault, _mode);
        (await sut.ExecuteAsync(new VaultItemCommand(_item.Id))).Status.Should().Be(CommandStatus.ReadOnly);
        _items.Rows.Should().ContainKey(_item.Id);
    }

    // ---------- 布局：松手一次落库，工作区相对 DIP，最小 120，夹回可见区域 ----------
    [Fact]
    public async Task Move_on_high_dpi_monitor_stores_work_area_relative_dip_and_keeps_size()
    {
        (await _sut.ExecuteAsync(new MoveFenceCommand(_a.Id, new ScreenRect(1920 + 300, 150, 999, 999)))).IsOk.Should().BeTrue();
        var f = _fences.Rows[_a.Id];
        (f.MonitorId, f.X, f.Y, f.Width, f.Height, f.Dpi).Should().Be(("M2", 200, 100, 200, 200, 144));
    }

    [Theory]
    [InlineData(-500, -500, 0, 0)]
    [InlineData(5000, 5000, 1720, 840)]
    public async Task Move_outside_work_area_is_clamped_back(int x, int y, int ex, int ey)
    {
        await _sut.ExecuteAsync(new MoveFenceCommand(_a.Id, new ScreenRect(x, y, 200, 200)));
        var f = _fences.Rows[_a.Id];
        (f.X, f.Y).Should().Be((ex, ey));
    }

    [Fact]
    public async Task Resize_below_minimum_is_clamped_to_120_dip_and_oversize_to_work_area()
    {
        await _sut.ExecuteAsync(new ResizeFenceCommand(_a.Id, new ScreenRect(10, 10, 30, 40)));
        (_fences.Rows[_a.Id].Width, _fences.Rows[_a.Id].Height).Should().Be((120, 120));
        await _sut.ExecuteAsync(new ResizeFenceCommand(_a.Id, new ScreenRect(0, 0, 4000, 4000)));
        (_fences.Rows[_a.Id].Width, _fences.Rows[_a.Id].Height).Should().Be((1920, 1040));
    }

    [Fact]
    public async Task Invalid_rename_or_opacity_is_rejected_and_not_saved()
    {
        (await _sut.ExecuteAsync(new RenameFenceCommand(_a.Id, "  "))).Status.Should().Be(CommandStatus.Rejected);
        (await _sut.ExecuteAsync(new SetFenceOpacityCommand(_a.Id, 5))).Status.Should().Be(CommandStatus.Rejected);
        (_fences.Rows[_a.Id].Title, _fences.Rows[_a.Id].Opacity).Should().Be(("A", 85));
        (await _sut.ExecuteAsync(new RenameFenceCommand(Guid.NewGuid(), "x"))).Status.Should().Be(CommandStatus.NotFound);
    }

    [Fact]
    public async Task Toggle_collapse_twice_returns_to_expanded()
    {
        await _sut.ExecuteAsync(new ToggleCollapseCommand(_a.Id));
        _fences.Rows[_a.Id].IsCollapsed.Should().BeTrue();
        await _sut.ExecuteAsync(new ToggleCollapseCommand(_a.Id));
        _fences.Rows[_a.Id].IsCollapsed.Should().BeFalse();
    }

    [Fact]
    public async Task Null_commands_throw()
    {
        await FluentActions.Awaiting(() => _sut.ExecuteAsync((MoveFenceCommand)null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _sut.ExecuteAsync((ResizeFenceCommand)null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _sut.ExecuteAsync((RenameFenceCommand)null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _sut.ExecuteAsync((SetFenceOpacityCommand)null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _sut.ExecuteAsync((ToggleCollapseCommand)null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _sut.ExecuteAsync((DeleteFenceCommand)null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _sut.ExecuteAsync((ReassignItemCommand)null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _sut.ExecuteAsync((RemoveItemCommand)null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _sut.ExecuteAsync((VaultItemCommand)null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Journaled_vault_mover_targets_vault_folder_with_same_name()
    {
        _native.Move(Src, Vault + @"\a.txt").Returns(Win32Errors.Success);
        var identity = Substitute.For<IFileIdentity>();
        identity.TryGetFileId(Src).Returns(FileId);
        var jobId = Guid.NewGuid();
        var probe = Substitute.For<IFileSystemProbe>();
        var folders = Substitute.For<IKnownFolders>();
        folders.UserDesktop.Returns(@"C:\Users\u\Desktop");
        folders.PublicDesktop.Returns(@"C:\Users\Public\Desktop");
        var sut = new JournaledVaultMover(new JournaledMove(_db, new RenamingFileMover(_native, probe, folders), _journal, identity, probe,
            Substitute.For<IRecoveryLog>(), TimeProvider.System), Vault + @"\", () => jobId);
        (await sut.MoveToVaultAsync(Src)).Should().BeOfType<MoveResult.Moved>();
        _journal.Entries.Values.Single().JobId.Should().Be(jobId);
        await FluentActions.Awaiting(() => sut.MoveToVaultAsync(" ")).Should().ThrowAsync<ArgumentException>();
    }
}

public class FenceMenuTests
{
    private readonly OperatingMode _mode = new();
    private readonly Fence _a = new(Guid.NewGuid(), "A", "M1", 0, 0, 200, 200);
    private readonly Fence _b = new(Guid.NewGuid(), "B", "M1", 0, 0, 200, 200);
    private readonly Fence _staging = new(Guid.NewGuid(), "暂存", "M1", 0, 0, 200, 200, isStaging: true);

    private FenceItem Item(bool pub = false) =>
        new(Guid.NewGuid(), _a.Id, @"C:\d\x.txt", "1A2B3C4D:000000000000000000000000000000AA", pub);

    [Fact]
    public void Fence_menu_has_the_four_approved_items_and_toggles_collapse_label()
    {
        FenceMenus.ForFence(_a, _mode).Select(e => e.Action).Should()
            .Equal(FenceMenuAction.Rename, FenceMenuAction.Collapse, FenceMenuAction.Delete, FenceMenuAction.Opacity);
        _a.Collapse();
        FenceMenus.ForFence(_a, _mode)[1].Action.Should().Be(FenceMenuAction.Expand);
        FenceMenus.ForFence(_a, _mode).Should().OnlyContain(e => e.IsEnabled);
    }

    [Fact]
    public void Staging_delete_is_disabled_with_reason()
    {
        var del = FenceMenus.ForFence(_staging, _mode).Single(e => e.Action == FenceMenuAction.Delete);
        (del.IsEnabled, del.DisabledReason).Should().Be((false, FenceUiText.StagingCannotDelete));
    }

    [Fact]
    public void Item_menu_lists_other_fences_and_disables_vault_for_public_desktop()
    {
        var menu = FenceMenus.ForItem(Item(pub: true), [_a, _b, _staging], _mode);
        menu.Select(e => e.Action).Should().Equal(FenceMenuAction.Open, FenceMenuAction.OpenLocation,
            FenceMenuAction.MoveToFence, FenceMenuAction.MoveToFence, FenceMenuAction.MoveToVault, FenceMenuAction.RemoveFromFence);
        menu.Where(e => e.Action == FenceMenuAction.MoveToFence).Select(e => e.TargetTitle).Should().Equal("B", "暂存");
        var vault = menu.Single(e => e.Action == FenceMenuAction.MoveToVault);
        (vault.IsEnabled, vault.DisabledReason).Should().Be((false, FenceUiText.PublicDesktopCannotVault));
        FenceMenus.ForItem(Item(), [_a, _b], _mode).Single(e => e.Action == FenceMenuAction.MoveToVault).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Single_fence_disables_move_to_other_fence()
    {
        var move = FenceMenus.ForItem(Item(), [_a], _mode).Single(e => e.Action == FenceMenuAction.MoveToFence);
        (move.IsEnabled, move.DisabledReason).Should().Be((false, FenceUiText.NoOtherFence));
    }

    [Fact]
    public void Read_only_disables_ownership_and_file_items_with_reason_but_keeps_open_and_layout()
    {
        _mode.EnterReadOnly("恢复失败");
        var item = FenceMenus.ForItem(Item(), [_a, _b], _mode);
        item.Where(e => e.Action is FenceMenuAction.Open or FenceMenuAction.OpenLocation).Should().OnlyContain(e => e.IsEnabled);
        item.Where(e => e.Action is FenceMenuAction.MoveToFence or FenceMenuAction.MoveToVault or FenceMenuAction.RemoveFromFence)
            .Should().OnlyContain(e => !e.IsEnabled && e.DisabledReason!.Contains("恢复失败"));
        var fence = FenceMenus.ForFence(_a, _mode);
        fence.Single(e => e.Action == FenceMenuAction.Delete).IsEnabled.Should().BeFalse();
        fence.Where(e => e.Action != FenceMenuAction.Delete).Should().OnlyContain(e => e.IsEnabled);
        FenceMenus.ForItem(Item(), [_a], _mode).Single(e => e.Action == FenceMenuAction.MoveToFence).DisabledReason.Should().Contain("只读");
    }

    [Fact]
    public void Labels_and_text_are_defined_for_every_action()
    {
        foreach (var a in Enum.GetValues<FenceMenuAction>()) FenceMenus.Label(a).Should().NotBeNullOrWhiteSpace();
        FluentActions.Invoking(() => FenceMenus.Label((FenceMenuAction)99)).Should().Throw<ArgumentOutOfRangeException>();
        FenceUiText.ReadOnly(null).Should().Contain("只读");
        FenceMenus.OpacityLevels.Should().Equal(40, 70, 100);
        FluentActions.Invoking(() => FenceMenus.ForFence(null!, _mode)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => FenceMenus.ForItem(null!, [], _mode)).Should().Throw<ArgumentNullException>();
    }
}

public class DragSessionTests
{
    private static readonly ScreenRect Orig = new(100, 100, 200, 200);

    [Fact]
    public void Fence_drag_moves_window_only_and_produces_one_command_on_release()
    {
        var applied = new List<ScreenRect>();
        var s = new FenceDragSession(Guid.NewGuid(), Orig, resize: false, applied.Add);
        s.Update(10, 0);
        s.Update(30, 40);
        applied.Should().Equal(Orig with { X = 110 }, Orig with { X = 130, Y = 140 });
        s.Complete().Should().Be(new MoveFenceCommand(s.FenceId, Orig with { X = 130, Y = 140 }));
        s.IsFinished.Should().BeTrue();
        s.Complete().Should().BeNull("只落库一次");
        s.Update(1, 1);
        applied.Should().HaveCount(2);
    }

    [Fact]
    public void Esc_cancel_restores_original_position_and_produces_no_command()
    {
        var applied = new List<ScreenRect>();
        var s = new FenceDragSession(Guid.NewGuid(), Orig, resize: false, applied.Add);
        s.Update(500, 500);
        s.Cancel();
        applied.Last().Should().Be(Orig);
        s.Current.Should().Be(Orig);
        s.Complete().Should().BeNull("Esc 取消后不写库");
        s.Cancel();
        applied.Should().HaveCount(2);
    }

    [Fact]
    public void Resize_drag_produces_resize_command_and_no_movement_produces_nothing()
    {
        var s = new FenceDragSession(Guid.NewGuid(), Orig, resize: true, _ => { });
        s.IsResize.Should().BeTrue();
        s.Update(-500, 20);
        s.Current.Should().Be(Orig with { Width = 1, Height = 220 });
        s.Complete().Should().BeOfType<ResizeFenceCommand>();
        new FenceDragSession(Guid.NewGuid(), Orig, false, _ => { }).Complete().Should().BeNull();
    }

    [Fact]
    public void Item_drag_highlights_only_other_fences_and_drop_reassigns()
    {
        var (item, src, dst) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var s = new ItemDragSession(item, src);
        s.Hover(src);
        s.IsDropTarget(src).Should().BeFalse("原方框不高亮");
        s.Hover(dst);
        s.IsDropTarget(dst).Should().BeTrue();
        s.Drop(dst).Should().Be(new ReassignItemCommand(item, dst));
        s.IsDropTarget(dst).Should().BeFalse();
        s.Drop(dst).Should().BeNull();
    }

    [Fact]
    public void Item_drop_outside_on_source_or_after_cancel_does_nothing()
    {
        var src = Guid.NewGuid();
        new ItemDragSession(Guid.NewGuid(), src).Drop(null).Should().BeNull();
        new ItemDragSession(Guid.NewGuid(), src).Drop(src).Should().BeNull();
        var s = new ItemDragSession(Guid.NewGuid(), src);
        s.Hover(Guid.NewGuid());
        s.Cancel();
        s.HoverFenceId.Should().BeNull();
        s.Hover(Guid.NewGuid());
        s.HoverFenceId.Should().BeNull();
        s.Drop(Guid.NewGuid()).Should().BeNull();
    }
}

public class InstanceActivationTests
{
    /// <summary>假界面线程：Post 只入队；Pump 时在"界面线程"上执行，期间 IsOnUiThread 为 true。</summary>
    private sealed class FakeDispatcher : IUiDispatcher
    {
        public Queue<Action> Queue { get; } = new();
        public bool IsOnUiThread { get; private set; }
        public void Post(Action action) => Queue.Enqueue(action);

        public void Pump()
        {
            IsOnUiThread = true;
            try { while (Queue.TryDequeue(out var a)) a(); }
            finally { IsOnUiThread = false; }
        }
    }

    private sealed class RecordingActivator(FakeDispatcher d) : IWindowActivator
    {
        public List<bool> CalledOnUiThread { get; } = [];
        public void RestoreActivateAndBringToFront() => CalledOnUiThread.Add(d.IsOnUiThread);
    }

    [Fact]
    public async Task Signal_from_background_thread_runs_restore_activate_front_on_ui_dispatcher()
    {
        var d = new FakeDispatcher();
        var a = new RecordingActivator(d);
        var sut = new InstanceActivation(d, a);
        await Task.Run(() => sut.OnSignal(null, EventArgs.Empty));
        a.CalledOnUiThread.Should().BeEmpty("信号线程上绝不直接操作窗口");
        d.Queue.Should().HaveCount(1);
        d.Pump();
        a.CalledOnUiThread.Should().Equal(true);
    }

    [Fact]
    public void Running_instance_wires_single_instance_activated_to_activation()
    {
        var d = new FakeDispatcher();
        var a = new RecordingActivator(d);
        var sut = new InstanceActivation(d, a);
        EventHandler? activated = null;
        activated += sut.OnSignal;
        activated(this, EventArgs.Empty);
        activated(this, EventArgs.Empty);
        d.Pump();
        a.CalledOnUiThread.Should().Equal(true, true);
    }

    [Fact]
    public async Task Second_process_signals_and_exits_without_touching_the_database()
    {
        var instance = Substitute.For<ISingleInstance>();
        instance.TryAcquire().Returns(false);
        var database = Substitute.For<IDatabaseBootstrapper>();
        var recovery = Substitute.For<IStartupRecovery>();
        var desktop = Substitute.For<IDesktopEnumerator>();
        var embedder = Substitute.For<IStartupEmbedder>();
        var db = Substitute.For<IUnitOfWorkFactory>();
        var sut = new StartupSequence(instance, database, recovery, desktop, embedder, Substitute.For<IStartupNotifier>(), new OperatingMode());

        var r = await sut.RunAsync();

        r.Outcome.Should().Be(StartupOutcome.SecondInstance);
        instance.Received(1).SignalRunningInstance();
        database.ReceivedCalls().Should().BeEmpty("新进程不开库、不迁移");
        recovery.ReceivedCalls().Should().BeEmpty();
        desktop.ReceivedCalls().Should().BeEmpty();
        embedder.ReceivedCalls().Should().BeEmpty();
        db.ReceivedCalls().Should().BeEmpty();
    }
}

public class FenceBoardTests
{
    [Fact]
    public async Task Load_returns_each_fence_with_its_items_in_one_transaction()
    {
        var db = new FakeDb();
        var fences = new InMemoryFences();
        var items = new InMemoryFenceItems();
        var a = new Fence(Guid.NewGuid(), "A", "m", 0, 0, 120, 120);
        var b = new Fence(Guid.NewGuid(), "B", "m", 0, 0, 120, 120);
        fences.Rows[a.Id] = a;
        fences.Rows[b.Id] = b;
        var i = new FenceItem(Guid.NewGuid(), b.Id, @"C:\d\x", "1A2B3C4D:000000000000000000000000000000AA");
        items.Rows[i.Id] = i;
        var r = await new FenceBoard(db, fences, items).LoadAsync();
        r.Single(s => s.Fence.Id == b.Id).Items.Single().Id.Should().Be(i.Id);
        r.Single(s => s.Fence.Id == a.Id).Items.Should().BeEmpty();
        db.Commits.Should().Be(1);
    }
}

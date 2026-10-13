using DeskBox.Domain;
using DeskBox.Domain.Entities;
using DeskBox.Domain.StateMachines;

namespace DeskBox.Domain.Tests;

public class FenceTests
{
    private static Fence Make(string title = "工作", int w = 300, int h = 200, int opacity = 85, int dpi = 144, bool staging = false) =>
        new(Guid.NewGuid(), title, "MON-1", 10, 20, w, h, dpi, opacity, staging);

    [Fact]
    public void Constructor_sets_all_fields()
    {
        var f = Make();
        f.Title.Should().Be("工作");
        (f.X, f.Y, f.Width, f.Height, f.Dpi, f.Opacity).Should().Be((10, 20, 300, 200, 144, 85));
        f.IsStaging.Should().BeFalse();
        f.State.Should().Be(FenceState.Expanded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123456789012345678901234567890123")]
    public void Invalid_title_throws(string title) =>
        FluentActions.Invoking(() => Make(title)).Should().Throw<DomainValidationException>().Which.Field.Should().Be("Title");

    [Fact]
    public void Title_of_32_chars_and_1_char_allowed()
    {
        Make(new string('a', 32)).Title.Should().HaveLength(32);
        Make("a").Title.Should().Be("a");
        Make("  x  ").Title.Should().Be("x");
    }

    [Theory]
    [InlineData(119, 120)]
    [InlineData(120, 119)]
    [InlineData(0, 0)]
    [InlineData(-1, 500)]
    public void Size_below_min_throws(int w, int h) =>
        FluentActions.Invoking(() => Make(w: w, h: h)).Should().Throw<DomainValidationException>().Which.Field.Should().Be("Size");

    [Theory]
    [InlineData(19)]
    [InlineData(101)]
    public void Opacity_out_of_range_throws(int o) =>
        FluentActions.Invoking(() => Make(opacity: o)).Should().Throw<DomainValidationException>().Which.Field.Should().Be("Opacity");

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    public void Opacity_bounds_allowed(int o) => Make(opacity: o).Opacity.Should().Be(o);

    [Theory]
    [InlineData(47)]
    [InlineData(961)]
    public void Dpi_out_of_range_throws(int dpi) =>
        FluentActions.Invoking(() => Make(dpi: dpi)).Should().Throw<DomainValidationException>().Which.Field.Should().Be("Dpi");

    [Fact]
    public void Empty_id_or_monitor_throws()
    {
        FluentActions.Invoking(() => new Fence(Guid.Empty, "a", "M", 0, 0, 200, 200)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new Fence(Guid.NewGuid(), "a", " ", 0, 0, 200, 200)).Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Mutators_validate_and_apply()
    {
        var f = Make();
        f.Rename("新名字");
        f.MoveTo("MON-2", -1920, 5, 96);
        f.Resize(120, 120);
        f.SetOpacity(50);
        f.SortOrder = 3;
        (f.Title, f.MonitorId, f.X, f.Y, f.Dpi, f.Width, f.Height, f.Opacity, f.SortOrder)
            .Should().Be(("新名字", "MON-2", -1920, 5, 96, 120, 120, 50, 3));
        FluentActions.Invoking(() => f.Resize(100, 300)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => f.SetOpacity(0)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => f.Rename("")).Should().Throw<DomainValidationException>();
        f.Width.Should().Be(120);
    }

    [Fact]
    public void Lifecycle_methods_drive_state()
    {
        var f = Make();
        f.Collapse(); f.State.Should().Be(FenceState.Collapsed);
        f.Expand(); f.State.Should().Be(FenceState.Expanded);
        f.Hide(); f.State.Should().Be(FenceState.Hidden);
        f.Show(); f.State.Should().Be(FenceState.Expanded);
        f.Delete(); f.State.Should().Be(FenceState.Deleted);
        FluentActions.Invoking(f.Show).Should().Throw<InvalidTransitionException>();
    }

    [Fact]
    public void Staging_fence_cannot_be_deleted()
    {
        var f = Make(staging: true);
        f.IsStaging.Should().BeTrue();
        FluentActions.Invoking(f.Delete).Should().Throw<DomainValidationException>().Which.Field.Should().Be("IsStaging");
        f.State.Should().Be(FenceState.Expanded);
    }
}

public class FenceItemTests
{
    private const string Fid = "1a2b3c4d:00112233445566778899aabbccddeeff";

    [Fact]
    public void Valid_item_normalizes_file_id()
    {
        var item = new FenceItem(Guid.NewGuid(), Guid.NewGuid(), @"C:\Users\a\Desktop\a.lnk", Fid);
        item.FileId.Should().Be(Fid.ToUpperInvariant());
        item.CanMoveToVault.Should().BeTrue();
    }

    [Fact]
    public void Public_desktop_item_cannot_go_to_vault() =>
        new FenceItem(Guid.NewGuid(), Guid.NewGuid(), @"C:\Users\Public\Desktop\x.lnk", Fid, isPublicDesktop: true)
            .CanMoveToVault.Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1a2b3c4d00112233445566778899aabbccddeeff0")]
    [InlineData("1a2b3c4z:00112233445566778899aabbccddeeff")]
    [InlineData("1a2b3c4d:0011")]
    public void Invalid_file_id_rejected(string? id)
    {
        FenceItem.IsValidFileId(id).Should().BeFalse();
        FluentActions.Invoking(() => new FenceItem(Guid.NewGuid(), Guid.NewGuid(), "p", id!)).Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Invalid_ctor_args_throw()
    {
        FluentActions.Invoking(() => new FenceItem(Guid.Empty, Guid.NewGuid(), "p", Fid)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new FenceItem(Guid.NewGuid(), Guid.Empty, "p", Fid)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new FenceItem(Guid.NewGuid(), Guid.NewGuid(), " ", Fid)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new FenceItem(Guid.NewGuid(), Guid.NewGuid(), "p", Fid, sortOrder: -1)).Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Move_and_rename_tracking()
    {
        var item = new FenceItem(Guid.NewGuid(), Guid.NewGuid(), "old", Fid);
        var target = Guid.NewGuid();
        item.MoveToFence(target, 5);
        item.UpdatePath("new");
        (item.FenceId, item.SortOrder, item.FilePath).Should().Be((target, 5, "new"));
        FluentActions.Invoking(() => item.MoveToFence(Guid.Empty, 0)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => item.MoveToFence(target, -1)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => item.UpdatePath("")).Should().Throw<DomainValidationException>();
    }
}

public class SnapshotTests
{
    [Fact]
    public void Ctor_copies_entries_and_validates()
    {
        var entries = new List<SnapshotEntry> { new("F", @"C:\a", null, 1, 2) };
        var s = new Snapshot(Guid.NewGuid(), SnapshotTrigger.BeforeRestore, DateTimeOffset.UnixEpoch, entries);
        entries.Clear();
        s.Entries.Should().ContainSingle().Which.FilePath.Should().Be(@"C:\a");
        s.Trigger.Should().Be(SnapshotTrigger.BeforeRestore);
        FluentActions.Invoking(() => new Snapshot(Guid.Empty, SnapshotTrigger.Manual, default, [])).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new Snapshot(Guid.NewGuid(), (SnapshotTrigger)99, default, [])).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new Snapshot(Guid.NewGuid(), SnapshotTrigger.Auto, default, null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Keeps_latest_ten()
    {
        var all = Enumerable.Range(0, 13)
            .Select(i => new Snapshot(Guid.NewGuid(), SnapshotTrigger.Manual, DateTimeOffset.UnixEpoch.AddMinutes(i), [])).ToList();
        var expired = Snapshot.SelectExpired(all);
        expired.Should().HaveCount(3);
        expired.Select(s => s.CreatedAt).Should().BeEquivalentTo(all.Take(3).Select(s => s.CreatedAt));
        Snapshot.SelectExpired(all.Take(10)).Should().BeEmpty();
    }
}

public class VaultRecordTests
{
    private const string Fid = "1a2b3c4d:00112233445566778899aabbccddeeff";

    [Fact]
    public void Restore_once_only()
    {
        var r = new VaultRecord(Guid.NewGuid(), @"C:\d\a.zip", @"C:\vault\a.zip", Fid, DateTimeOffset.UnixEpoch);
        r.IsRestored.Should().BeFalse();
        FluentActions.Invoking(() => r.MarkRestored(DateTimeOffset.UnixEpoch.AddSeconds(-1))).Should().Throw<DomainValidationException>();
        r.MarkRestored(DateTimeOffset.UnixEpoch.AddDays(1));
        r.IsRestored.Should().BeTrue();
        r.RestoredAt.Should().Be(DateTimeOffset.UnixEpoch.AddDays(1));
        FluentActions.Invoking(() => r.MarkRestored(DateTimeOffset.UnixEpoch.AddDays(2))).Should().Throw<InvalidTransitionException>();
    }

    [Theory]
    [InlineData("", "v", Fid)]
    [InlineData("o", " ", Fid)]
    [InlineData(@"C:\A", @"c:\a", Fid)]
    [InlineData("o", "v", "bad")]
    public void Invalid_args_throw(string o, string v, string f) =>
        FluentActions.Invoking(() => new VaultRecord(Guid.NewGuid(), o, v, f, default)).Should().Throw<DomainValidationException>();

    [Fact]
    public void Empty_id_throws() =>
        FluentActions.Invoking(() => new VaultRecord(Guid.Empty, "o", "v", Fid, default)).Should().Throw<DomainValidationException>();
}

public class JournalEntryTests
{
    private const string Fid = "1a2b3c4d:00112233445566778899aabbccddeeff";
    private static JournalEntry Make() => new(Guid.NewGuid(), Guid.NewGuid(), Fid, "s", "t", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Starts_pending_with_file_id_and_commits()
    {
        var e = Make();
        e.Status.Should().Be(JournalStatus.Pending);
        e.FileId.Should().Be(Fid.ToUpperInvariant());
        (e.TargetPath, e.Attempt).Should().Be(("t", 0));
        e.ActualTargetPath.Should().BeNull();
        FluentActions.Invoking(() => e.Commit(" ")).Should().Throw<DomainValidationException>();
        e.Status.Should().Be(JournalStatus.Pending);
        e.Commit("t (2)");
        e.Status.Should().Be(JournalStatus.Committed);
        e.ActualTargetPath.Should().Be("t (2)");
        FluentActions.Invoking(() => e.RollBack("x")).Should().Throw<InvalidTransitionException>().Which.From.Should().Be("Committed");
    }

    [Fact]
    public void Begin_attempt_updates_candidate_only_while_pending()
    {
        var e = Make();
        e.BeginAttempt("t");
        e.BeginAttempt("t (2)");
        (e.TargetPath, e.Attempt).Should().Be(("t (2)", 2));
        FluentActions.Invoking(() => e.BeginAttempt("")).Should().Throw<DomainValidationException>();
        e.Commit("t (2)");
        FluentActions.Invoking(() => e.BeginAttempt("t (3)")).Should().Throw<InvalidTransitionException>().Which.Trigger.Should().Be("BeginAttempt");
        e.TargetPath.Should().Be("t (2)");
    }

    [Fact]
    public void Rolled_back_is_terminal()
    {
        var e = Make();
        FluentActions.Invoking(() => e.RollBack("")).Should().Throw<DomainValidationException>();
        e.RollBack("Locked (Win32 32)");
        e.Status.Should().Be(JournalStatus.RolledBack);
        e.AbortReason.Should().Be("Locked (Win32 32)");
        FluentActions.Invoking(() => e.Commit("x")).Should().Throw<InvalidTransitionException>().Which.Trigger.Should().Be("Commit");
        FluentActions.Invoking(e.MarkLost).Should().Throw<InvalidTransitionException>();
    }

    [Fact]
    public void Lost_is_terminal()
    {
        var e = Make();
        e.MarkLost();
        e.Status.Should().Be(JournalStatus.Lost);
        FluentActions.Invoking(() => e.Commit("x")).Should().Throw<InvalidTransitionException>().Which.From.Should().Be("Lost");
    }

    [Fact]
    public void Invalid_args_throw()
    {
        FluentActions.Invoking(() => new JournalEntry(Guid.Empty, Guid.NewGuid(), Fid, "s", "t", default)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new JournalEntry(Guid.NewGuid(), Guid.Empty, Fid, "s", "t", default)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), "bad", "s", "t", default)).Should().Throw<DomainValidationException>().Which.Field.Should().Be("FileId");
        FluentActions.Invoking(() => new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), Fid, "", "t", default)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), Fid, "s", "", default)).Should().Throw<DomainValidationException>();
    }
}

public class LostFileNoticeTests
{
    private const string Fid = "1a2b3c4d:00112233445566778899aabbccddeeff";

    [Theory]
    [InlineData(@"C:\Users\u\Desktop\notes.txt", "notes.txt")]
    [InlineData("/d/照片😀.png", "照片😀.png")]
    [InlineData(@"C:\dir\", @"C:\dir\")]
    public void File_name_extracted(string path, string name) =>
        new LostFileNotice(Guid.NewGuid(), Fid, path, default).FileName.Should().Be(name);

    [Fact]
    public void Acknowledge_once()
    {
        var n = new LostFileNotice(Guid.NewGuid(), Fid, "a", DateTimeOffset.UnixEpoch);
        n.IsAcknowledged.Should().BeFalse();
        n.FileId.Should().Be(Fid.ToUpperInvariant());
        n.Acknowledge(DateTimeOffset.UnixEpoch.AddHours(1));
        (n.IsAcknowledged, n.AcknowledgedAt).Should().Be((true, DateTimeOffset.UnixEpoch.AddHours(1)));
        FluentActions.Invoking(() => n.Acknowledge(default)).Should().Throw<InvalidTransitionException>();
    }

    [Fact]
    public void Invalid_args()
    {
        FluentActions.Invoking(() => new LostFileNotice(Guid.Empty, Fid, "a", default)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new LostFileNotice(Guid.NewGuid(), "x", "a", default)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new LostFileNotice(Guid.NewGuid(), Fid, " ", default)).Should().Throw<DomainValidationException>();
    }
}

public class FenceUiDomainTests
{
    [Fact]
    public void Collapsed_flag_restores_state_and_toggle_flips_it()
    {
        var f = new Fence(Guid.NewGuid(), "A", "m", 0, 0, 120, 120, isCollapsed: true);
        f.IsCollapsed.Should().BeTrue();
        f.ToggleCollapse();
        f.IsCollapsed.Should().BeFalse();
        f.ToggleCollapse();
        f.State.Should().Be(DeskBox.Domain.StateMachines.FenceState.Collapsed);
    }

    [Fact]
    public void Staging_and_deleted_fences_cannot_be_deleted()
    {
        new Fence(Guid.NewGuid(), "暂存", "m", 0, 0, 120, 120, isStaging: true).CanDelete.Should().BeFalse();
        var f = new Fence(Guid.NewGuid(), "A", "m", 0, 0, 120, 120);
        f.CanDelete.Should().BeTrue();
        f.Delete();
        f.CanDelete.Should().BeFalse();
    }

    [Fact]
    public void Public_desktop_item_cannot_move_to_vault()
    {
        const string id = "1A2B3C4D:000000000000000000000000000000AA";
        FluentActions.Invoking(() => new FenceItem(Guid.NewGuid(), Guid.NewGuid(), @"C:\p", id, isPublicDesktop: true).EnsureCanMoveToVault())
            .Should().Throw<DomainValidationException>().Which.Field.Should().Be("IsPublicDesktop");
        FluentActions.Invoking(() => new FenceItem(Guid.NewGuid(), Guid.NewGuid(), @"C:\p", id).EnsureCanMoveToVault()).Should().NotThrow();
    }

    [Theory]
    [InlineData(-10, -10, 200, 200, 0, 0, 200, 200)]
    [InlineData(1900, 1000, 200, 200, 1720, 840, 200, 200)]
    [InlineData(10, 10, 50, 60, 10, 10, 120, 120)]
    [InlineData(0, 0, 5000, 5000, 0, 0, 1920, 1040)]
    public void ClampStored_keeps_min_size_and_inside_work_area(int x, int y, int w, int h, int ex, int ey, int ew, int eh) =>
        DeskBox.Domain.Geometry.DesktopGeometry.ClampStored(new(x, y, w, h), 1920, 1040, 120).Should().Be(new DeskBox.Domain.Geometry.ScreenRect(ex, ey, ew, eh));

    [Fact]
    public void ClampStored_on_tiny_work_area_still_keeps_min_size() =>
        DeskBox.Domain.Geometry.DesktopGeometry.ClampStored(new(50, 50, 300, 300), 100, 100, 120).Should().Be(new DeskBox.Domain.Geometry.ScreenRect(0, 0, 120, 120));
}

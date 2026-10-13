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
        f.MonitorLost(); f.State.Should().Be(FenceState.Orphaned);
        f.MonitorRestored(); f.State.Should().Be(FenceState.Expanded);
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
    private static JournalEntry Make() => new(Guid.NewGuid(), Guid.NewGuid(), "s", "t", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Starts_pending_and_commits()
    {
        var e = Make();
        e.Status.Should().Be(JournalStatus.Pending);
        e.ActualTargetPath.Should().BeNull();
        FluentActions.Invoking(() => e.Commit(" ")).Should().Throw<DomainValidationException>();
        e.Status.Should().Be(JournalStatus.Pending);
        e.Commit("t (2)");
        e.Status.Should().Be(JournalStatus.Committed);
        e.ActualTargetPath.Should().Be("t (2)");
        FluentActions.Invoking(e.RollBack).Should().Throw<InvalidTransitionException>().Which.From.Should().Be("Committed");
    }

    [Fact]
    public void Rolled_back_is_terminal()
    {
        var e = Make();
        e.RollBack();
        e.Status.Should().Be(JournalStatus.RolledBack);
        FluentActions.Invoking(() => e.Commit("x")).Should().Throw<InvalidTransitionException>().Which.Trigger.Should().Be("Commit");
    }

    [Fact]
    public void Invalid_args_throw()
    {
        FluentActions.Invoking(() => new JournalEntry(Guid.Empty, Guid.NewGuid(), "s", "t", default)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new JournalEntry(Guid.NewGuid(), Guid.Empty, "s", "t", default)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), "", "t", default)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => new JournalEntry(Guid.NewGuid(), Guid.NewGuid(), "s", "", default)).Should().Throw<DomainValidationException>();
    }
}

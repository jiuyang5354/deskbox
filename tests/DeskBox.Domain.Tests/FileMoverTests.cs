using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;
using NSubstitute;

namespace DeskBox.Domain.Tests;

/// <summary>内存文件系统，模拟 MoveFileExW(src, dst, 0) 的不替换语义。</summary>
internal sealed class FakeFs : INativeFileMove
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Attempts { get; } = [];
    public Action<string>? BeforeAttempt { get; set; }

    public int Move(string src, string dst)
    {
        Attempts.Add(dst);
        BeforeAttempt?.Invoke(dst);
        if (!Files.ContainsKey(src)) return Win32Errors.FileNotFound;
        if (Locked.Contains(src)) return Win32Errors.SharingViolation;
        if (Files.ContainsKey(dst)) return Win32Errors.AlreadyExists;
        Files[dst] = Files[src];
        Files.Remove(src);
        return Win32Errors.Success;
    }
}

public class RenamingFileMoverTests
{
    private const string Src = "/desk/notes.txt";
    private const string Dst = "/vault/notes.txt";
    private readonly FakeFs _fs = new();
    private RenamingFileMover Mover => new(_fs, _ => false);

    public RenamingFileMoverTests() => _fs.Files[Src] = "new";

    [Fact]
    public async Task Moves_without_rename_when_free()
    {
        var r = await Mover.MoveAsync(Src, Dst);
        r.Should().Be(new MoveResult.Moved(Src, Dst, Renamed: false));
        _fs.Files[Dst].Should().Be("new");
    }

    [Fact]
    public async Task Name_clash_saves_as_n2_and_leaves_original_untouched()
    {
        _fs.Files[Dst] = "original";
        var r = await Mover.MoveAsync(Src, Dst);
        r.Should().Be(new MoveResult.Moved(Src, "/vault/notes (2).txt", Renamed: true));
        _fs.Files[Dst].Should().Be("original");
        _fs.Files["/vault/notes (2).txt"].Should().Be("new");
        _fs.Files.Should().NotContainKey(Src);
    }

    [Fact]
    public async Task Locked_source_is_skipped_as_locked()
    {
        _fs.Locked.Add(Src);
        (await Mover.MoveAsync(Src, Dst)).Should().Be(new MoveResult.Skipped(Src, SkipReason.Locked));
        _fs.Files[Src].Should().Be("new");
        _fs.Attempts.Should().ContainSingle();
    }

    [Fact]
    public async Task Exhausting_999_returns_name_conflict()
    {
        _fs.Files[Dst] = "o";
        for (var n = 2; n <= 999; n++) _fs.Files[$"/vault/notes ({n}).txt"] = "o";
        var r = await Mover.MoveAsync(Src, Dst);
        r.Should().Be(new MoveResult.Skipped(Src, SkipReason.NameConflict));
        _fs.Attempts.Should().HaveCount(999);
        _fs.Attempts[^1].Should().Be("/vault/notes (999).txt");
        _fs.Files[Src].Should().Be("new");
    }

    [Fact]
    public async Task Race_file_appears_between_attempts()
    {
        // 第一次尝试时目标空闲但被别人抢先写入；第二次尝试前 (2) 也被抢占
        _fs.BeforeAttempt = dst =>
        {
            if (dst == Dst || dst == "/vault/notes (2).txt") _fs.Files.TryAdd(dst, "other");
        };
        var r = await Mover.MoveAsync(Src, Dst);
        r.Should().Be(new MoveResult.Moved(Src, "/vault/notes (3).txt", Renamed: true));
        _fs.Files[Dst].Should().Be("other");
        _fs.Files["/vault/notes (2).txt"].Should().Be("other");
        _fs.Files["/vault/notes (3).txt"].Should().Be("new");
    }

    [Fact]
    public async Task Never_prechecks_existence_first_call_is_the_move()
    {
        var native = Substitute.For<INativeFileMove>();
        native.Move(Src, Dst).Returns(Win32Errors.Success);
        await new RenamingFileMover(native, _ => false).MoveAsync(Src, Dst);
        native.Received(1).Move(Src, Dst);
    }

    [Fact]
    public async Task Directory_rename_has_no_extension_split()
    {
        _fs.Files["/desk/a.b"] = "dir";
        _fs.Files["/vault/a.b"] = "o";
        var r = await new RenamingFileMover(_fs, _ => true).MoveAsync("/desk/a.b", "/vault/a.b");
        ((MoveResult.Moved)r).TargetPath.Should().Be("/vault/a.b (2)");
    }

    [Theory]
    [InlineData(Win32Errors.SharingViolation, SkipReason.Locked)]
    [InlineData(Win32Errors.LockViolation, SkipReason.Locked)]
    [InlineData(Win32Errors.FileNotFound, SkipReason.NotFound)]
    [InlineData(Win32Errors.PathNotFound, SkipReason.NotFound)]
    [InlineData(Win32Errors.AccessDenied, SkipReason.NeedsAdmin)]
    [InlineData(Win32Errors.NotSameDevice, SkipReason.CrossVolume)]
    [InlineData(Win32Errors.WriteProtect, SkipReason.ReadOnly)]
    [InlineData(362, SkipReason.CloudOnly)]
    [InlineData(406, SkipReason.CloudOnly)]
    [InlineData(112, SkipReason.IoError)]
    public async Task Error_mapping(int error, SkipReason expected)
    {
        var native = Substitute.For<INativeFileMove>();
        native.Move(default!, default!).ReturnsForAnyArgs(error);
        (await new RenamingFileMover(native, _ => false).MoveAsync(Src, Dst))
            .Should().Be(new MoveResult.Skipped(Src, expected));
    }

    [Fact]
    public async Task Guards()
    {
        await FluentActions.Awaiting(() => Mover.MoveAsync(" ", Dst)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Mover.MoveAsync(Src, "")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Mover.MoveAsync(Src, Dst, new CancellationToken(true))).Should().ThrowAsync<OperationCanceledException>();
    }
}

public class ConflictNamingTests
{
    [Theory]
    [InlineData("/v/notes.txt", 2, false, "/v/notes (2).txt")]
    [InlineData("/v/a.tar.gz", 999, false, "/v/a.tar (999).gz")]
    [InlineData("/v/.gitignore", 3, false, "/v/.gitignore (3)")]
    [InlineData("/v/noext", 2, false, "/v/noext (2)")]
    [InlineData("/v/照片😀.png", 2, false, "/v/照片😀 (2).png")]
    [InlineData("/v/my.folder", 2, true, "/v/my.folder (2)")]
    public void Builds_names(string path, int n, bool dir, string expected) =>
        ConflictNaming.WithIndex(path, n, dir).Should().Be(expected);

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Index_out_of_range(int n) =>
        FluentActions.Invoking(() => ConflictNaming.WithIndex("/a.txt", n, false)).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Long_path_supported()
    {
        var p = "/" + new string('d', 300) + "/f.txt";
        ConflictNaming.WithIndex(p, 2, false).Should().EndWith("/f (2).txt").And.HaveLength(p.Length + 4);
    }
}

public class JournaledMoveTests
{
    [Fact]
    public async Task Commits_actual_renamed_path()
    {
        var fs = new FakeFs();
        fs.Files["/d/notes.txt"] = "n";
        fs.Files["/v/notes.txt"] = "o";
        var journal = Substitute.For<IJournal>();
        JournalEntry? pending = null;
        await journal.AppendPendingAsync(Arg.Do<JournalEntry>(e => pending = e), Arg.Any<CancellationToken>());
        var sut = new JournaledMove(new RenamingFileMover(fs, _ => false), journal, TimeProvider.System);
        var job = Guid.NewGuid();

        var r = await sut.ExecuteAsync(job, "/d/notes.txt", "/v/notes.txt");

        ((MoveResult.Moved)r).Renamed.Should().BeTrue();
        pending!.JobId.Should().Be(job);
        pending.TargetPath.Should().Be("/v/notes.txt");
        await journal.Received(1).CommitAsync(pending.Id, "/v/notes (2).txt", Arg.Any<CancellationToken>());
        await journal.DidNotReceiveWithAnyArgs().RollBackAsync(default);
    }

    [Fact]
    public async Task Skipped_move_rolls_back()
    {
        var mover = Substitute.For<IFileMover>();
        mover.MoveAsync("s", "t", Arg.Any<CancellationToken>()).Returns(new MoveResult.Skipped("s", SkipReason.Locked));
        var journal = Substitute.For<IJournal>();
        var r = await new JournaledMove(mover, journal, TimeProvider.System).ExecuteAsync(Guid.NewGuid(), "s", "t");
        r.Should().BeOfType<MoveResult.Skipped>();
        await journal.Received(1).RollBackAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await journal.DidNotReceiveWithAnyArgs().CommitAsync(default, default!);
    }
}

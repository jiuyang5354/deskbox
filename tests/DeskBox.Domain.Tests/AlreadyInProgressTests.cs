using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;
using NSubstitute;

namespace DeskBox.Domain.Tests;

/// <summary>Begin 时同一 file_id 已有 Pending → Skipped(AlreadyInProgress)：不报错、不移动、不碰那条 Pending。</summary>
public class AlreadyInProgressTests
{
    private const string Id = "1a2b3c4d:000000000000000000000000000000aa";

    [Fact]
    public async Task Journal_conflict_at_begin_maps_to_skipped_already_in_progress_before_any_move()
    {
        var identity = Substitute.For<IFileIdentity>();
        identity.TryGetFileId(@"C:\desk\a.txt").Returns(Id);
        var journal = Substitute.For<IJournal>();
        journal.HasPendingAsync(Arg.Any<IUnitOfWork>(), Id.ToUpperInvariant(), Arg.Any<CancellationToken>()).Returns(true);
        var mover = Substitute.For<IFileMover>();
        var log = Substitute.For<IRecoveryLog>();
        var sut = new JournaledMove(new FakeDb(), mover, journal, identity, log, TimeProvider.System);

        var r = await sut.ExecuteAsync(Guid.NewGuid(), @"C:\desk\a.txt", @"C:\vault\a.txt");

        r.Should().Be(new MoveResult.Skipped(@"C:\desk\a.txt", SkipReason.AlreadyInProgress));
        ((MoveResult.Skipped)r).AbortReason.Should().Be("AlreadyInProgress");
        mover.ReceivedCalls().Should().BeEmpty("检查在 MoveFileExW 之前");
        await journal.DidNotReceiveWithAnyArgs().AppendPendingAsync(default!, default!, default);
        await journal.DidNotReceiveWithAnyArgs().RollBackAsync(default!, default, default!, default);
        await journal.DidNotReceiveWithAnyArgs().CommitAsync(default!, default, default!, default);
        log.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Other_journal_errors_at_begin_still_propagate()
    {
        var identity = Substitute.For<IFileIdentity>();
        identity.TryGetFileId(Arg.Any<string>()).Returns(Id);
        var journal = Substitute.For<IJournal>();
        journal.AppendPendingAsync(default!, default!, default).ReturnsForAnyArgs<Task>(_ => throw new IOException("disk"));
        var sut = new JournaledMove(new FakeDb(), Substitute.For<IFileMover>(), journal, identity, Substitute.For<IRecoveryLog>(), TimeProvider.System);
        await FluentActions.Awaiting(() => sut.ExecuteAsync(Guid.NewGuid(), "s", "t")).Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Precheck_and_append_share_one_transaction()
    {
        var db = new FakeDb();
        var journal = new InMemoryJournal();
        var identity = Substitute.For<IFileIdentity>();
        identity.TryGetFileId(Arg.Any<string>()).Returns(Id);
        var mover = Substitute.For<IFileMover>();
        mover.MoveAsync(default!, default!, default, default).ReturnsForAnyArgs(new MoveResult.Skipped("s", SkipReason.Locked, 32));
        var sut = new JournaledMove(db, mover, journal, identity, Substitute.For<IRecoveryLog>(), TimeProvider.System);
        await sut.ExecuteAsync(Guid.NewGuid(), "s", "t");
        db.Commits.Should().Be(2, "Begin（查 + 写）一个事务，RollBack 一个事务");
        (await sut.ExecuteAsync(Guid.NewGuid(), "s", "t")).Should().BeOfType<MoveResult.Skipped>()
            .Which.Reason.Should().Be(SkipReason.Locked, "上一条已 RolledBack，不再算进行中");
    }
}

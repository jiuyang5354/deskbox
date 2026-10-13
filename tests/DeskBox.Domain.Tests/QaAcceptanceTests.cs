using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;
using DeskBox.Domain.StateMachines;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DeskBox.Domain.Tests;

public class QaAcceptanceTests
{
    const string Src = @"C:\Users\u\Desktop\notes.txt";
    const string Dst = @"C:\Users\u\DeskBox\Vault\notes.txt";

    const string FileId = "0badf00d:0123456789abcdef0123456789abcdef";

    static IFileIdentity Identity()
    {
        var i = Substitute.For<IFileIdentity>();
        i.TryGetFileId(Src).Returns(FileId);
        return i;
    }

    static (RenamingFileMover m, INativeFileMove n) Mover()
    {
        var n = Substitute.For<INativeFileMove>();
        var p = Substitute.For<IFileSystemProbe>();
        p.GetAttributes(Arg.Any<string>()).Returns(0x20u);
        var f = Substitute.For<IKnownFolders>();
        f.PublicDesktop.Returns(@"C:\Users\Public\Desktop");
        return (new RenamingFileMover(n, p, f), n);
    }

    // QA-01 崩溃窗口：原生移动成功后、Commit 之前进程死掉，Pending 记录里只有原目标路径
    [Fact]
    public async Task QA01_pending_entry_does_not_know_renamed_target_before_commit()
    {
        var (m, n) = Mover();
        n.Move(Src, Dst).Returns(Win32Errors.AlreadyExists);
        n.Move(Src, Arg.Is<string>(s => s.EndsWith("notes (2).txt"))).Returns(Win32Errors.Success);
        var journal = Substitute.For<IJournal>();
        JournalEntry? pending = null;
        journal.AppendPendingAsync(Arg.Any<IUnitOfWork>(), Arg.Do<JournalEntry>(e => pending = e), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        journal.CommitAsync(default!, default, default!, default).ReturnsForAnyArgs<Task>(_ => throw new IOException("模拟崩溃：Commit 前进程被杀"));
        var jm = new JournaledMove(new FakeDb(), m, journal, Identity(), Substitute.For<IFileSystemProbe>(), Substitute.For<IRecoveryLog>(), TimeProvider.System);
        await FluentActions.Awaiting(() => jm.ExecuteAsync(Guid.NewGuid(), Src, Dst)).Should().ThrowAsync<IOException>();
        // 文件实际落在 notes (2).txt，但磁盘上能留给恢复逻辑的只有这条 Pending：
        // 它必须带 file_id，且在调用 MoveFileExW 之前已持久化了候选路径
        pending!.FileId.Should().Be(FileId.ToUpperInvariant());
        pending.Status.Should().Be(JournalStatus.Pending);
        pending.TargetPath.Should().EndWith("notes (2).txt", "恢复时按 Pending 记录找文件，记录必须能定位到实际落地路径");
        pending.Attempt.Should().Be(2);
        Received.InOrder(() =>
        {
            journal.UpdateCandidateAsync(Arg.Any<IUnitOfWork>(), pending.Id, Dst, 1, Arg.Any<CancellationToken>());
            n.Move(Src, Dst);
            journal.UpdateCandidateAsync(Arg.Any<IUnitOfWork>(), pending.Id, Arg.Is<string>(s => s.EndsWith("notes (2).txt")), 2, Arg.Any<CancellationToken>());
            n.Move(Src, Arg.Is<string>(s => s.EndsWith("notes (2).txt")));
        });
    }

    // QA-02 取消/异常时 Pending 不能悬空
    [Fact]
    public async Task QA02_cancel_during_move_leaves_journal_pending()
    {
        var (m, n) = Mover();
        var journal = Substitute.For<IJournal>();
        using var cts = new CancellationTokenSource();
        journal.AppendPendingAsync(Arg.Any<IUnitOfWork>(), Arg.Any<JournalEntry>(), Arg.Any<CancellationToken>()).Returns(_ => { cts.Cancel(); return Task.CompletedTask; });
        var jm = new JournaledMove(new FakeDb(), m, journal, Identity(), Substitute.For<IFileSystemProbe>(), Substitute.For<IRecoveryLog>(), TimeProvider.System);
        await FluentActions.Awaiting(() => jm.ExecuteAsync(Guid.NewGuid(), Src, Dst, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        await journal.ReceivedWithAnyArgs(1).RollBackAsync(default!, default, default!, default);
    }

    // QA-03 尾随空格/点：先剥掉末尾点和空格再拆扩展名（PRD v1.2.2）
    [Theory]
    [InlineData("report. ", "report (2)")]
    [InlineData("a .", "a (2)")]
    public void QA03_conflict_name_strips_trailing_dot_space(string name, string expected)
    {
        const string dir = @"C:\v";
        ConflictNaming.WithIndex(WinPath.Combine(dir, name), 2, false).Should().Be(WinPath.Combine(dir, expected));
    }

    // QA-04 999 用尽：恰好调用 999 次（原名 + 2..999）
    [Fact]
    public async Task QA04_exhaustion_calls_exactly_999_times()
    {
        var (m, n) = Mover();
        n.Move(default!, default!).ReturnsForAnyArgs(Win32Errors.FileExists);
        var r = await m.MoveAsync(Src, Dst);
        r.Should().BeOfType<MoveResult.Skipped>().Which.Reason.Should().Be(SkipReason.NameConflict);
        n.ReceivedCalls().Count().Should().Be(999);
    }

    // QA-05 公共桌面预检被前缀/斜杠绕过
    [Theory]
    [InlineData(@"\\?\C:\Users\Public\Desktop\a.lnk")]
    [InlineData(@"C:/Users/Public/Desktop/a.lnk")]
    [InlineData(@"C:\Users\Public\Desktop\\a.lnk")]
    public async Task QA05_public_desktop_precheck_variants(string src)
    {
        var (m, n) = Mover();
        var r = await m.MoveAsync(src, Dst);
        r.Should().BeOfType<MoveResult.Skipped>().Which.Reason.Should().Be(SkipReason.NeedsAdmin);
        n.ReceivedCalls().Should().BeEmpty();
    }

    // QA-06 非法流转：除 Classifying 外任何状态都不能 Cancel
    [Fact]
    public void QA06_cancel_only_from_classifying()
    {
        foreach (var s in Enum.GetValues<OrganizeJobState>().Where(s => s != OrganizeJobState.Classifying))
            OrganizeJobStateMachine.Table.ContainsKey((s, OrganizeJobTrigger.Cancel)).Should().BeFalse(s.ToString());
    }
}

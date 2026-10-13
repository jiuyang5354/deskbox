using DeskBox.Domain.Entities;

namespace DeskBox.Domain.Tests;

public class JournalRestoreTests
{
    private const string Id = "1a2b3c4d:000000000000000000000000000000aa";
    private static readonly Guid A = Guid.NewGuid(), J = Guid.NewGuid();

    private static JournalEntry R(JournalStatus s, string? actual = null, string? reason = null, int attempt = 2, int failures = 1) =>
        JournalEntry.Restore(A, J, Id, "s", "t", DateTimeOffset.UnixEpoch, attempt, s, actual, reason, failures);

    [Fact]
    public void Restores_every_state_with_consistent_fields()
    {
        var p = R(JournalStatus.Pending, "ignored", "ignored");
        (p.Status, p.Attempt, p.RecoveryFailures, p.ActualTargetPath, p.AbortReason, p.FileId)
            .Should().Be((JournalStatus.Pending, 2, 1, (string?)null, (string?)null, Id.ToUpperInvariant()));
        p.RecordRecoveryFailure().Should().Be(2, "从持久化计数继续累加");
        R(JournalStatus.Committed, "t (2)").ActualTargetPath.Should().Be("t (2)");
        R(JournalStatus.RolledBack, reason: "Locked (Win32 32)").AbortReason.Should().Be("Locked (Win32 32)");
        var lost = R(JournalStatus.Lost);
        lost.Status.Should().Be(JournalStatus.Lost);
        FluentActions.Invoking(() => lost.Commit("x")).Should().Throw<InvalidTransitionException>();
    }

    [Fact]
    public void Rejects_inconsistent_rows()
    {
        FluentActions.Invoking(() => R(JournalStatus.Committed)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => R(JournalStatus.RolledBack)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => R((JournalStatus)42)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => R(JournalStatus.Pending, attempt: -1)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => R(JournalStatus.Pending, failures: -1)).Should().Throw<DomainValidationException>();
        FluentActions.Invoking(() => JournalEntry.Restore(A, J, "bad", "s", "t", default, 0, JournalStatus.Pending, null, null, 0))
            .Should().Throw<DomainValidationException>();
    }
}

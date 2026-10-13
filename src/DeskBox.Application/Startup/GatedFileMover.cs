using DeskBox.Application.Ports;
using DeskBox.Domain.Files;

namespace DeskBox.Application.Startup;

/// <summary>
/// 包在真正的 <see cref="IFileMover"/> 外面：只读模式下在任何原生尝试之前抛 <see cref="ReadOnlyModeException"/>。
/// 此时 JournaledMove 尚未发起尝试，能证明未移动，会立即 RollBack 刚写的 Pending。
/// </summary>
public sealed class GatedFileMover(IFileMover inner, OperatingMode mode) : IFileMover
{
    private readonly IFileMover _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly OperatingMode _mode = mode ?? throw new ArgumentNullException(nameof(mode));

    public Task<MoveResult> MoveAsync(string sourcePath, string targetPath,
        Func<string, CancellationToken, Task>? beforeAttempt, CancellationToken ct = default)
    {
        if (_mode.ReadOnlyReason is { } reason) throw new ReadOnlyModeException(reason);
        return _inner.MoveAsync(sourcePath, targetPath, beforeAttempt, ct);
    }
}

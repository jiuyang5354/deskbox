using DeskBox.Application.Ports;
using DeskBox.Domain.Files;

namespace DeskBox.Application.Files;

/// <summary>
/// 安全移动：不预先检查目标是否存在（避免 TOCTOU 竞争），直接做不替换移动；
/// 目标已存在则依次尝试 "name (n).ext"（n=2..999），用尽返回 Skipped(NameConflict)。
/// </summary>
public sealed class RenamingFileMover(INativeFileMove native, Func<string, bool> isDirectory) : IFileMover
{
    public Task<MoveResult> MoveAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Move(sourcePath, targetPath, ct));
    }

    private MoveResult Move(string source, string target, CancellationToken ct)
    {
        var dir = isDirectory(source);
        for (var n = 1; n <= ConflictNaming.MaxIndex; n++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = n == 1 ? target : ConflictNaming.WithIndex(target, n, dir);
            var error = native.Move(source, candidate);
            if (error == Win32Errors.Success) return new MoveResult.Moved(source, candidate, Renamed: n > 1);
            if (error is Win32Errors.FileExists or Win32Errors.AlreadyExists) continue;
            return new MoveResult.Skipped(source, Map(error));
        }
        return new MoveResult.Skipped(source, SkipReason.NameConflict);
    }

    internal static SkipReason Map(int error) => error switch
    {
        Win32Errors.SharingViolation or Win32Errors.LockViolation => SkipReason.Locked,
        Win32Errors.FileNotFound or Win32Errors.PathNotFound => SkipReason.NotFound,
        Win32Errors.AccessDenied => SkipReason.NeedsAdmin,
        Win32Errors.NotSameDevice => SkipReason.CrossVolume,
        Win32Errors.WriteProtect => SkipReason.ReadOnly,
        >= Win32Errors.CloudFirst and <= Win32Errors.CloudLast => SkipReason.CloudOnly,
        _ => SkipReason.IoError,
    };
}

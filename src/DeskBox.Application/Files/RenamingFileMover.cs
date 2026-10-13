using DeskBox.Application.Ports;
using DeskBox.Domain.Files;

namespace DeskBox.Application.Files;

/// <summary>
/// 安全移动：不预先检查目标是否存在（避免 TOCTOU 竞争），直接做不替换移动；
/// 目标已存在则依次尝试 "name (n).ext"（n=2..999），用尽返回 Skipped(NameConflict)。
/// </summary>
/// 预检查（不调用原生移动）：仅云端文件 → CloudOnly；位于公共桌面 → NeedsAdmin。
public sealed class RenamingFileMover(INativeFileMove native, IFileSystemProbe probe, IKnownFolders folders) : IFileMover
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
        var attributes = probe.GetAttributes(source);
        if (attributes is { } a && (a & Win32FileAttributes.CloudMask) != 0)
            return new MoveResult.Skipped(source, SkipReason.CloudOnly);
        if (IsUnder(source, folders.PublicDesktop))
            return new MoveResult.Skipped(source, SkipReason.NeedsAdmin);
        var dir = probe.IsDirectory(source);
        for (var n = 1; n <= ConflictNaming.MaxIndex; n++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = n == 1 ? target : ConflictNaming.WithIndex(target, n, dir);
            var error = native.Move(source, candidate);
            if (error == Win32Errors.Success) return new MoveResult.Moved(source, candidate, Renamed: n > 1);
            if (error is Win32Errors.FileExists or Win32Errors.AlreadyExists) continue;
            return new MoveResult.Skipped(source, Map(error), error);
        }
        return new MoveResult.Skipped(source, SkipReason.NameConflict, Win32Errors.AlreadyExists);
    }

    internal static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var root = folder.TrimEnd('\\', '/');
        return path.Length > root.Length + 1
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && path[root.Length] is '\\' or '/';
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

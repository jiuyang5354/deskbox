using DeskBox.Application.Ports;
using DeskBox.Domain.Files;

namespace DeskBox.Application.Files;

/// <summary>
/// 安全移动：不预先检查目标是否存在（避免 TOCTOU 竞争），直接做不替换移动；
/// 目标已存在则依次尝试 "name (n).ext"（n=2..999），用尽返回 Skipped(NameConflict)。
/// </summary>
/// 预检查（不调用原生移动）：仅云端文件 → CloudOnly；位于公共桌面（两侧路径规范化后比较）→ NeedsAdmin。
public sealed class RenamingFileMover(INativeFileMove native, IFileSystemProbe probe, IKnownFolders folders) : IFileMover
{
    public Task<MoveResult> MoveAsync(string sourcePath, string targetPath, CancellationToken ct = default) =>
        MoveAsync(sourcePath, targetPath, null, ct);

    public Task<MoveResult> MoveAsync(string sourcePath, string targetPath,
        Func<string, CancellationToken, Task>? beforeAttempt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ct.ThrowIfCancellationRequested();
        return Move(sourcePath, targetPath, beforeAttempt, ct);
    }

    private async Task<MoveResult> Move(string source, string target, Func<string, CancellationToken, Task>? beforeAttempt, CancellationToken ct)
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
            if (beforeAttempt is not null) await beforeAttempt(candidate, ct);
            var error = native.Move(source, candidate);
            if (error == Win32Errors.Success) return new MoveResult.Moved(source, candidate, Renamed: n > 1);
            if (error is Win32Errors.FileExists or Win32Errors.AlreadyExists) continue;
            return new MoveResult.Skipped(source, Map(error), error);
        }
        return new MoveResult.Skipped(source, SkipReason.NameConflict, Win32Errors.AlreadyExists);
    }

    /// <summary>
    /// 判断 path 是否位于 folder 之下：两侧都先经 <see cref="Win32PathNormalizer"/>
    /// （统一 "\\?\" / UNC 前缀、"/" 分隔符、重复分隔符、"." / ".."），再做不区分大小写的前缀比较，
    /// 且要求前缀后紧跟分隔符，避免 Desktopx 这类兄弟目录误判。任一侧无法规范化则视为不在其下。
    /// </summary>
    internal static bool IsUnder(string path, string folder)
    {
        if (!Win32PathNormalizer.TryNormalize(path, out var p) || !Win32PathNormalizer.TryNormalize(folder, out var f))
            return false;
        var root = f.TrimEnd('\\');
        return p.Length > root.Length + 1
            && p.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && p[root.Length] == '\\';
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

using System.IO;
using DeskBox.Application.Ports;
using DeskBox.Domain.Files;

namespace DeskBox.Infrastructure.Files;

/// <summary>同分区内安全移动：目标存在则跳过，绝不覆盖。</summary>
public sealed class SafeFileMover : IFileMover
{
    public Task<MoveResult> MoveAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run<MoveResult>(() =>
        {
            var isDir = Directory.Exists(sourcePath);
            if (!isDir && !File.Exists(sourcePath)) return new MoveResult.Skipped(sourcePath, SkipReason.NotFound);
            if (!string.Equals(Path.GetPathRoot(sourcePath), Path.GetPathRoot(targetPath), StringComparison.OrdinalIgnoreCase))
                return new MoveResult.Skipped(sourcePath, SkipReason.CrossVolume);
            var attrs = File.GetAttributes(sourcePath);
            if ((attrs & FileAttributes.Offline) != 0 || ((int)attrs & 0x00400000) != 0)
                return new MoveResult.Skipped(sourcePath, SkipReason.CloudOnly);
            if (!isDir && (attrs & FileAttributes.ReadOnly) != 0) return new MoveResult.Skipped(sourcePath, SkipReason.ReadOnly);
            if (File.Exists(targetPath) || Directory.Exists(targetPath)) return new MoveResult.Skipped(sourcePath, SkipReason.Locked);
            try
            {
                if (isDir) Directory.Move(sourcePath, targetPath);
                else File.Move(sourcePath, targetPath, overwrite: false);
                return new MoveResult.Moved(sourcePath, targetPath);
            }
            catch (UnauthorizedAccessException) { return new MoveResult.Skipped(sourcePath, SkipReason.NeedsAdmin); }
            catch (IOException) { return new MoveResult.Skipped(sourcePath, SkipReason.Locked); }
        }, ct);
    }
}

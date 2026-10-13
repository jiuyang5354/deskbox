using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Infrastructure.Files;

/// <summary>
/// 用 GetFileInformationByHandleEx(FileIdInfo) 读取 "卷序列号:128 位文件 ID"（与 FenceItem.IsValidFileId 格式一致）。
/// 只读属性（不请求读写权限）、允许共享，目录用 FILE_FLAG_BACKUP_SEMANTICS 打开；不跟随重解析点。
/// 文件不存在或无法打开时返回 null。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsFileIdentity : IFileIdentity
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong IdLow;
        public ulong IdHigh;
    }

    public string? TryGetFileId(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        using var h = CreateFile(Win32PathNormalizer.Normalize(fullPath), FileReadAttributes, ShareAll, IntPtr.Zero,
            OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid) return null;
        if (!GetFileInformationByHandleEx(h, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
            throw new IOException($"GetFileInformationByHandleEx 失败（Win32 {Marshal.GetLastPInvokeError()}）");
        return FileIdFormat.Format(info.VolumeSerialNumber, info.IdHigh, info.IdLow);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FileIdInfo info, uint size);
}

/// <summary>file_id 文本格式："8 位卷序列号:32 位文件 ID"（大写十六进制），与 FenceItem.IsValidFileId 一致。</summary>
public static class FileIdFormat
{
    public static string Format(ulong volume, ulong high, ulong low) =>
        string.Create(CultureInfo.InvariantCulture, $"{(uint)volume:X8}:{high:X16}{low:X16}");
}

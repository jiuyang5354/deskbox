using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;
using DeskBox.Domain.Files;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Infrastructure.Files;

/// <summary>
/// 用目录句柄上的 GetFileInformationByHandleEx(FileIdExtdDirectoryInfo) 列出直接子项：
/// 一次拿到名字、属性、128 位文件 ID、修改时间，<b>不打开任何子项</b>（不会触发云端文件下载），
/// 也不进入联接点 / 符号链接目录。目录路径先经 Win32PathNormalizer（\\?\ 前缀，支持长路径）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsDirectoryLister : IDirectoryLister
{
    private const uint FileListDirectory = 0x1;
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const int FileIdInfoClass = 18;
    private const int FileIdExtdDirectoryInfoClass = 19;
    private const int FileIdExtdDirectoryRestartInfoClass = 20;
    private const int ErrorNoMoreFiles = 18;
    private const int BufferSize = 64 * 1024;

    public IReadOnlyList<DirectoryItem> List(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var h = CreateFile(Win32PathNormalizer.Normalize(directory), FileListDirectory | FileReadAttributes, ShareAll,
            IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"无法打开目录 {directory}（Win32 {Marshal.GetLastPInvokeError()}）");

        var idInfo = new byte[24];
        if (!GetFileInformationByHandleEx(h, FileIdInfoClass, idInfo, (uint)idInfo.Length))
            throw new IOException($"读取卷序列号失败（Win32 {Marshal.GetLastPInvokeError()}）");
        var volume = BinaryPrimitives.ReadUInt64LittleEndian(idInfo);

        var items = new List<DirectoryItem>();
        var buffer = new byte[BufferSize];
        var infoClass = FileIdExtdDirectoryRestartInfoClass;
        while (true)
        {
            if (!GetFileInformationByHandleEx(h, infoClass, buffer, BufferSize))
            {
                var err = Marshal.GetLastPInvokeError();
                if (err == ErrorNoMoreFiles) break;
                throw new IOException($"列目录失败 {directory}（Win32 {err}）");
            }
            infoClass = FileIdExtdDirectoryInfoClass;
            DirectoryInfoParser.Parse(buffer, directory, volume, items);
        }
        return items;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, [Out] byte[] buffer, uint size);
}

/// <summary>FILE_ID_EXTD_DIR_INFO 缓冲区解析（纯内存操作，可在任何平台测试）。</summary>
internal static class DirectoryInfoParser
{
    /// <summary>
    /// 解析 FILE_ID_EXTD_DIR_INFO 链表：NextEntryOffset@0, LastWriteTime@24, FileAttributes@56,
    /// FileNameLength@60, FileId@72（低 8 字节在前）, FileName@88（UTF-16）。跳过 "." 与 ".."。
    /// </summary>
    internal static void Parse(ReadOnlySpan<byte> buffer, string directory, ulong volume, List<DirectoryItem> into)
    {
        var offset = 0;
        while (true)
        {
            var e = buffer[offset..];
            var next = BinaryPrimitives.ReadInt32LittleEndian(e);
            var lastWrite = BinaryPrimitives.ReadInt64LittleEndian(e[24..]);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(e[56..]);
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(e[60..]);
            var low = BinaryPrimitives.ReadUInt64LittleEndian(e[72..]);
            var high = BinaryPrimitives.ReadUInt64LittleEndian(e[80..]);
            var name = Encoding.Unicode.GetString(e.Slice(88, nameLength));
            if (name is not ("." or ".."))
                into.Add(new DirectoryItem(WinPath.Combine(directory, name), attributes, FileIdFormat.Format(volume, high, low),
                    DateTimeOffset.FromFileTime(lastWrite).ToUniversalTime()));
            if (next == 0) return;
            offset += next;
        }
    }
}

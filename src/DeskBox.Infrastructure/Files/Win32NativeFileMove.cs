using System.Runtime.InteropServices;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;

namespace DeskBox.Infrastructure.Files;

/// <summary>MoveFileExW(src, dst, 0)：不替换已有文件，不允许跨卷复制。两端路径都先经 Win32PathNormalizer。</summary>
public sealed partial class Win32NativeFileMove : INativeFileMove
{
    private const uint NoFlags = 0;

    public int Move(string sourcePath, string targetPath) =>
        MoveFileEx(Win32PathNormalizer.Normalize(sourcePath), Win32PathNormalizer.Normalize(targetPath), NoFlags)
            ? Win32Errors.Success
            : Marshal.GetLastPInvokeError();

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}

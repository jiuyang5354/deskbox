using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;

namespace DeskBox.Infrastructure.Files;

/// <summary>通过 GetFileAttributesW 读取属性（只查目录项，不打开文件，不会触发云端文件下载）；路径先经 Win32PathNormalizer（长路径、末尾空格/点均保留）。</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsFileSystemProbe : IFileSystemProbe
{
    private const uint InvalidFileAttributes = 0xFFFF_FFFF;
    private const uint DirectoryAttribute = 0x10;

    public bool IsDirectory(string path) => GetAttributes(path) is { } a && (a & DirectoryAttribute) != 0;

    public uint? GetAttributes(string path)
    {
        var a = GetFileAttributes(Win32PathNormalizer.Normalize(path));
        return a == InvalidFileAttributes ? null : a;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFileAttributes(string fileName);
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsKnownFolders : IKnownFolders
{
    private static readonly Guid FolderIdPublicDesktop = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");
    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private readonly Lazy<string> _publicDesktop = new(() => Resolve(FolderIdPublicDesktop));
    private readonly Lazy<string> _userDesktop = new(() => Resolve(FolderIdDesktop));

    public string PublicDesktop => _publicDesktop.Value;
    public string UserDesktop => _userDesktop.Value;

    private static string Resolve(Guid id)
    {
        var hr = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr);
        try
        {
            Marshal.ThrowExceptionForHR(hr);
            return Marshal.PtrToStringUni(ptr) ?? string.Empty;
        }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);
}

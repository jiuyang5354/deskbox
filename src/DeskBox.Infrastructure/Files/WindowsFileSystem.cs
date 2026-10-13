using System.IO;
using System.Runtime.InteropServices;
using DeskBox.Application.Ports;

namespace DeskBox.Infrastructure.Files;

public sealed class WindowsFileSystemProbe : IFileSystemProbe
{
    public bool IsDirectory(string path) => Directory.Exists(path);

    public uint? GetAttributes(string path)
    {
        try { return (uint)File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
}

public sealed partial class WindowsKnownFolders : IKnownFolders
{
    private static readonly Guid FolderIdPublicDesktop = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");
    private readonly Lazy<string> _publicDesktop = new(() => Resolve(FolderIdPublicDesktop));

    public string PublicDesktop => _publicDesktop.Value;

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

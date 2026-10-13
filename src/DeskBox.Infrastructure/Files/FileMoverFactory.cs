using System.Runtime.Versioning;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;

namespace DeskBox.Infrastructure.Files;

[SupportedOSPlatform("windows")]
public static class FileMoverFactory
{
    public static IFileMover Create() => new RenamingFileMover(new Win32NativeFileMove(), new WindowsFileSystemProbe(), new WindowsKnownFolders());

    /// <summary>文件属性探测（JournaledMove 用它在打开文件之前识别仅云端文件）。</summary>
    public static IFileSystemProbe CreateProbe() => new WindowsFileSystemProbe();

    public static IDesktopEnumerator CreateDesktopEnumerator() =>
        new DesktopEnumerator(new WindowsKnownFolders(), new WindowsDirectoryLister(), new FileSystemDirectoryWatcher());
}

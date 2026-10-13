using System.Runtime.Versioning;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;

namespace DeskBox.Infrastructure.Files;

[SupportedOSPlatform("windows")]
public static class FileMoverFactory
{
    public static IFileMover Create() => new RenamingFileMover(new Win32NativeFileMove(), new WindowsFileSystemProbe(), new WindowsKnownFolders());

    public static IDesktopEnumerator CreateDesktopEnumerator() =>
        new DesktopEnumerator(new WindowsKnownFolders(), new WindowsDirectoryLister(), new FileSystemDirectoryWatcher());
}

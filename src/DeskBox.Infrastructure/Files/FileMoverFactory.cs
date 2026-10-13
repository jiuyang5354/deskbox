using System.IO;
using DeskBox.Application.Files;
using DeskBox.Application.Ports;

namespace DeskBox.Infrastructure.Files;

public static class FileMoverFactory
{
    public static IFileMover Create() => new RenamingFileMover(new Win32NativeFileMove(), Directory.Exists);
}

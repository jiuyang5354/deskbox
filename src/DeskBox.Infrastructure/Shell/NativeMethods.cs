using System.Runtime.InteropServices;

namespace DeskBox.Infrastructure.Shell;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "SetParent", SetLastError = true)]
    internal static partial IntPtr SetParent(IntPtr child, IntPtr newParent);
}

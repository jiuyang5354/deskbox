using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBox.Application.Ports;
using DeskBox.Domain.Geometry;
using static DeskBox.Infrastructure.Shell.NativeMethods;

namespace DeskBox.Infrastructure.Shell;

/// <summary>EnumDisplayMonitors + GetMonitorInfo + GetDpiForMonitor(MDT_EFFECTIVE_DPI)。进程须为 Per-Monitor V2（见 app.manifest）。</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32MonitorService : IMonitorService, IDisposable
{
    private const uint MonitorInfoPrimary = 1;
    private readonly Win32ShellNotifications? _shell;

    public Win32MonitorService(Win32ShellNotifications? shell)
    {
        _shell = shell;
        if (_shell is not null) _shell.DisplayChanged += OnDisplayChanged;
    }

    public event EventHandler? MonitorsChanged;

    private void OnDisplayChanged(object? sender, EventArgs e) => MonitorsChanged?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = "" };
            if (!GetMonitorInfo(h, ref info)) return true;
            var dpi = GetDpiForMonitor(h, 0, out var dx, out _) == 0 ? (int)dx : DesktopGeometry.BaseDpi;
            list.Add(new MonitorInfo(info.Device, ToRect(info.Monitor), ToRect(info.Work), dpi, (info.Flags & MonitorInfoPrimary) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static ScreenRect ToRect(Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public void Dispose()
    {
        if (_shell is not null) _shell.DisplayChanged -= OnDisplayChanged;
    }
}

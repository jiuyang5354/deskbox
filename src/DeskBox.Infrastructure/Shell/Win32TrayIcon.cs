using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBox.Application.Desktop;
using DeskBox.Application.Fences;
using DeskBox.Application.Ports;

namespace DeskBox.Infrastructure.Shell;

/// <summary>
/// 托盘图标（Shell_NotifyIcon，只显示提示文字，不弹气泡）。兼容显示时提示「桌面嵌入不可用，已切换为兼容显示」（PRD §6A）。
/// Explorer 重启（TaskbarCreated）后重新添加图标。必须在 UI 线程创建与使用。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32TrayIcon : IDisposable
{
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifIcon = 0x2, NifTip = 0x4;
    private static readonly IntPtr HwndMessage = -3;

    private readonly IShellNotifications _shell;
    private readonly IntPtr _owner;
    private string _tip = FenceUiText.TrayNormal;
    private bool _disposed;

    public Win32TrayIcon(IShellNotifications shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _owner = CreateWindowEx(0, "STATIC", "DeskBox.Tray", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Notify(NimAdd);
        _shell.TaskbarCreated += OnTaskbarCreated;
    }

    public void Show(EmbedMode mode)
    {
        _tip = FenceUiText.TrayTooltip(mode);
        if (!_disposed) Notify(NimModify);
    }

    private void OnTaskbarCreated(object? sender, EventArgs e)
    {
        if (!_disposed) Notify(NimAdd);
    }

    private void Notify(uint message)
    {
        if (_owner == IntPtr.Zero) return;
        var data = new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(), Hwnd = _owner, Id = 1, Flags = NifIcon | NifTip,
            Icon = LoadIcon(IntPtr.Zero, 32512 /* IDI_APPLICATION */), Tip = _tip,
        };
        Shell_NotifyIcon(message, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _shell.TaskbarCreated -= OnTaskbarCreated;
        Notify(NimDelete);
        _disposed = true;
        if (_owner != IntPtr.Zero) NativeMethods.DestroyWindow(_owner);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Hwnd;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(uint exStyle, string className, string? title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
}

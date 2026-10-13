using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBox.Application.Ports;
using static DeskBox.Infrastructure.Shell.NativeMethods;

namespace DeskBox.Infrastructure.Shell;

/// <summary>
/// 在专用线程上建一个隐藏的<b>顶层</b>窗口（TaskbarCreated / WM_DISPLAYCHANGE 只广播给顶层窗口，
/// HWND_MESSAGE 窗口收不到），RegisterWindowMessage("TaskbarCreated") 并对 UIPI 放行。
/// 事件封送到构造时传入的 <see cref="SynchronizationContext"/>（通常是 UI 线程）；为 null 时在监听线程上触发。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32ShellNotifications : IShellNotifications, IDisposable
{
    private readonly SynchronizationContext? _context;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly WndProc _proc; // 保持委托存活
    private readonly string _className = "DeskBox.ShellNotify." + Guid.NewGuid().ToString("N");
    private IntPtr _hwnd;
    private Exception? _startError;

    public Win32ShellNotifications(SynchronizationContext? context)
    {
        _context = context;
        _proc = Proc;
        TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (TaskbarCreatedMessage == 0) throw new InvalidOperationException("RegisterWindowMessage(TaskbarCreated) 失败");
        _thread = new Thread(Run) { IsBackground = true, Name = "DeskBox shell notifications" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startError is not null) throw new InvalidOperationException("无法创建通知窗口", _startError);
    }

    public uint TaskbarCreatedMessage { get; }

    public event EventHandler? TaskbarCreated;

    public event EventHandler? DisplayChanged;

    private void Run()
    {
        try
        {
            SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);
            var instance = GetModuleHandle(null);
            var cls = new WndClassEx
            {
                Size = Marshal.SizeOf<WndClassEx>(), WndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                Instance = instance, ClassName = _className,
            };
            if (RegisterClassExW(ref cls) == 0) throw new InvalidOperationException($"RegisterClassEx 失败（Win32 {Marshal.GetLastPInvokeError()}）");
            // WS_POPUP，不可见、不在任务栏
            _hwnd = CreateWindowEx(0x80 /* WS_EX_TOOLWINDOW */, _className, null, 0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx 失败（Win32 {Marshal.GetLastPInvokeError()}）");
            ChangeWindowMessageFilterEx(_hwnd, TaskbarCreatedMessage, MsgfltAllow, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException or EntryPointNotFoundException)
        {
            _startError = ex;
            _ready.Set();
            return;
        }
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessage(msg);
        }
        UnregisterClass(_className, GetModuleHandle(null));
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TaskbarCreatedMessage) Raise(TaskbarCreated);
        else if (msg == WmDisplayChange) Raise(DisplayChanged);
        else if (msg == WmClose)
        {
            DestroyWindow(hwnd);
            return IntPtr.Zero;
        }
        else if (msg == WmDestroy) PostQuitMessage(0);
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is null) return;
        if (_context is null) handler(this, EventArgs.Empty);
        else _context.Post(_ => handler(this, EventArgs.Empty), null);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero && PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero)) _thread.Join(TimeSpan.FromSeconds(5));
        _hwnd = IntPtr.Zero;
        _ready.Dispose();
    }
}

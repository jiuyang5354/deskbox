using System.Runtime.Versioning;
using System.Security.Principal;
using DeskBox.Application.Startup;

namespace DeskBox.Infrastructure.Startup;

/// <summary>
/// 按用户（SID）命名的 Local\ 互斥锁 + 同名事件。第一个进程持锁并在后台线程等待事件，
/// 第二个进程抢锁失败后 Set 事件再退出；第一个进程收到后触发 <see cref="Activated"/>（封送到构造时的同步上下文）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedMutexSingleInstance : ISingleInstance, IDisposable
{
    private readonly string _name;
    private readonly SynchronizationContext? _context;
    private Mutex? _mutex;
    private EventWaitHandle? _signal;
    private RegisteredWaitHandle? _wait;

    public NamedMutexSingleInstance(SynchronizationContext? context, string? name = null)
    {
        _context = context;
        _name = name ?? "DeskBox." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
    }

    public event EventHandler? Activated;

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_mutex is null && _signal is not null, this);
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + _name + ".mutex", out var created);
        if (!created)
        {
            mutex.Dispose();
            return false;
        }
        _mutex = mutex;
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + _name + ".activate");
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => Raise(), null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    public void SignalRunningInstance()
    {
        if (EventWaitHandle.TryOpenExisting(@"Local\" + _name + ".activate", out var e))
            using (e) e.Set();
    }

    private void Raise()
    {
        if (_context is null) Activated?.Invoke(this, EventArgs.Empty);
        else _context.Post(_ => Activated?.Invoke(this, EventArgs.Empty), null);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _signal?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
    }
}

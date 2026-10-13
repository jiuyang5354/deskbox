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

    /// <summary>锁名前缀：写死的常量，与程序集名 / exe 文件名无关（改名为 DeskBox.exe 不影响单实例）。</summary>
    public const string NamePrefix = "DeskBox";

    public NamedMutexSingleInstance(SynchronizationContext? context, string? name = null)
    {
        _context = context;
        _name = name ?? DefaultName(WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
    }

    /// <summary>按用户命名：<c>DeskBox.&lt;SID&gt;</c>，实际对象为 <c>Local\DeskBox.&lt;SID&gt;.mutex</c> / <c>.activate</c>。</summary>
    public static string DefaultName(string userKey) => NamePrefix + "." + userKey;

    public string MutexName => @"Local\" + _name + ".mutex";

    public event EventHandler? Activated;

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_mutex is null && _signal is not null, this);
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
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

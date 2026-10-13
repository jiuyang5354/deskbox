using DeskBox.Application.Ports;

namespace DeskBox.Application.Desktop;

public enum EmbedMode
{
    /// <summary>尚未嵌入。</summary>
    None,
    /// <summary>全部方框已成为 WorkerW 的子窗口。</summary>
    Embedded,
    /// <summary>找不到 Progman / WorkerW 或 SetParent 失败：普通置底窗口，仍然可见；等 TaskbarCreated 再试。</summary>
    Degraded,
}

/// <summary>
/// 把方框窗口嵌入桌面层，并在 Explorer 重启后自愈（TDD §4.5）。
/// <list type="bullet">
/// <item>嵌入：FindProgman → 发 0x052C → FindWorkerW → SetParent；不注入、不挂钩。</item>
/// <item>自愈：只由 TaskbarCreated 触发（无轮询、无忙重试）；收到后丢弃所有旧句柄，重新查找 WorkerW 并重新挂接。</item>
/// <item>位置：每次挂接都从数据库读取方框位置（<see cref="IFenceStore"/>），从不沿用重启前窗口的位置。</item>
/// <item>降级：找不到桌面层时退为普通置底窗口，方框照常显示。</item>
/// </list>
/// 所有回调都在调用方的 UI 线程上执行（IShellNotifications 的实现负责封送）。
/// </summary>
public sealed class DesktopEmbedder : Startup.IStartupEmbedder, IDisposable
{
    private readonly IUnitOfWorkFactory _db;
    private readonly IFenceStore _fences;
    private readonly IDesktopWindowing _win;
    private readonly IMonitorService _monitors;
    private readonly IBoxWindows _windows;
    private readonly IShellNotifications _shell;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private bool _disposed;

    public DesktopEmbedder(IUnitOfWorkFactory db, IFenceStore fences, IDesktopWindowing win, IMonitorService monitors,
        IBoxWindows windows, IShellNotifications shell)
    {
        (_db, _fences, _win, _monitors, _windows, _shell) = (
            db ?? throw new ArgumentNullException(nameof(db)),
            fences ?? throw new ArgumentNullException(nameof(fences)),
            win ?? throw new ArgumentNullException(nameof(win)),
            monitors ?? throw new ArgumentNullException(nameof(monitors)),
            windows ?? throw new ArgumentNullException(nameof(windows)),
            shell ?? throw new ArgumentNullException(nameof(shell)));
    }

    public EmbedMode Mode { get; private set; } = EmbedMode.None;

    /// <summary>当前（本轮查找到的）WorkerW；降级时为 Zero。</summary>
    public IntPtr WorkerW { get; private set; }

    /// <summary>最近一轮挂接的方框句柄。</summary>
    public IReadOnlyDictionary<Guid, IntPtr> Windows { get; private set; } = new Dictionary<Guid, IntPtr>();

    /// <summary>每轮挂接完成（含降级）后触发。</summary>
    public event EventHandler<EmbedMode>? Attached;

    /// <summary>每轮挂接中某个方框失败（异常不外抛，方框留在降级状态）。</summary>
    public event EventHandler<Exception>? AttachFailed;

    /// <summary>首次挂接，并开始监听 TaskbarCreated。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            _shell.TaskbarCreated += OnTaskbarCreated;
            _started = true;
        }
        await AttachAllAsync(ct);
    }

    private async void OnTaskbarCreated(object? sender, EventArgs e)
    {
        try
        {
            await AttachAllAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 事件处理器里不能把异常抛回消息循环；交给订阅者记录，下一次 TaskbarCreated 再试
            Mode = EmbedMode.Degraded;
            AttachFailed?.Invoke(this, ex);
        }
    }

    /// <summary>一轮完整挂接：丢弃旧句柄 → 重新查找桌面层 → 从数据库读位置 → 挂接并放置每个方框。</summary>
    public async Task AttachAllAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            WorkerW = IntPtr.Zero; // 旧句柄一律作废
            var fences = await _db.InTransactionAsync(u => _fences.ListAsync(u, ct), ct);
            var monitors = _monitors.GetMonitors();
            var host = FindHost();
            var all = host != IntPtr.Zero;
            var handles = new Dictionary<Guid, IntPtr>();
            foreach (var fence in fences)
            {
                var hwnd = _windows.Ensure(fence);
                handles[fence.Id] = hwnd;
                var embedded = host != IntPtr.Zero && _win.SetParent(hwnd, host);
                if (!embedded)
                {
                    all = false;
                    if (_win.GetParent(hwnd) != IntPtr.Zero) _win.SetParent(hwnd, IntPtr.Zero);
                    _win.SetBottomMost(hwnd);
                }
                _win.SetBounds(hwnd, FenceLayout.Place(fence, monitors));
            }
            WorkerW = host;
            Windows = handles;
            Mode = all ? EmbedMode.Embedded : EmbedMode.Degraded;
        }
        finally
        {
            _gate.Release();
        }
        Attached?.Invoke(this, Mode);
    }

    private IntPtr FindHost()
    {
        var progman = _win.FindProgman();
        if (progman == IntPtr.Zero) return IntPtr.Zero;
        _win.SpawnWorkerW(progman);
        var worker = _win.FindWorkerW(progman);
        return worker != IntPtr.Zero && _win.IsWindow(worker) ? worker : IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started) _shell.TaskbarCreated -= OnTaskbarCreated;
        _gate.Dispose();
    }
}

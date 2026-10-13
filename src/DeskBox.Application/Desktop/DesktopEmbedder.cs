using DeskBox.Application.Ports;

namespace DeskBox.Application.Desktop;

public enum EmbedMode
{
    /// <summary>尚未嵌入。</summary>
    None,
    /// <summary>全部方框已挂到桌面层（旧层级的 WorkerW，或 24H2 起的 Progman）。</summary>
    Embedded,
    /// <summary>兼容显示：至少一个方框没能嵌入（找不到桌面层、SetParent 失败或挂载后校验失败），退为钉在最底的普通窗口，仍然可见；每次 TaskbarCreated 再试。</summary>
    Degraded,
}

/// <summary>
/// 把方框窗口嵌入桌面层，并在 Explorer 重启后自愈（TDD v1.0.13 §4.5）。
/// <list type="bullet">
/// <item>查找：<see cref="DesktopHostLocator"/> 经可注入的 <see cref="IDesktopWindowTree"/> 发 0x052C，按窗口结构选择旧层级 WorkerW 或新层级 Progman。</item>
/// <item>校验：SetParent 之后用 <see cref="EmbedValidator"/> 检查；失败就撤回挂载、退回钉在最底的兼容窗口。方框必须能看见。</item>
/// <item>自愈：每次 TaskbarCreated 都完整重跑「判断层级 → 挂接 → 校验」，从不沿用上一轮的父窗口句柄；兼容显示时同样重试。</item>
/// <item>位置：每次挂接都从数据库读取方框位置（<see cref="IFenceStore"/>）。</item>
/// </list>
/// 所有回调都在调用方的 UI 线程上执行（IShellNotifications 的实现负责封送）。
/// </summary>
public sealed class DesktopEmbedder : Startup.IStartupEmbedder, IDisposable
{
    private readonly IUnitOfWorkFactory _db;
    private readonly IFenceStore _fences;
    private readonly IDesktopWindowTree _tree;
    private readonly IDesktopWindowing _win;
    private readonly IMonitorService _monitors;
    private readonly IBoxWindows _windows;
    private readonly IShellNotifications _shell;
    private readonly IEmbedLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private bool _disposed;

    public DesktopEmbedder(IUnitOfWorkFactory db, IFenceStore fences, IDesktopWindowTree tree, IDesktopWindowing win, IMonitorService monitors,
        IBoxWindows windows, IShellNotifications shell, IEmbedLog log)
    {
        (_db, _fences, _tree, _win, _monitors, _windows, _shell, _log) = (
            db ?? throw new ArgumentNullException(nameof(db)),
            fences ?? throw new ArgumentNullException(nameof(fences)),
            tree ?? throw new ArgumentNullException(nameof(tree)),
            win ?? throw new ArgumentNullException(nameof(win)),
            monitors ?? throw new ArgumentNullException(nameof(monitors)),
            windows ?? throw new ArgumentNullException(nameof(windows)),
            shell ?? throw new ArgumentNullException(nameof(shell)),
            log ?? throw new ArgumentNullException(nameof(log)));
    }

    public EmbedMode Mode { get; private set; } = EmbedMode.None;

    /// <summary>本轮判断出的层级。</summary>
    public DesktopLayering Layering { get; private set; } = DesktopLayering.NotFound;

    /// <summary>本轮选中的父窗口（旧层级为 WorkerW，新层级为 Progman）；兼容显示时为 Zero。</summary>
    public IntPtr Host { get; private set; }

    /// <summary>最近一轮挂接的方框句柄。</summary>
    public IReadOnlyDictionary<Guid, IntPtr> Windows { get; private set; } = new Dictionary<Guid, IntPtr>();

    /// <summary>每轮挂接完成（含兼容显示）后触发。</summary>
    public event EventHandler<EmbedMode>? Attached;

    /// <summary>每轮挂接中出现异常（不外抛，方框留在兼容显示）。</summary>
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

    /// <summary>一轮完整挂接：丢弃旧句柄 → 重新判断层级 → 从数据库读位置 → 挂接、校验并放置每个方框。</summary>
    public async Task AttachAllAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            Host = IntPtr.Zero; // 旧句柄一律作废
            var fences = await _db.InTransactionAsync(u => _fences.ListAsync(u, ct), ct);
            var monitors = _monitors.GetMonitors();
            var host = DesktopHostLocator.Locate(_tree);
            var os = _tree.OsVersion;
            var all = host.Host != IntPtr.Zero;
            var handles = new Dictionary<Guid, IntPtr>();
            foreach (var fence in fences)
            {
                var hwnd = _windows.Ensure(fence);
                handles[fence.Id] = hwnd;
                var bounds = FenceLayout.Place(fence, monitors);
                var validation = host.Host == IntPtr.Zero
                    ? new EmbedValidation(false, "未找到桌面层")
                    : TryEmbed(hwnd, host, bounds, monitors);
                if (!validation.Ok)
                {
                    all = false;
                    if (_win.GetParent(hwnd) != IntPtr.Zero) _win.SetParent(hwnd, IntPtr.Zero);
                    _win.PinToBottom(hwnd);
                    _win.SetBounds(hwnd, bounds);
                }
                _log.EmbedAttempt(new EmbedReport(fence.Id, os, host.Layering,
                    host.Host == IntPtr.Zero ? "-" : DesktopHostLocator.ClassChain(_tree, host.Host),
                    validation.Detail, validation.Ok ? EmbedPath.Embedded : EmbedPath.Fallback));
            }
            Layering = host.Layering;
            Host = all ? host.Host : IntPtr.Zero;
            Windows = handles;
            Mode = all ? EmbedMode.Embedded : EmbedMode.Degraded;
        }
        finally
        {
            _gate.Release();
        }
        Attached?.Invoke(this, Mode);
    }

    private EmbedValidation TryEmbed(IntPtr hwnd, DesktopHost host, Domain.Geometry.ScreenRect bounds, IReadOnlyList<MonitorInfo> monitors)
    {
        _win.Unpin(hwnd);
        if (!_win.SetParent(hwnd, host.Host)) return new(false, "SetParent 失败");
        _win.SetBounds(hwnd, bounds);
        return EmbedValidator.Validate(_tree, hwnd, bounds, monitors, host.Progman);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started) _shell.TaskbarCreated -= OnTaskbarCreated;
        _gate.Dispose();
    }
}

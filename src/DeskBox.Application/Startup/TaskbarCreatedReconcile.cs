using DeskBox.Application.Ports;

namespace DeskBox.Application.Startup;

/// <summary>
/// 收到 TaskbarCreated 时再对账一次并刷新界面（TDD v1.0.14 §4.5.5 第 1 条）。
/// 事件处理是 async void：对账和刷新抛出的任何异常（含取消）都在这里接住并记日志，绝不外抛到消息循环。
/// 上一次还没跑完又来一次时不并发再跑，只在跑完后补跑一次。
/// </summary>
public sealed class TaskbarCreatedReconcile : IDisposable
{
    private readonly IShellNotifications _shell;
    private readonly IStartupReconcile _reconcile;
    private readonly Func<Task> _refresh;
    private readonly IDesktopScanLog _log;
    // 线程假设：TaskbarCreated 由 Win32ShellNotifications 在界面线程触发，HandleAsync 里的 await 续体也回到界面线程（单线程）。
    // 因此 _running / _pending 不会被并发改写。若改在线程池上触发，信号恰好落在 while 循环退出之后、_running 清零之前
    // 时会被丢掉（看到 _running==1 只置了 _pending，而循环已不再检查），届时需改用锁或在清零后再查一次 _pending。
    private int _running;

    // 运行期间来多少次信号都只记成一次待补跑（合并），跑完后最多补跑一次。
    private int _pending;

    public TaskbarCreatedReconcile(IShellNotifications shell, IStartupReconcile reconcile, Func<Task> refresh, IDesktopScanLog log)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _reconcile = reconcile ?? throw new ArgumentNullException(nameof(reconcile));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _shell.TaskbarCreated += OnTaskbarCreated;
    }

    private async void OnTaskbarCreated(object? sender, EventArgs e) => await HandleAsync();

    /// <summary>一次 TaskbarCreated 的完整处理；从不抛异常。</summary>
    public async Task HandleAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            Interlocked.Exchange(ref _pending, 1);
            return;
        }
        try
        {
            do
            {
                Interlocked.Exchange(ref _pending, 0);
                await RunOnceAsync();
            }
            while (Interlocked.Exchange(ref _pending, 0) == 1);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task RunOnceAsync()
    {
        try
        {
            await _reconcile.RunAsync();
            await _refresh();
        }
        catch (OperationCanceledException)
        {
            _log.DesktopScanSkipped("TaskbarCreated 对账已取消");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.DesktopReconcileFailed($"TaskbarCreated {FailureText.Describe(ex)}");
        }
    }

    public void Dispose() => _shell.TaskbarCreated -= OnTaskbarCreated;
}

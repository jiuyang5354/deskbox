namespace DeskBox.Infrastructure.Shell;

/// <summary>每 5 秒巡检一次 Progman 句柄，句柄变化即视为 Explorer 已重启。</summary>
public sealed class ExplorerWatcher : IDisposable
{
    private readonly Timer _timer;
    private IntPtr _last;

    public ExplorerWatcher(Func<IntPtr> probe, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _last = probe();
        _timer = new Timer(_ =>
        {
            var current = probe();
            if (current != IntPtr.Zero && current != _last)
            {
                _last = current;
                Restarted?.Invoke(this, EventArgs.Empty);
            }
        }, null, interval, interval);
    }

    public event EventHandler? Restarted;

    public void Dispose() => _timer.Dispose();
}

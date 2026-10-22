namespace DeskBox.Application.Startup;

/// <summary>界面线程调度器（WPF 实现为 Dispatcher.BeginInvoke）。</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>把主界面 / 方框窗口恢复、激活并置前（UI 层实现）。必须在界面线程调用。</summary>
public interface IWindowActivator
{
    void RestoreActivateAndBringToFront();
}

/// <summary>
/// 第二实例唤醒（TDD §4.7 第 6 条）：已运行实例收到单实例激活信号后，把置前操作封送到界面线程执行。
/// 信号可能在任意线程到达；这里从不直接调用 <see cref="IWindowActivator"/>，也不碰数据库。
/// </summary>
public sealed class InstanceActivation(IUiDispatcher dispatcher, IWindowActivator activator)
{
    public void OnSignal(object? sender, EventArgs e) => dispatcher.Post(activator.RestoreActivateAndBringToFront);
}

/// <summary>托盘通知（气泡，不抢焦点）。</summary>
public interface ITrayNotifier
{
    void ShowNotice(string text);
}

/// <summary>
/// 第二实例唤醒按嵌入状态区分（TDD v1.0.13 §4.5.3）：嵌入桌面时照旧恢复并置前；
/// 兼容显示时<b>不</b>激活、不把方框带到前面（否则会盖住别的窗口），只在托盘弹一次「DeskBox 已在运行」。
/// </summary>
public sealed class EmbedAwareActivator(IWindowActivator inner, Func<Desktop.EmbedMode> mode, ITrayNotifier tray) : IWindowActivator
{
    public const string AlreadyRunning = "DeskBox 已在运行";

    public void RestoreActivateAndBringToFront()
    {
        if (mode() == Desktop.EmbedMode.Degraded) tray.ShowNotice(AlreadyRunning);
        else inner.RestoreActivateAndBringToFront();
    }
}

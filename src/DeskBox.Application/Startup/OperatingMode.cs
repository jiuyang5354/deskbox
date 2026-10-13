namespace DeskBox.Application.Startup;

/// <summary>
/// 进程级运行模式。启动恢复失败后进入只读：<see cref="GatedFileMover"/> 拒绝一切移动，
/// 启动流程也不枚举桌面（不会产生由枚举触发的移动）。只读一旦进入，本次进程内不再退出。
/// </summary>
public sealed class OperatingMode
{
    private volatile string? _readOnlyReason;

    public bool IsReadOnly => _readOnlyReason is not null;

    public string? ReadOnlyReason => _readOnlyReason;

    public void EnterReadOnly(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _readOnlyReason ??= reason;
    }
}

/// <summary>只读模式下尝试移动文件。</summary>
public sealed class ReadOnlyModeException(string reason)
    : InvalidOperationException($"DeskBox 处于只读模式，不移动文件：{reason}");

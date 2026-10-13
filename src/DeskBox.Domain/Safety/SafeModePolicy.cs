namespace DeskBox.Domain.Safety;

/// <summary>P0：连续 2 次在启动后 30 秒内崩溃，下次启动进入安全模式（不接管系统图标）。</summary>
public sealed class SafeModePolicy
{
    public static readonly TimeSpan EarlyCrashWindow = TimeSpan.FromSeconds(30);
    public const int ConsecutiveEarlyCrashThreshold = 2;

    /// <summary>判断一次崩溃是否属于"启动后早期崩溃"。</summary>
    public static bool IsEarlyCrash(DateTimeOffset startedAt, DateTimeOffset crashedAt)
    {
        var uptime = crashedAt - startedAt;
        return uptime >= TimeSpan.Zero && uptime <= EarlyCrashWindow;
    }

    /// <summary>
    /// 按时间顺序给出历次运行记录（crashedAt 为 null 表示正常退出），
    /// 若最近连续的早期崩溃次数达到阈值则应进入安全模式。
    /// </summary>
    public static bool ShouldEnterSafeMode(IEnumerable<RunRecord> runsOldestFirst)
    {
        ArgumentNullException.ThrowIfNull(runsOldestFirst);
        var streak = 0;
        foreach (var run in runsOldestFirst)
            streak = run.CrashedAt is { } c && IsEarlyCrash(run.StartedAt, c) ? streak + 1 : 0;
        return streak >= ConsecutiveEarlyCrashThreshold;
    }
}

public sealed record RunRecord(DateTimeOffset StartedAt, DateTimeOffset? CrashedAt);

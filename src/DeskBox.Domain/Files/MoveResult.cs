namespace DeskBox.Domain.Files;

public enum SkipReason { Locked, ReadOnly, CrossVolume, CloudOnly, NeedsAdmin, NotFound, NameConflict, IoError,
    /// <summary>同一文件已有未完成（Pending）的移动，本次跳过；不是错误，也不影响那次移动。</summary>
    AlreadyInProgress }

/// <summary>文件移动结果（可辨识联合）：要么 Moved，要么 Skipped。绝不覆盖。</summary>
public abstract record MoveResult(string SourcePath)
{
    private MoveResult() : this(string.Empty) { }

    /// <summary>移动成功。Renamed 表示因目标重名而自动改名为 "name (n).ext"。</summary>
    public sealed record Moved(string SourcePath, string TargetPath, bool Renamed = false) : MoveResult(SourcePath);

    /// <summary>跳过。NativeErrorCode 为触发跳过的原始 Win32 错误码（预检查跳过时为 null）。</summary>
    public sealed record Skipped(string SourcePath, SkipReason Reason, int? NativeErrorCode = null) : MoveResult(SourcePath)
    {
        /// <summary>写入 Journal/日志的中止原因，例如 "NeedsAdmin (Win32 5)"。</summary>
        public string AbortReason => NativeErrorCode is { } code ? $"{Reason} (Win32 {code})" : Reason.ToString();
    }

    public T Match<T>(Func<Moved, T> moved, Func<Skipped, T> skipped) => this switch
    {
        Moved m => moved(m),
        Skipped s => skipped(s),
        _ => throw new InvalidOperationException("未知的 MoveResult 类型"),
    };
}

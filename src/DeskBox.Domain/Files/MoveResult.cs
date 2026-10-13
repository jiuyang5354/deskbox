namespace DeskBox.Domain.Files;

public enum SkipReason { Locked, ReadOnly, CrossVolume, CloudOnly, NeedsAdmin, NotFound, NameConflict, IoError }

/// <summary>文件移动结果（可辨识联合）：要么 Moved，要么 Skipped。绝不覆盖。</summary>
public abstract record MoveResult(string SourcePath)
{
    private MoveResult() : this(string.Empty) { }

    /// <summary>移动成功。Renamed 表示因目标重名而自动改名为 "name (n).ext"。</summary>
    public sealed record Moved(string SourcePath, string TargetPath, bool Renamed = false) : MoveResult(SourcePath);

    public sealed record Skipped(string SourcePath, SkipReason Reason) : MoveResult(SourcePath)
    {
        public string Message => Reason switch
        {
            SkipReason.Locked => "文件正在被占用",
            SkipReason.ReadOnly => "文件为只读",
            SkipReason.CrossVolume => "目标位于其他磁盘分区",
            SkipReason.CloudOnly => "文件仅在云端（OneDrive 占位符）",
            SkipReason.NeedsAdmin => "需要管理员权限",
            SkipReason.NotFound => "文件不存在",
            SkipReason.NameConflict => "重名文件过多（已尝试到 (999)）",
            SkipReason.IoError => "磁盘读写错误",
            _ => throw new ArgumentOutOfRangeException(nameof(Reason), Reason, null),
        };
    }

    public T Match<T>(Func<Moved, T> moved, Func<Skipped, T> skipped) => this switch
    {
        Moved m => moved(m),
        Skipped s => skipped(s),
        _ => throw new InvalidOperationException("未知的 MoveResult 类型"),
    };
}

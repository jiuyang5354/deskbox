using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeskBox.Application.Desktop;
using DeskBox.Application.Ports;
using DeskBox.Application.Startup;

namespace DeskBox.Infrastructure.Logging;

/// <summary>
/// 落盘日志（TDD v1.0.13 §6）：写 <c>%LocalAppData%\DeskBox\logs\deskbox.log</c>，单个文件超过 <see cref="MaxBytes"/> 就滚动，
/// 保留 <see cref="KeepRolled"/> 份旧文件（deskbox.1.log 最新 … deskbox.3.log 最旧）。线程安全；写失败不抛（日志不能把程序弄崩），
/// 失败次数记在 <see cref="WriteFailures"/>。必须先 <see cref="Open"/>。只记文件名和数量，不记文件内容。
/// </summary>
public sealed class RollingFileLog : IEmbedLog, IDesktopScanLog
{
    public const long DefaultMaxBytes = 5L * 1024 * 1024;
    public const int DefaultKeepRolled = 3;
    public const string BaseName = "deskbox";

    private readonly object _lock = new();
    private readonly TimeProvider _clock;

    public RollingFileLog(string directory, long maxBytes = DefaultMaxBytes, int keepRolled = DefaultKeepRolled, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(keepRolled);
        (Directory, MaxBytes, KeepRolled, _clock) = (directory, maxBytes, keepRolled, clock ?? TimeProvider.System);
    }

    public string Directory { get; }
    public long MaxBytes { get; }
    public int KeepRolled { get; }
    private int _writeFailures;
    private bool _open;

    public int WriteFailures
    {
        get { lock (_lock) return _writeFailures; }
    }

    /// <summary>是否已打开。打开之前的写入直接丢弃、不建目录不建文件（第二实例在拿到单实例锁之前不写日志）。</summary>
    public bool IsOpen
    {
        get { lock (_lock) return _open; }
    }

    /// <summary>拿到单实例锁之后调用。</summary>
    public void Open()
    {
        lock (_lock) _open = true;
    }

    public string CurrentPath => PathOf(0);

    public string PathOf(int index) => Path.Combine(Directory, index == 0 ? BaseName + ".log" : $"{BaseName}.{index}.log");

    public void Write(string level, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{_clock.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}");
        var bytes = Encoding.UTF8.GetBytes(line);
        lock (_lock)
        {
            if (!_open) return;
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var current = new FileInfo(CurrentPath);
                if (current.Exists && current.Length > 0 && current.Length + bytes.Length > MaxBytes) Roll();
                using var fs = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writeFailures++;
                Debug.WriteLine($"DeskBox 日志写入失败：{ex.Message}");
            }
        }
    }

    private void Roll()
    {
        if (KeepRolled == 0)
        {
            File.Delete(CurrentPath);
            return;
        }
        File.Delete(PathOf(KeepRolled));
        for (var i = KeepRolled - 1; i >= 0; i--)
            if (File.Exists(PathOf(i))) File.Move(PathOf(i), PathOf(i + 1));
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void EmbedAttempt(EmbedReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var msg = string.Create(CultureInfo.InvariantCulture,
            $"Embed fence={report.FenceId} os=[{report.Os}] layering={report.Layering} parent=[{report.ParentChain}] validation=[{report.Validation}] path={report.Path}");
        if (report.Path == EmbedPath.Embedded) Info(msg);
        else Warn(msg);
    }

    public void DesktopScanSkipped(string reason) => Warn($"DesktopScanSkipped reason=[{reason}]");

    public void DesktopScanStarted() => Info("DesktopScanStarted");

    public void DesktopScanCompleted(int found, int fenceItemRowsWritten, int pruned, bool pruneSkipped) =>
        Info(string.Create(CultureInfo.InvariantCulture,
            $"DesktopScanCompleted found={found} fenceItemRowsWritten={fenceItemRowsWritten} pruned={pruned} pruneSkipped={(pruneSkipped ? "true" : "false")}"));

    public void FenceItemPruned(string fileName) => Info($"FenceItemPruned name=[{fileName}]");

    public void DesktopScanPruneSkipped(string reason) => Warn($"DesktopScanPruneSkipped reason=[{reason}]");

    public void StagingFenceMissing() => Error("StagingFenceMissing: 启动对账时找不到暂存方框，本次对账未写入任何行（不在此处重建）");

    public void DesktopReconcileFailed(string error) => Error($"DesktopReconcileFailed {error}");
}

/// <summary>把现有的 Trace.TraceWarning / TraceError 等也写进同一个日志文件。</summary>
public sealed class RollingFileTraceListener(RollingFileLog log) : TraceListener
{
    private readonly RollingFileLog _log = log ?? throw new ArgumentNullException(nameof(log));

    public override void Write(string? message) => WriteLine(message);

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrEmpty(message)) _log.Info(message);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (Filter is not null && !Filter.ShouldTrace(eventCache, source, eventType, id, message, null, null, null)) return;
        _log.Write(eventType.ToString().ToUpperInvariant(), message ?? string.Empty);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
        TraceEvent(eventCache, source, eventType, id, args is { Length: > 0 } && format is not null
            ? string.Format(CultureInfo.InvariantCulture, format, args) : format);
}

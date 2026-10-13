using System.Diagnostics;
using DeskBox.Application.Desktop;
using DeskBox.Application.Ports;
using DeskBox.Infrastructure.Logging;

namespace DeskBox.Infrastructure.Tests;

/// <summary>落盘日志：%LocalAppData%\DeskBox\logs，单文件上限后滚动，保留 N 份（TDD v1.0.13 §6）。</summary>
public sealed class RollingFileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskbox-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Defaults_are_5mb_and_keep_3()
    {
        var log = new RollingFileLog(_dir);
        (log.MaxBytes, log.KeepRolled).Should().Be((5L * 1024 * 1024, 3));
        log.CurrentPath.Should().Be(Path.Combine(_dir, "deskbox.log"));
        log.PathOf(2).Should().Be(Path.Combine(_dir, "deskbox.2.log"));
    }

    [Fact]
    public void Creates_directory_and_appends_lines()
    {
        var log = new RollingFileLog(_dir);
        log.Info("hello");
        log.Warn("w");
        log.Error("e");
        var lines = File.ReadAllLines(log.CurrentPath);
        lines.Should().HaveCount(3);
        lines[0].Should().EndWith("[INFO] hello");
        lines[1].Should().EndWith("[WARN] w");
        lines[2].Should().EndWith("[ERROR] e");
        log.WriteFailures.Should().Be(0);
    }

    [Fact]
    public void Rolls_when_size_exceeded_and_keeps_only_three_old_files()
    {
        var log = new RollingFileLog(_dir, maxBytes: 200, keepRolled: 3);
        for (var i = 0; i < 40; i++) log.Info($"line {i:D3} " + new string('x', 60));

        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToList();
        files.Should().Equal("deskbox.1.log", "deskbox.2.log", "deskbox.3.log", "deskbox.log");
        foreach (var f in Directory.GetFiles(_dir)) new FileInfo(f).Length.Should().BeLessThanOrEqualTo(200);
        File.ReadAllText(log.CurrentPath).Should().Contain("line 039", "最新的写在当前文件");
        File.ReadAllText(log.PathOf(1)).Should().Contain("line 038", "deskbox.1.log 是上一份");
        Directory.GetFiles(_dir).SelectMany(File.ReadAllLines).Should().NotContain(l => l.Contains("line 000"), "最旧的已被丢弃");
    }

    [Fact]
    public void A_single_line_larger_than_the_limit_is_still_written()
    {
        var log = new RollingFileLog(_dir, maxBytes: 10, keepRolled: 1);
        log.Info(new string('a', 50));
        log.Info(new string('b', 50));
        File.ReadAllText(log.CurrentPath).Should().Contain(new string('b', 50));
        File.ReadAllText(log.PathOf(1)).Should().Contain(new string('a', 50));
    }

    [Fact]
    public void Keep_zero_discards_the_old_file()
    {
        var log = new RollingFileLog(_dir, maxBytes: 10, keepRolled: 0);
        log.Info("first line");
        log.Info("second line");
        Directory.GetFiles(_dir).Should().ContainSingle();
        File.ReadAllText(log.CurrentPath).Should().Contain("second").And.NotContain("first");
    }

    [Fact]
    public void Write_failure_is_counted_not_thrown()
    {
        File.WriteAllText(_dir, "not a directory"); // 目录路径被同名文件占用
        try
        {
            var log = new RollingFileLog(_dir);
            FluentActions.Invoking(() => log.Info("x")).Should().NotThrow();
            log.WriteFailures.Should().Be(1);
        }
        finally
        {
            File.Delete(_dir);
        }
    }

    [Fact]
    public void Embed_and_scan_lines_carry_layering_os_and_counts_only()
    {
        var log = new RollingFileLog(_dir);
        var id = Guid.NewGuid();
        log.EmbedAttempt(new EmbedReport(id, new OsVersionInfo("26200.9457", "25H2"), DesktopLayering.ProgmanChild, "Progman(0x10)", "ok", EmbedPath.Embedded));
        log.EmbedAttempt(new EmbedReport(id, new OsVersionInfo("26200.9457", "25H2"), DesktopLayering.Legacy, "WorkerW(0x20)", "父窗口不可见：WorkerW(0x20)", EmbedPath.Fallback));
        log.DesktopScanSkipped("1 条未完成的整理记录恢复失败");
        log.DesktopScanStarted();
        log.DesktopScanCompleted(12, 3, 0);

        var lines = File.ReadAllLines(log.CurrentPath);
        lines[0].Should().EndWith($"[INFO] Embed fence={id} os=[build 26200.9457, DisplayVersion 25H2] layering=ProgmanChild parent=[Progman(0x10)] validation=[ok] path=Embedded");
        lines[1].Should().Contain("[WARN] Embed").And.Contain("layering=Legacy").And.Contain("path=Fallback");
        lines[2].Should().EndWith("[WARN] DesktopScanSkipped reason=[1 条未完成的整理记录恢复失败]");
        lines[3].Should().EndWith("[INFO] DesktopScanStarted");
        lines[4].Should().EndWith("[INFO] DesktopScanCompleted found=15 user=12 public=3 fenceItemRowsWritten=0");
        FluentActions.Invoking(() => log.EmbedAttempt(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Trace_listener_forwards_trace_calls_into_the_file()
    {
        var log = new RollingFileLog(_dir);
        using var listener = new RollingFileTraceListener(log);
        var source = new TraceSource("t", SourceLevels.All);
        source.Listeners.Clear();
        source.Listeners.Add(listener);
        source.TraceEvent(TraceEventType.Warning, 1, "StagingFenceRecreated {0}", 42);
        source.TraceEvent(TraceEventType.Error, 2, "plain");
        listener.Write("w");
        listener.WriteLine("");
        listener.Filter = new EventTypeFilter(SourceLevels.Error);
        source.TraceEvent(TraceEventType.Warning, 3, "filtered");

        var lines = File.ReadAllLines(log.CurrentPath);
        lines.Should().HaveCount(3);
        lines[0].Should().EndWith("[WARNING] StagingFenceRecreated 42");
        lines[1].Should().EndWith("[ERROR] plain");
        lines[2].Should().EndWith("[INFO] w");
        FluentActions.Invoking(() => new RollingFileTraceListener(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Guards()
    {
        FluentActions.Invoking(() => new RollingFileLog(" ")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new RollingFileLog(_dir, maxBytes: 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new RollingFileLog(_dir, keepRolled: -1)).Should().Throw<ArgumentOutOfRangeException>();
    }
}

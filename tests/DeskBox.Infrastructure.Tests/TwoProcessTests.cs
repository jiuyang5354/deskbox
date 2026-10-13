using System.Diagnostics;
using DeskBox.SqliteWriter;

namespace DeskBox.Infrastructure.Tests;

/// <summary>两个进程同时写同一个数据库：不能出现 SQLITE_BUSY，也不能损坏。</summary>
public sealed class TwoProcessTests : IAsyncLifetime
{
    private const int PerProcess = 150;
    private readonly TempDb _t = new();
    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Concurrent_writers_in_two_processes_do_not_get_busy_or_corrupt()
    {
        var writerDll = Path.Combine(AppContext.BaseDirectory, "DeskBox.SqliteWriter.dll");
        File.Exists(writerDll).Should().BeTrue();
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
        var psi = new ProcessStartInfo(host) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { writerDll, _t.Path_, PerProcess.ToString(System.Globalization.CultureInfo.InvariantCulture), "AAAAAAAA" })
            psi.ArgumentList.Add(a);

        using var child = Process.Start(psi)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        await Writer.RunAsync(_t.Path_, PerProcess, "BBBBBBBB"); // 本进程同时写
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));

        (await stderr).Should().BeEmpty("子进程不应出现任何异常（包括 SQLITE_BUSY）");
        (await stdout).Should().Contain($"ok {PerProcess}");
        child.ExitCode.Should().Be(0);

        (await _t.Scalar<string>("PRAGMA integrity_check;")).Should().Be("ok");
        (await _t.Scalar<long>("SELECT COUNT(*) FROM journal WHERE status = 'Committed';")).Should().Be(2 * PerProcess);
        (await _t.Scalar<long>("SELECT COUNT(*) FROM vault_record;")).Should().Be(2 * PerProcess);
        (await _t.Scalar<long>("SELECT COUNT(DISTINCT substr(file_id, 1, 8)) FROM journal;")).Should().Be(2);
    }

    private ProcessStartInfo Child(params string[] args)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
        var psi = new ProcessStartInfo(host) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DeskBox.SqliteWriter.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    [Fact]
    public async Task Two_processes_beginning_the_same_file_yield_one_pending_and_one_already_in_progress()
    {
        const string fileId = "ABCDEF01:00000000000000000000000000000042";
        var go = Path.Combine(_t.Dir, "go");
        using var p1 = Process.Start(Child("begin", _t.Path_, fileId, go))!;
        using var p2 = Process.Start(Child("begin", _t.Path_, fileId, go))!;
        try
        {
            var timeout = TimeSpan.FromMinutes(2);
            (await p1.StandardOutput.ReadLineAsync().WaitAsync(timeout)).Should().Be("ready");
            (await p2.StandardOutput.ReadLineAsync().WaitAsync(timeout)).Should().Be("ready");
            await File.WriteAllTextAsync(go, "go"); // 两个进程同时开始 Begin

            var r1 = p1.StandardOutput.ReadLineAsync().WaitAsync(timeout);
            var r2 = p2.StandardOutput.ReadLineAsync().WaitAsync(timeout);
            var lines = new[] { await r1, await r2 };
            lines.Should().BeEquivalentTo(new[] { "attempt", "skipped AlreadyInProgress" });

            var loser = lines[0] == "attempt" ? p2 : p1;
            await loser.WaitForExitAsync().WaitAsync(timeout);
            loser.ExitCode.Should().Be(0);
            (await loser.StandardError.ReadToEndAsync()).Should().BeEmpty("跳过不是错误");
        }
        finally
        {
            foreach (var p in new[] { p1, p2 }) if (!p.HasExited) p.Kill(entireProcessTree: true); // 抢到的一方挂在移动前，模拟被杀
            await p1.WaitForExitAsync();
            await p2.WaitForExitAsync();
        }
        (await _t.Scalar<long>($"SELECT COUNT(*) FROM journal WHERE file_id = '{fileId}' AND status = 'Pending';")).Should().Be(1);
        (await _t.Scalar<long>("SELECT COUNT(*) FROM journal;")).Should().Be(1);
        (await _t.Scalar<string>("PRAGMA integrity_check;")).Should().Be("ok");
    }

    [Fact]
    public async Task Writer_usage_errors_return_nonzero()
    {
        (await Writer.Main([])).Should().Be(2);
        (await Writer.Main([Path.Combine(_t.Dir, "no", "x.db"), "1", "CCCCCCCC"])).Should().Be(1);
        (await Writer.Main(["begin", Path.Combine(_t.Dir, "no", "x.db"), "ABCDEF01:00000000000000000000000000000042", "go"])).Should().Be(1);
    }
}

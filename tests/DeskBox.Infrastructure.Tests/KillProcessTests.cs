using System.Diagnostics;

namespace DeskBox.Infrastructure.Tests;

/// <summary>
/// 杀进程耐久性：在真实 SQLite 上杀掉正在写 journal 的子进程，库必须仍可读、不损坏。
/// 打 WindowsIntegration，只在手动触发的 windows-integration 任务里跑（常规 CI 用 Category!= 排除）。
/// </summary>
[Trait("Category", "WindowsIntegration")]
public sealed class KillProcessTests : IAsyncLifetime
{
    private readonly TempDb _t = new();
    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private static ProcessStartInfo WriterPsi(params string[] args)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
        var psi = new ProcessStartInfo(host)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DeskBox.SqliteWriter.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// 子进程循环写 Pending→Commit；中途 Kill 后：integrity_check=ok，
    /// 每条 Committed 都有对应 vault_record，且不会出现「半条」破坏唯一索引后的库。
    /// </summary>
    [Fact]
    public async Task Kill_writer_mid_batch_keeps_integrity_and_committed_match_vault()
    {
        const int planned = 4000;
        // volume 必须是 8 位十六进制（FileId 校验）
        const string volume = "DEADBEEF";
        var writerDll = Path.Combine(AppContext.BaseDirectory, "DeskBox.SqliteWriter.dll");
        File.Exists(writerDll).Should().BeTrue("SqliteWriter 必须复制到测试输出目录");

        using var child = Process.Start(WriterPsi(_t.Path_, planned.ToString(System.Globalization.CultureInfo.InvariantCulture), volume))!;
        var stderrTask = child.StandardError.ReadToEndAsync();

        // 等库里出现一些 Committed，确认杀在写入过程中，而不是空库
        var deadline = DateTime.UtcNow.AddSeconds(30);
        long committed = 0;
        while (DateTime.UtcNow < deadline && !child.HasExited)
        {
            try { committed = await _t.Scalar<long>("SELECT COUNT(*) FROM journal WHERE status = 'Committed';"); }
            catch { /* 子进程可能正持有锁，稍后重试 */ }
            if (committed >= 20) break;
            await Task.Delay(50);
        }

        if (child.HasExited && committed == 0)
        {
            var err = await stderrTask;
            throw new Xunit.Sdk.XunitException(
                $"Writer 在写出任何 Committed 之前就退出了 (exit={child.ExitCode}). stderr:\n{err}");
        }

        committed.Should().BeGreaterThan(0, "杀之前至少应写进若干条，否则测试无效");

        if (!child.HasExited) child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // 杀完后重新开连接（清池），检查耐久性
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        (await _t.Scalar<string>("PRAGMA integrity_check;")).Should().Be("ok");

        var committedFinal = await _t.Scalar<long>("SELECT COUNT(*) FROM journal WHERE status = 'Committed';");
        var vault = await _t.Scalar<long>("SELECT COUNT(*) FROM vault_record;");
        var pending = await _t.Scalar<long>("SELECT COUNT(*) FROM journal WHERE status = 'Pending';");
        var total = await _t.Scalar<long>("SELECT COUNT(*) FROM journal;");

        vault.Should().Be(committedFinal, "每条 Committed 必须有一条 vault_record（同事务）");
        (pending + committedFinal).Should().Be(total, "journal 只有 Pending/Committed 两种收尾");
        total.Should().BeLessThan(planned, "进程被杀，不应写完全部计划条数");
        committedFinal.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Begin 挂在 MoveFileExW 之前留下 Pending；Kill 后 Pending 仍在，integrity_check=ok。
    /// </summary>
    [Fact]
    public async Task Kill_hanging_begin_leaves_one_pending_and_integrity_ok()
    {
        // FileId：8 位十六进制卷号 + ':' + 32 位十六进制
        const string fileId = "CAFEBABE:00000000000000000000000000000001";
        var go = Path.Combine(_t.Dir, "go-kill");
        using var child = Process.Start(WriterPsi("begin", _t.Path_, fileId, go))!;
        var stderrTask = child.StandardError.ReadToEndAsync();
        try
        {
            (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(1))).Should().Be("ready");
            await File.WriteAllTextAsync(go, "go");
            var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(1));
            if (line is null)
            {
                var err = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
                throw new Xunit.Sdk.XunitException($"期望 attempt，stdout 已 EOF。stderr:\n{err}");
            }
            line.Should().Be("attempt");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        (await _t.Scalar<string>("PRAGMA integrity_check;")).Should().Be("ok");
        (await _t.Scalar<long>($"SELECT COUNT(*) FROM journal WHERE file_id = '{fileId}' AND status = 'Pending';"))
            .Should().Be(1);
        (await _t.Scalar<long>("SELECT COUNT(*) FROM vault_record;")).Should().Be(0);
    }
}

using System.IO;
using System.Runtime.Versioning;
using DeskBox.App;

namespace DeskBox.Desktop.Tests;

/// <summary>
/// 方框界面交互状态截图（任务 5 评审要求）：悬停、可放下高亮、禁用原因提示、Esc 取消还原。
/// 输出到 DESKBOX_SCREENSHOT_DIR（windows-integration.yml 上传为 artifact）。WPF 渲染需要 STA 线程。
/// </summary>
[Trait("Category", "WindowsIntegration")]
[SupportedOSPlatform("windows")]
public sealed class FenceUiScreenshotTests
{
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("STA 渲染失败", error);
    }

    [Fact]
    public void Renders_one_png_per_interaction_state()
    {
        var dir = Environment.GetEnvironmentVariable("DESKBOX_SCREENSHOT_DIR") is { Length: > 0 } d
            ? d : Path.Combine(Path.GetTempPath(), "deskbox-screenshots");
        IReadOnlyList<string> files = [];
        RunSta(() => files = FenceUiPreview.RenderAll(dir));
        files.Select(Path.GetFileName).Should().Equal("01-hover.png", "02-drop-target.png", "03-disabled-reason.png", "04-esc-cancel.png");
        foreach (var f in files)
        {
            var bytes = File.ReadAllBytes(f);
            bytes.Length.Should().BeGreaterThan(1000, $"{f} 不应是空图");
            bytes.Take(8).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "PNG 文件头");
        }
    }

    [Fact]
    public void Esc_cancel_preview_produces_no_db_command()
    {
        object? cmd = new object();
        RunSta(() => FenceUiPreview.EscCancel(out cmd));
        cmd.Should().BeNull("Esc 取消后不写库");
    }
}

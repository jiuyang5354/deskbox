using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using DeskBox.Infrastructure.Startup;

namespace DeskBox.Desktop.Tests;

/// <summary>单实例锁名是写死的常量，与程序集名 / exe 名无关（App 程序集改名为 DeskBox 后新旧版本仍互斥）。不需要交互桌面，随 build-test 运行。</summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstanceNameTests
{
    [Fact]
    public void Prefix_is_a_compile_time_constant()
    {
        var field = typeof(NamedMutexSingleInstance).GetField(nameof(NamedMutexSingleInstance.NamePrefix), BindingFlags.Public | BindingFlags.Static)!;
        field.IsLiteral.Should().BeTrue("const，不是运行时从程序集或进程读出来的");
        NamedMutexSingleInstance.NamePrefix.Should().Be("DeskBox");
    }

    [Fact]
    public void Mutex_name_depends_only_on_prefix_and_user()
    {
        NamedMutexSingleInstance.DefaultName("S-1-5-21-1").Should().Be("DeskBox.S-1-5-21-1");
        using var s = new NamedMutexSingleInstance(null, NamedMutexSingleInstance.DefaultName("S-1-5-21-1"));
        s.MutexName.Should().Be(@"Local\DeskBox.S-1-5-21-1.mutex");
    }

    [Fact]
    public void App_assembly_is_DeskBox_but_mutex_name_does_not_use_it()
    {
        var app = typeof(DeskBox.App.App).Assembly.GetName().Name;
        app.Should().Be("DeskBox", "exe 为 DeskBox.exe");
        typeof(DeskBox.App.App).Namespace.Should().Be("DeskBox.App", "根命名空间不变");
        var name = NamedMutexSingleInstance.DefaultName("u");
        name.Should().NotContain(".App").And.NotContain(".exe");
        name.Should().NotContain(Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "testhost"));
    }
}

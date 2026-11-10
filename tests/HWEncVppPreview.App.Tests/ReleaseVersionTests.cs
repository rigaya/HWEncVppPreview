using System.Globalization;
using System.Reflection;
using HWEncVppPreview.App;

namespace HWEncVppPreview.App.Tests;

public sealed class ReleaseVersionTests
{
    [Fact]
    public void 表示する版は小数点以下二桁を保ち整数の版と対応する()
    {
        string version = ApplicationVersion.Value;
        Assert.Matches(@"^[0-9]+\.[0-9]{2}$", version);
        Assert.DoesNotContain("+", version);
        string[] parts = version.Split('.');
        var numeric = new Version(int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture), 0, 0);
        var assembly = typeof(App).Assembly;
        Assert.Equal(numeric, assembly.GetName().Version);
        Assert.Equal(numeric.ToString(), assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Equal(version, assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        Assert.Equal(version, typeof(HWEncVppPreview.Core.FrameCache).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        Assert.Equal("HWEncVppPreview", assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
        Assert.Equal("Copyright (c) 2026 rigaya", assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()!.Copyright);
    }

    [Fact]
    public void 版の引数は画面を初期化せず一行だけ標準出力へ返す()
    {
        var previousApplication = Avalonia.Application.Current;
        TextWriter previous = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Program.Main(["--version"]);
        }
        finally { Console.SetOut(previous); }
        Assert.Equal("HWEncVppPreview " + ApplicationVersion.Value + Environment.NewLine, output.ToString());
        Assert.Same(previousApplication, Avalonia.Application.Current);
    }
}

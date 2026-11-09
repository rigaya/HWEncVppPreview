using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class EncoderDiscoveryTests
{
    [Theory]
    [InlineData("QSVEncC64.exe", "QSVEncC64.exe", "NVEncC64.exe", "VCEEncC64.exe")]
    [InlineData("NVEncC64.exe", "NVEncC64.exe", "VCEEncC64.exe", "")]
    [InlineData("VCEEncC64.exe", "VCEEncC64.exe", "", "")]
    public void Windowsの同じフォルダでは指定された候補順を使う(string expected, params string[] available)
    {
        var files = available.Select(name => @"C:\Viewer\" + name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(@"C:\Viewer\" + expected, EncoderDiscovery.Find(@"C:\Viewer", EncoderPlatform.Windows,
            null, files.Contains, _ => throw new InvalidOperationException("Windowsでは実行権限を調べない")));
    }

    [Theory]
    [InlineData("qsvencc", "qsvencc", "nvencc", "vceencc", "rkmppenc")]
    [InlineData("nvencc", "nvencc", "vceencc", "rkmppenc", "")]
    [InlineData("vceencc", "vceencc", "rkmppenc", "", "")]
    [InlineData("rkmppenc", "rkmppenc", "", "", "")]
    public void Linuxの同じフォルダでは指定された候補順を使う(string expected, params string[] available)
    {
        var files = available.Select(name => "/viewer/" + name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal("/viewer/" + expected, EncoderDiscovery.Find("/viewer", EncoderPlatform.Linux,
            null, files.Contains, _ => true));
    }

    [Fact]
    public void 本体と同じフォルダは検索経路より先に使う()
    {
        var files = new HashSet<string> { "/viewer/nvencc", "/bin/qsvencc" };
        Assert.Equal("/viewer/nvencc", EncoderDiscovery.Find("/viewer", EncoderPlatform.Linux,
            "/bin", files.Contains, _ => true));
    }

    [Fact]
    public void 検索経路では候補名の優先順をフォルダ順より先に適用する()
    {
        var files = new HashSet<string> { "/first/nvencc", "/second/qsvencc" };
        Assert.Equal("/second/qsvencc", EncoderDiscovery.Find("/viewer", EncoderPlatform.Linux,
            "/first:/second", files.Contains, _ => true));
    }

    [Fact]
    public void 同じ候補名なら検索経路の先のフォルダを使う()
    {
        var files = new HashSet<string> { "/first/qsvencc", "/second/qsvencc" };
        Assert.Equal("/first/qsvencc", EncoderDiscovery.Find("/viewer", EncoderPlatform.Linux,
            "/first:/second", files.Contains, _ => true));
    }

    [Fact]
    public void Linuxでは存在しても実行権限がない候補を採らない()
    {
        var files = new HashSet<string> { "/viewer/qsvencc", "/viewer/nvencc" };
        Assert.Equal("/viewer/nvencc", EncoderDiscovery.Find("/viewer", EncoderPlatform.Linux,
            null, files.Contains, path => path.EndsWith("/nvencc", StringComparison.Ordinal)));
    }

    [Fact]
    public void 同じフォルダの候補が実行不能なら検索経路を続けて探す()
    {
        var files = new HashSet<string> { "/viewer/qsvencc", "/bin/qsvencc" };
        Assert.Equal("/bin/qsvencc", EncoderDiscovery.Find("/viewer", EncoderPlatform.Linux,
            "/bin", files.Contains, path => path.StartsWith("/bin/", StringComparison.Ordinal)));
    }

    [Fact]
    public void 存在しない候補の実行権限は調べない()
    {
        Assert.Null(EncoderDiscovery.Find("/viewer", EncoderPlatform.Linux, "/bin", _ => false,
            _ => throw new InvalidOperationException("存在しない候補の権限を調べない")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 空の検索経路全体はカレントディレクトリを追加しない(string? searchPath)
    {
        var visited = new List<string>();
        Assert.Null(EncoderDiscovery.Find("/viewer", EncoderPlatform.Linux, searchPath,
            path => { visited.Add(path); return false; }, _ => true));
        Assert.Equal(new[] { "/viewer/qsvencc", "/viewer/nvencc", "/viewer/vceencc", "/viewer/rkmppenc" }, visited);
    }

    [Theory]
    [InlineData("/first::/last", "/first", "/last")]
    [InlineData("relative/bin:/last", "/last")]
    [InlineData(".:/last", "/last")]
    [InlineData("/bin:", "/bin")]
    public void 空と相対の検索経路要素は存在確認もしない(string searchPath, params string[] directories)
    {
        var visited = new List<string>();
        Assert.Null(EncoderDiscovery.Find("/viewer", EncoderPlatform.Linux, searchPath,
            path => { visited.Add(path); return false; },
            _ => throw new InvalidOperationException("存在しない候補の権限を調べない")));
        string[] expected = new[] { "qsvencc", "nvencc", "vceencc", "rkmppenc" }
            .SelectMany(name => directories.Select(directory => directory + "/" + name)).ToArray();
        Assert.Equal(new[] { "/viewer/qsvencc", "/viewer/nvencc", "/viewer/vceencc", "/viewer/rkmppenc" }
            .Concat(expected), visited);
    }

    [Fact]
    public void フォルダ名の空白は削らない()
    {
        Assert.Equal("/tools with spaces/nvencc", EncoderDiscovery.Find("/viewer", EncoderPlatform.Linux,
            "/tools with spaces", path => path == "/tools with spaces/nvencc", _ => true));
    }

    [Theory]
    [InlineData("/", "/qsvencc")]
    [InlineData("/viewer/", "/viewer/qsvencc")]
    public void Linuxの末尾区切りを重ねない(string directory, string expected)
    {
        Assert.Equal(expected, EncoderDiscovery.Find(directory, EncoderPlatform.Linux,
            null, path => path == expected, _ => true));
    }

    [Theory]
    [InlineData(@"C:\Viewer\", @"C:\Viewer\QSVEncC64.exe")]
    [InlineData("C:/Viewer/", @"C:/Viewer\QSVEncC64.exe")]
    public void Windowsの末尾区切りを重ねない(string directory, string expected)
    {
        Assert.Equal(expected, EncoderDiscovery.Find(directory, EncoderPlatform.Windows,
            null, path => path == expected, _ => true));
    }

    [Fact]
    public void Windowsでは検索経路と32bit版とrkmppencを探さない()
    {
        var visited = new List<string>();
        Assert.Null(EncoderDiscovery.Find(@"C:\Viewer", EncoderPlatform.Windows, @"C:\Encoders",
            path => { visited.Add(path); return path is @"C:\Viewer\QSVEncC.exe" or @"C:\Viewer\rkmppenc.exe" or @"C:\Encoders\QSVEncC64.exe"; }, _ => true));
        Assert.Equal(new[] { @"C:\Viewer\QSVEncC64.exe", @"C:\Viewer\NVEncC64.exe", @"C:\Viewer\VCEEncC64.exe" }, visited);
    }

    [Fact]
    public void 対象外の環境では自動取得しない()
    {
        Assert.Null(EncoderDiscovery.Find("/viewer", EncoderPlatform.Other, "/bin",
            _ => throw new InvalidOperationException("対象外の環境では探索しない"), _ => true));
    }

    [Fact]
    public void 基底フォルダ未指定ならLinuxの検索経路だけを調べる()
    {
        var visited = new List<string>();
        Assert.Null(EncoderDiscovery.Find("", EncoderPlatform.Linux, "/bin",
            path => { visited.Add(path); return false; }, _ => true));
        Assert.Equal(new[] { "/bin/qsvencc", "/bin/nvencc", "/bin/vceencc", "/bin/rkmppenc" }, visited);
    }
}

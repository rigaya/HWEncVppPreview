using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public class OptionTextParserTests
{
    [Fact]
    public void 基本の分割()
    {
        Assert.Equal(new[] { "--vpp-nlmeans", "sigma=0.005,h=0.05" },
            OptionTextParser.TokenizeLine("--vpp-nlmeans sigma=0.005,h=0.05"));
    }

    [Fact]
    public void 連続する空白とタブと前後の空白()
    {
        Assert.Equal(new[] { "a", "b", "c" }, OptionTextParser.TokenizeLine("  a \t b   c  "));
    }

    [Fact]
    public void クォートで空白を含むパス()
    {
        Assert.Equal(new[] { "--vpp-subburn", @"file=C:\My Videos\a b.ass" },
            OptionTextParser.TokenizeLine(@"--vpp-subburn ""file=C:\My Videos\a b.ass"""));
    }

    [Fact]
    public void 引数の途中にあるクォート()
    {
        Assert.Equal(new[] { "x=a b", "c" }, OptionTextParser.TokenizeLine(@"x=""a b"" c"));
    }

    [Fact]
    public void クォート内の二重クォートは1文字()
    {
        Assert.Equal(new[] { @"say""hi""" }, OptionTextParser.TokenizeLine(@"""say""""hi"""""));
        Assert.Equal(new[] { @"a""b" }, OptionTextParser.TokenizeLine(@"""a""""b"""));
    }

    [Fact]
    public void 空のクォートは空文字の引数()
    {
        Assert.Equal(new[] { "a", "", "b" }, OptionTextParser.TokenizeLine(@"a """" b"));
    }

    [Fact]
    public void 閉じていないクォートは行末まで1引数()
    {
        Assert.Equal(new[] { "a", "b c" }, OptionTextParser.TokenizeLine(@"a ""b c"));
    }

    [Fact]
    public void バックスラッシュはエスケープではない()
    {
        Assert.Equal(new[] { @"C:\dir\file.txt" }, OptionTextParser.TokenizeLine(@"C:\dir\file.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# --vpp-nlmeans")]
    [InlineData("   # indented comment")]
    public void 空行とコメントは無視(string line)
    {
        Assert.Empty(OptionTextParser.TokenizeLine(line));
    }

    [Fact]
    public void 行の途中のシャープは引数の一部()
    {
        Assert.Equal(new[] { "a", "b#c", "#d" }, OptionTextParser.TokenizeLine("a b#c #d"));
    }

    [Fact]
    public void 無効な行は含めない()
    {
        var args = OptionTextParser.Parse(new[]
        {
            new OptionLine("--vpp-nlmeans sigma=0.01"),
            new OptionLine("--vpp-knn radius=3", Enabled: false),
            new OptionLine(""),
            new OptionLine("# memo"),
            new OptionLine("--vpp-resize spline36"),
        });
        Assert.Equal(new[] { "--vpp-nlmeans", "sigma=0.01", "--vpp-resize", "spline36" }, args);
    }

    [Fact]
    public void 全部無効なら空配列()
    {
        Assert.Empty(OptionTextParser.Parse(new[] { new OptionLine("--a", false) }));
        Assert.Empty(OptionTextParser.Parse(Array.Empty<OptionLine>()));
    }

    [Fact]
    public void ParseTextはCRLFとLFの両方を扱う()
    {
        Assert.Equal(new[] { "a", "b", "c" }, OptionTextParser.ParseText("a\r\nb\nc\r\n"));
    }
}

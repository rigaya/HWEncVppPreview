using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public class CommandBuilderTests
{
    private static PreviewRequest Req(double t, double mb = 2.0, double ma = 0.5, double fps = 30.0) => new()
    {
        EncoderPath = "enc",
        InputFile = @"D:\my videos\in.ts",
        TargetSeconds = t,
        PreMarginSeconds = mb,
        PostMarginSeconds = ma,
        Fps = fps,
        VppArgs = new[] { "--vpp-nlmeans", "d=1" },
    };

    [Fact]
    public void Tが十分大きい場合()
    {
        var req = Req(100.0);
        var plan = CommandBuilder.Plan(req);
        Assert.Equal(98.0, plan.SeekSeconds);
        Assert.Equal(2.0, plan.ActualPreMargin);
        Assert.Equal(76, plan.FrameCount); // ceil(2.5*30)+1

        Assert.Equal(new[]
        {
            "--avhw", "-i", @"D:\my videos\in.ts", "--seek", "0:01:38.000", "--frames", "76",
            "-c", "raw", "--y4m-timestamp", "-o", "-", "--vpp-nlmeans", "d=1",
        }, CommandBuilder.BuildOneShot(req));
    }

    [Fact]
    public void 常駐用は出力関係を含まない()
    {
        Assert.Equal(new[]
        {
            "--avhw", "-i", @"D:\my videos\in.ts", "--seek", "0:01:38.000", "--frames", "76",
            "--vpp-nlmeans", "d=1",
        }, CommandBuilder.BuildServer(Req(100.0)));
    }

    [Fact]
    public void T0ではseekを付けない()
    {
        var req = Req(0.0);
        var plan = CommandBuilder.Plan(req);
        Assert.Null(plan.SeekSeconds);
        Assert.Equal(0.0, plan.ActualPreMargin);
        Assert.Equal(16, plan.FrameCount); // ceil(0.5*30)+1
        Assert.DoesNotContain("--seek", CommandBuilder.BuildOneShot(req));
    }

    [Fact]
    public void TがMb未満なら前マージンはTで先頭から()
    {
        var req = Req(1.2);
        var plan = CommandBuilder.Plan(req);
        Assert.Null(plan.SeekSeconds);
        Assert.Equal(1.2, plan.ActualPreMargin);
        Assert.Equal(52, plan.FrameCount); // ceil(1.7*30)=51 → +1
        Assert.DoesNotContain("--seek", CommandBuilder.BuildServer(req));
    }

    [Fact]
    public void TがMbちょうどならseekなし()
    {
        var plan = CommandBuilder.Plan(Req(2.0));
        Assert.Null(plan.SeekSeconds);
        Assert.Equal(2.0, plan.ActualPreMargin);
    }

    [Theory]
    [InlineData(3600.0, "1:00:00.000")]
    [InlineData(5398.0, "1:29:58.000")]
    [InlineData(359999.5, "99:59:59.500")]
    [InlineData(360000.0, "100:00:00.000")]
    [InlineData(59.9996, "0:01:00.000")]   // ms への丸めで繰り上がる
    [InlineData(0.0, "0:00:00.000")]
    [InlineData(61.25, "0:01:01.250")]
    public void seek時刻の書式(double sec, string expected)
    {
        Assert.Equal(expected, CommandBuilder.FormatSeek(sec));
    }

    [Fact]
    public void 時刻の書式は現在のカルチャに依存しない()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("0:01:01.250", CommandBuilder.FormatSeek(61.25));
            Assert.Contains("0:01:38.000", CommandBuilder.BuildOneShot(Req(100.0)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void ActualPreMarginはms丸め後のseekと整合する()
    {
        var plan = CommandBuilder.Plan(Req(100.0004, mb: 2.0));
        Assert.Equal(98.0, plan.SeekSeconds);
        Assert.Equal(100.0004 - 98.0, plan.ActualPreMargin, 9);
    }

    [Fact]
    public void frames算出は浮動小数の誤差で増えない()
    {
        // (0.7+0.1)*30 は 24.000000000000004 になるが、ceil で 25 にならず 24+1 になる
        Assert.Equal(25, CommandBuilder.Plan(Req(100.0, mb: 0.7, ma: 0.1)).FrameCount);
    }

    [Fact]
    public void 入力の追加引数を差し替えられる()
    {
        var req = Req(100.0) with { InputArgs = new[] { "--avsw", "--input-thread", "1" } };
        Assert.Equal(new[] { "--avsw", "--input-thread", "1", "-i" }, CommandBuilder.BuildServer(req)[..4]);
    }

    [Fact]
    public void 不正な値は例外()
    {
        Assert.Throws<ArgumentException>(() => CommandBuilder.Plan(Req(1.0, fps: 0)));
        Assert.Throws<ArgumentException>(() => CommandBuilder.Plan(Req(-1.0)));
        Assert.Throws<ArgumentException>(() => CommandBuilder.Plan(Req(1.0, mb: -1)));
    }
}

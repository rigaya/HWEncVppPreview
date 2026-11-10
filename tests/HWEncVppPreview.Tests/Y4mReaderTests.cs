using HWEncVppPreview.Core;
using static HWEncVppPreview.Tests.TestUtil;

namespace HWEncVppPreview.Tests;

public class Y4mReaderTests
{
    private static Y4mReader Open(byte[] data) => new(new MemoryStream(data));

    [Theory]
    [InlineData("420jpeg", Y4mChroma.C420, 8, 4 * 2 * 3 / 2 * 1 + 0)]
    [InlineData("420mpeg2", Y4mChroma.C420, 8, 12)]
    [InlineData("420paldv", Y4mChroma.C420, 8, 12)]
    [InlineData("420", Y4mChroma.C420, 8, 12)]
    [InlineData("420p10", Y4mChroma.C420, 10, 24)]
    [InlineData("420p16", Y4mChroma.C420, 16, 24)]
    [InlineData("422", Y4mChroma.C422, 8, 16)]
    [InlineData("422p12", Y4mChroma.C422, 12, 32)]
    [InlineData("444", Y4mChroma.C444, 8, 24)]
    [InlineData("444p10", Y4mChroma.C444, 10, 48)]
    [InlineData("mono", Y4mChroma.Mono, 8, 8)]
    [InlineData("mono12", Y4mChroma.Mono, 12, 16)]
    public void カラースペースごとのフレームサイズ(string c, Y4mChroma chroma, int depth, int frameBytes)
    {
        // 4x2 の画像
        var r = Open(Concat(Header(4, 2, c), Frame(frameBytes, fill: 7)));
        Assert.Equal(chroma, r.Header.Chroma);
        Assert.Equal(depth, r.Header.BitDepth);
        Assert.Equal(frameBytes, r.Header.FrameBytes);
        var f = r.ReadFrame();
        Assert.NotNull(f);
        Assert.Equal(frameBytes, f.Data.Length);
        Assert.All(f.Data, b => Assert.Equal(7, b));
        Assert.Null(r.ReadFrame());
    }

    [Fact]
    public void 奇数サイズの420はクロマを切り上げる()
    {
        var h = Y4mHeader.Parse("YUV4MPEG2 W5 H3 F30:1 C420jpeg");
        Assert.Equal(5 * 3 + 2 * 3 * 2, h.FrameBytes);
    }

    [Fact]
    public void ヘッダの各タグと拡張タグ()
    {
        var h = Y4mHeader.Parse("YUV4MPEG2 W64 H48 F30000:1001 It A4:3 XTIMEBASE=1:120 XYSCSS=420JPEG XFLAG C420p10");
        Assert.Equal(64, h.Width);
        Assert.Equal(48, h.Height);
        Assert.Equal(30000, h.FpsNum);
        Assert.Equal(1001, h.FpsDen);
        Assert.Equal(30000.0 / 1001, h.Fps, 9);
        Assert.Equal('t', h.Interlace);
        Assert.Equal(4, h.SarNum);
        Assert.Equal(3, h.SarDen);
        Assert.Equal("1:120", h.ExtTags["XTIMEBASE"]);
        Assert.Equal("420JPEG", h.ExtTags["XYSCSS"]);
        Assert.Equal("", h.ExtTags["XFLAG"]);
        Assert.Equal("420p10", h.ColorSpaceTag);
    }

    [Fact]
    public void Cタグ省略時は420jpeg扱い()
    {
        var h = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F1:1");
        Assert.Equal(Y4mChroma.C420, h.Chroma);
        Assert.Equal(8, h.BitDepth);
    }

    [Fact]
    public void XtsとXdurを読む()
    {
        var r = Open(Concat(Header(2, 2, "420jpeg"),
            Frame(6, "FRAME Xts=0.000000 Xdur=0.033333"),
            Frame(6, "FRAME Xts=1234.5"),
            Frame(6)));
        var f0 = r.ReadFrame()!;
        Assert.Equal(0.0, f0.Xts);
        Assert.Equal(0.033333, f0.Xdur);
        var f1 = r.ReadFrame()!;
        Assert.Equal(1234.5, f1.Xts);
        Assert.Null(f1.Xdur);
        var f2 = r.ReadFrame()!;
        Assert.Null(f2.Xts);
        Assert.Null(r.ReadFrame());
    }

    [Fact]
    public void 十進数のカンマ区切りロケールでも小数をInvariantで読む()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var r = Open(Concat(Header(2, 2, "420jpeg"), Frame(6, "FRAME Xts=1.5 Xdur=0.25")));
            var f = r.ReadFrame()!;
            Assert.Equal(1.5, f.Xts);
            Assert.Equal(0.25, f.Xdur);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void 零フレーム()
    {
        var r = Open(Concat(Header(2, 2, "420jpeg")));
        Assert.Null(r.ReadFrame());
    }

    [Fact]
    public void 途中で切れたフレームは専用例外()
    {
        var data = Concat(Header(2, 2, "420jpeg"), Frame(6, "FRAME Xts=0.5"), Frame(6, "FRAME Xts=1.0")[..^2]);
        var r = Open(data);
        Assert.NotNull(r.ReadFrame());
        var ex = Assert.Throws<Y4mTruncatedException>(() => r.ReadFrame());
        Assert.Equal(1.0, ex.Xts);
        Assert.Equal(6, ex.ExpectedBytes);
        Assert.Equal(4, ex.ActualBytes);
    }

    [Fact]
    public void 細切れに届くStreamでも読める()
    {
        var data = Concat(Header(2, 2, "420jpeg"), Frame(6, "FRAME Xts=0.5", 9), Frame(6, "FRAME Xts=1.0", 8));
        var r = new Y4mReader(new TrickleStream(data));
        Assert.Equal(9, r.ReadFrame()!.Data[0]);
        Assert.Equal(8, r.ReadFrame()!.Data[5]);
        Assert.Null(r.ReadFrame());
    }

    [Theory]
    [InlineData("")]
    [InlineData("HELLO W2 H2\n")]
    [InlineData("YUV4MPEG2 W0 H2 C420\n")]
    [InlineData("YUV4MPEG2 H2 C420\n")]
    [InlineData("YUV4MPEG2 W2 H2 Cfoo\n")]
    [InlineData("YUV4MPEG2 W2 H2 C420p99\n")]
    [InlineData("YUV4MPEG2 Wabc H2\n")]
    [InlineData("YUV4MPEG2 W2 H2 F30\n")]
    public void 壊れたヘッダは例外(string head)
    {
        Assert.Throws<Y4mFormatException>(() => Open(Concat(head)));
    }

    [Fact]
    public void FRAME以外の行は例外()
    {
        var r = Open(Concat(Header(2, 2, "420jpeg"), "GARBAGE\n"));
        Assert.Throws<Y4mFormatException>(() => r.ReadFrame());
    }

    [Fact]
    public void 実物のy4mを読める()
    {
        // tests/Data/sample.y4m の作成コマンド (NVEnc / Linux)。stdout 経由にしないと --y4m-timestamp が効かない:
        //   nvencc --avsw -i sakura_op_h264_720p_short.mp4 --frames 5 --vpp-resize spline36 --output-res 64x64 \
        //          -c raw --y4m-timestamp -o - --log-level error > sample.y4m
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "sample.y4m");
        using var fs = File.OpenRead(path);
        var r = new Y4mReader(fs);
        Assert.Equal(64, r.Header.Width);
        Assert.Equal(64, r.Header.Height);
        Assert.Equal(Y4mChroma.C420, r.Header.Chroma);
        Assert.Equal(30.0, r.Header.Fps);
        Assert.Equal("1:120", r.Header.ExtTags["XTIMEBASE"]);

        var xts = new List<double>();
        Y4mFrame? f;
        while ((f = r.ReadFrame()) != null)
        {
            Assert.Equal(64 * 64 * 3 / 2, f.Data.Length);
            Assert.NotNull(f.Xdur);
            xts.Add(f.Xts!.Value);
        }
        Assert.Equal(5, xts.Count);
        for (int i = 0; i < xts.Count; i++)
        {
            Assert.Equal(i / 30.0, xts[i], 3);
        }
    }
}

using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class YuvColorConversionTests
{
    [Theory]
    [InlineData("smpte170m", "LIMITED", 142, 47, 181)]
    [InlineData("smpte170m", "FULL", 139, 55, 173)]
    [InlineData("bt709", "LIMITED", 144, 65, 191)]
    [InlineData("bt709", "FULL", 141, 72, 182)]
    [InlineData("bt2020nc", "LIMITED", 145, 60, 185)]
    [InlineData("bt2020nc", "FULL", 141, 67, 177)]
    [InlineData("smpte240m", "LIMITED", 144, 64, 191)]
    [InlineData("smpte240m", "FULL", 140, 70, 182)]
    public void MatrixAndRangeUseKnownNonclippedReferenceValues(string matrix, string range, int blue, int green, int red)
    {
        var header = Header(2, 2, Y4mChroma.C444, 8, matrix, range, "left");
        byte[] converted = YuvToBgra.Convert(Frame(header, (_, _) => 100, (_, _) => 150, (_, _) => 180));
        Assert.Equal(new byte[] { (byte)blue, (byte)green, (byte)red, 255 }, converted[..4]);
    }

    [Theory]
    [InlineData("smpte170m", 81, 90, 240, 255, 0, 0)]
    [InlineData("bt709", 63, 102, 240, 255, 0, 0)]
    [InlineData("bt2020nc", 74, 97, 240, 255, 0, 0)]
    [InlineData("smpte240m", 62, 102, 240, 255, 0, 0)]
    public void ExplicitMatrixOverridesResolutionForPrimaryColorBars(string matrix, int y, int cb, int cr, int red, int green, int blue)
    {
        var header = Header(3, 1, Y4mChroma.C444, 8, matrix, "LIMITED", "left");
        byte[] converted = YuvToBgra.Convert(Frame(header, (_, _) => y, (_, _) => cb, (_, _) => cr));
        Assert.InRange(converted[0], Math.Max(0, blue - 1), Math.Min(255, blue + 1));
        Assert.InRange(converted[1], Math.Max(0, green - 1), Math.Min(255, green + 1));
        Assert.InRange(converted[2], Math.Max(0, red - 1), Math.Min(255, red + 1));
    }

    public static IEnumerable<object[]> FullFormats()
    {
        foreach (var chroma in Enum.GetValues<Y4mChroma>())
        foreach (int depth in new[] { 8, 9, 10, 12, 16 }) yield return new object[] { chroma, depth };
    }

    [Theory]
    [MemberData(nameof(FullFormats))]
    public void FullBlackAndWhiteUseTheEntireCodeRange(Y4mChroma chroma, int depth)
    {
        int neutral = 1 << (depth - 1), maximum = (1 << depth) - 1;
        var header = Header(3, 3, chroma, depth, "bt709", "FULL", "center");
        foreach (int level in new[] { 0, maximum })
        {
            var frame = Frame(header, (_, _) => level, (_, _) => neutral, (_, _) => neutral);
            byte[] original = (byte[])frame.Data.Clone();
            byte[] converted = YuvToBgra.Convert(frame);
            for (int i = 0; i < converted.Length; i += 4)
            {
                byte expected = level == 0 ? (byte)0 : (byte)255;
                Assert.Equal(new byte[] { expected, expected, expected, 255 }, converted[i..(i + 4)]);
            }
            Assert.Equal(original, frame.Data);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(16)]
    public void FullHighDepthNonneutralColorsUseTheFullChromaDenominator(int depth)
    {
        int scale = 1 << (depth - 8);
        int y = 100 * scale + 1, cb = 150 * scale + scale / 2, cr = 180 * scale + 3;
        var header = Header(2, 1, Y4mChroma.C444, depth, "bt2020nc", "FULL", "left");
        byte[] converted = YuvToBgra.Convert(Frame(header, (_, _) => y, (_, _) => cb, (_, _) => cr));
        byte[] expected = ReferenceRgb(y, cb, cr, depth, true, 0.2627, 0.0593);
        for (int channel = 0; channel < 4; ++channel)
            Assert.InRange((int)converted[channel], Math.Max(0, expected[channel] - 1), Math.Min(255, expected[channel] + 1));
    }

    public static IEnumerable<object[]> InterpolatedFormats()
    {
        foreach (var chroma in Enum.GetValues<Y4mChroma>())
        foreach (int depth in new[] { 8, 10, 16 })
        foreach (string position in new[] { "left", "center", "topleft" })
            yield return new object[] { chroma, depth, position };
    }

    [Theory]
    [MemberData(nameof(InterpolatedFormats))]
    public void ChromaInterpolationMatchesIndependentCoordinatesIncludingOddEdges(Y4mChroma chroma, int depth, string position)
    {
        int scale = 1 << (depth - 8);
        var header = Header(5, 5, chroma, depth, "bt709", "LIMITED", position);
        int Y(int x, int y) => (90 + x + 2 * y) * scale + scale / 3;
        int U(int x, int y) => (102 + x * 13 + y * 4) * scale + scale / 2;
        int V(int x, int y) => (145 - x * 7 - y * 5) * scale + scale / 3;
        byte[] converted = YuvToBgra.Convert(Frame(header, Y, U, V));
        for (int y = 0; y < header.Height; ++y)
        for (int x = 0; x < header.Width; ++x)
        {
            double cb = chroma == Y4mChroma.Mono ? 128 * scale : ReferenceChroma(header, x, y, position, U);
            double cr = chroma == Y4mChroma.Mono ? 128 * scale : ReferenceChroma(header, x, y, position, V);
            byte[] expected = ReferenceRgb(Y(x, y), cb, cr, depth, false, 0.2126, 0.0722);
            int offset = (y * header.Width + x) * 4;
            for (int channel = 0; channel < 4; ++channel)
                Assert.InRange((int)converted[offset + channel], Math.Max(0, expected[channel] - 1), Math.Min(255, expected[channel] + 1));
        }
    }

    [Fact]
    public void ChromaLocationChangesHorizontalAndVerticalPhase()
    {
        byte[] Convert(string position)
        {
            var header = Header(4, 4, Y4mChroma.C420, 8, "bt601", "LIMITED", position);
            return YuvToBgra.Convert(Frame(header, (_, _) => 126, (x, y) => 110 + x * 20 + y * 10, (_, _) => 128));
        }
        var left = Convert("left");
        var center = Convert("center");
        var topLeft = Convert("topleft");
        // 左端以外の横方向と上端以外の縦方向で、位相の違いが実際の画素へ反映される。
        Assert.NotEqual(left[4], center[4]);
        Assert.NotEqual(left[4 * 4], topLeft[4 * 4]);
    }

    [Theory]
    [InlineData("420jpeg", "center")]
    [InlineData("420mpeg2", "left")]
    [InlineData("420p10", "left")]
    [InlineData("420paldv", "left")]
    public void MissingOrUnknownChromaLocationUsesTheColorSpaceTag(string tag, string expected)
    {
        var header = Y4mHeader.Parse("YUV4MPEG2 W8 H8 F30:1 C" + tag + " XCHROMALOC=unknown");
        Assert.Equal(expected, YuvToBgra.GetColorInfo(header).ChromaLocation);
    }

    [Fact]
    public void MissingAndUnsupportedMetadataKeepThePreviousMatrixAndRangeGuess()
    {
        foreach (string extra in new[] { "", " XCOLORMATRIX=bt2020c XCOLORRANGE=unknown" })
        {
            var sd = Y4mHeader.Parse("YUV4MPEG2 W1024 H576 C420jpeg" + extra);
            var hd = Y4mHeader.Parse("YUV4MPEG2 W1025 H576 C420jpeg" + extra);
            Assert.Equal("BT.601", YuvToBgra.GetColorInfo(sd).Matrix);
            Assert.Equal("BT.709", YuvToBgra.GetColorInfo(hd).Matrix);
            Assert.Equal("limited", YuvToBgra.GetColorInfo(sd).Range);
        }
    }

    [Theory]
    [InlineData("smpte2084", true)]
    [InlineData("arib-std-b67", true)]
    [InlineData("PQ", true)]
    [InlineData("HLG", true)]
    [InlineData("bt709", false)]
    [InlineData("unknown", false)]
    public void HdrTransferSelectsToneMappingOnlyForPqAndHlg(string transfer, bool expected)
    {
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 C444 XTRANSFER=" + transfer);
        Assert.Equal(expected, YuvToBgra.GetColorInfo(header).IsHdr);
        var plain = Y4mHeader.Parse("YUV4MPEG2 W2 H2 C444");
        byte[] legacy = YuvToBgra.Convert(Frame(plain, (_, _) => 100, (_, _) => 150, (_, _) => 180));
        byte[] converted = YuvToBgra.Convert(Frame(header, (_, _) => 100, (_, _) => 150, (_, _) => 180));
        if (expected) Assert.NotEqual(legacy, converted);
        else Assert.Equal(legacy, converted);
    }

    private static Y4mHeader Header(int width, int height, Y4mChroma chroma, int depth, string matrix, string range, string location) => new()
    {
        Width = width, Height = height, Chroma = chroma, BitDepth = depth,
        ExtTags = new Dictionary<string, string> { ["XCOLORMATRIX"] = matrix, ["XCOLORRANGE"] = range, ["XCHROMALOC"] = location }
    };

    private static Y4mFrame Frame(Y4mHeader header, Func<int, int, int> luma, Func<int, int, int> cb, Func<int, int, int> cr)
    {
        byte[] data = new byte[header.FrameBytes];
        int scaleBytes = header.BytesPerSample;
        void Write(int index, int value)
        {
            data[index * scaleBytes] = (byte)value;
            if (scaleBytes == 2) data[index * scaleBytes + 1] = (byte)(value >> 8);
        }
        for (int y = 0; y < header.Height; ++y)
        for (int x = 0; x < header.Width; ++x) Write(y * header.Width + x, luma(x, y));
        if (header.Chroma != Y4mChroma.Mono)
        {
            var (cw, ch) = header.ChromaPlaneSize;
            int pixels = header.Width * header.Height;
            for (int y = 0; y < ch; ++y)
            for (int x = 0; x < cw; ++x)
            {
                Write(pixels + y * cw + x, cb(x, y));
                Write(pixels + cw * ch + y * cw + x, cr(x, y));
            }
        }
        return new(header, null, null, data);
    }

    // 製品の整数位相による実装とは別に、実座標と床関数から双線形補間の期待値を作る。
    private static double ReferenceChroma(Y4mHeader header, int x, int y, string position, Func<int, int, int> sample)
    {
        bool horizontal = header.Chroma is Y4mChroma.C420 or Y4mChroma.C422;
        bool vertical = header.Chroma == Y4mChroma.C420;
        double sx = horizontal ? (x - (position == "center" ? 0.5 : 0)) / 2 : x;
        double sy = vertical ? (y - (position == "topleft" ? 0 : 0.5)) / 2 : y;
        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
        double fx = sx - x0, fy = sy - y0;
        var (cw, ch) = header.ChromaPlaneSize;
        double At(int xx, int yy) => sample(Math.Clamp(xx, 0, cw - 1), Math.Clamp(yy, 0, ch - 1));
        return At(x0, y0) * (1 - fx) * (1 - fy) + At(x0 + 1, y0) * fx * (1 - fy)
            + At(x0, y0 + 1) * (1 - fx) * fy + At(x0 + 1, y0 + 1) * fx * fy;
    }

    private static byte[] ReferenceRgb(double y, double cb, double cr, int depth, bool full, double kr, double kb)
    {
        double scale = 1 << (depth - 8), neutral = 128 * scale;
        double luma = (y - (full ? 0 : 16 * scale)) / (full ? (1 << depth) - 1 : 219 * scale);
        cb = (cb - neutral) / (full ? (1 << depth) - 1 : 224 * scale);
        cr = (cr - neutral) / (full ? (1 << depth) - 1 : 224 * scale);
        double red = luma + 2 * (1 - kr) * cr;
        double blue = luma + 2 * (1 - kb) * cb;
        double green = (luma - kr * red - kb * blue) / (1 - kr - kb);
        byte Quantize(double value) => (byte)Math.Clamp((int)Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
        return new[] { Quantize(blue), Quantize(green), Quantize(red), (byte)255 };
    }
}

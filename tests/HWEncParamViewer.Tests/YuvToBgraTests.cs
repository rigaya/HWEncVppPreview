using System.Diagnostics;
using HWEncParamViewer.Core;
using Xunit.Abstractions;

namespace HWEncParamViewer.Tests;

public sealed class YuvToBgraTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Formats()
    {
        foreach (var chroma in Enum.GetValues<Y4mChroma>())
        foreach (int depth in Enumerable.Range(8, 9))
            yield return new object[] { chroma, depth };
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void LimitedBlackAndWhiteAcrossAllFormats(Y4mChroma chroma, int depth)
    {
        int scale = 1 << (depth - 8);
        foreach (int level in new[] { 16, 235 })
        {
            var frame = Uniform(3, 3, chroma, depth, level * scale, 128 * scale, 128 * scale);
            byte[] original = (byte[])frame.Data.Clone();
            byte[] converted = YuvToBgra.Convert(frame);
            byte value = level == 16 ? (byte)0 : (byte)255;
            Assert.Equal(3 * 3 * 4, converted.Length);
            for (int i = 0; i < converted.Length; i += 4)
                Assert.Equal(new byte[] { value, value, value, 255 }, converted[i..(i + 4)]);
            Assert.Equal(original, frame.Data);
        }
    }

    [Theory]
    [InlineData(81, 90, 240, 255, 0, 0)]
    [InlineData(145, 54, 34, 0, 255, 0)]
    [InlineData(41, 240, 110, 0, 0, 255)]
    public void Bt601PrimaryReferenceValues(int y, int cb, int cr, int red, int green, int blue)
    {
        byte[] result = YuvToBgra.Convert(Uniform(2, 2, Y4mChroma.C444, 8, y, cb, cr));
        // 8bitのYUV参照値そのものに量子化誤差があるため、1階調を許容する。
        Assert.InRange((int)result[0], blue - 1, blue + 1);
        Assert.InRange((int)result[1], green - 1, green + 1);
        Assert.InRange((int)result[2], red - 1, red + 1);
        Assert.Equal(255, result[3]);
    }

    [Theory]
    [InlineData(63, 102, 240, 255, 0, 0)]
    [InlineData(173, 42, 26, 0, 255, 0)]
    [InlineData(32, 240, 118, 0, 0, 255)]
    public void Bt709PrimaryReferenceValues(int y, int cb, int cr, int red, int green, int blue)
    {
        byte[] result = YuvToBgra.Convert(Uniform(1025, 1, Y4mChroma.C444, 8, y, cb, cr));
        Assert.InRange((int)result[0], blue - 1, blue + 1);
        Assert.InRange((int)result[1], green - 1, green + 1);
        Assert.InRange((int)result[2], red - 1, red + 1);
    }

    [Fact]
    public void MatrixBoundaryUsesBothWidthAndHeight()
    {
        byte[] sd = YuvToBgra.Convert(Uniform(1024, 576, Y4mChroma.C444, 8, 81, 90, 240));
        byte[] hdWidth = YuvToBgra.Convert(Uniform(1025, 576, Y4mChroma.C444, 8, 81, 90, 240));
        byte[] hdHeight = YuvToBgra.Convert(Uniform(1024, 577, Y4mChroma.C444, 8, 81, 90, 240));
        Assert.Equal(new byte[] { 0, 0, 254, 255 }, sd[..4]);
        Assert.Equal(hdWidth[..4], hdHeight[..4]);
        Assert.NotEqual(sd[..4], hdWidth[..4]);
    }

    [Theory]
    [InlineData(Y4mChroma.C420)]
    [InlineData(Y4mChroma.C422)]
    [InlineData(Y4mChroma.C444)]
    public void OddDimensionsUseCorrectChromaSample(Y4mChroma chroma)
    {
        var frame = Uniform(3, 3, chroma, 8, 81, 128, 128);
        var (cw, ch) = frame.Header.ChromaPlaneSize;
        frame.Data[9 + cw * ch - 1] = 90;
        frame.Data[9 + 2 * cw * ch - 1] = 240;
        byte[] result = YuvToBgra.Convert(frame);
        Assert.Equal(new byte[] { 0, 0, 254, 255 }, result[^4..]);
        Assert.Equal(new byte[] { 76, 76, 76, 255 }, result[..4]);
    }

    [Fact]
    public void LittleEndianHighDepthKeepsLowerBits()
    {
        var frame = Uniform(1, 1, Y4mChroma.Mono, 10, 65, 0, 0);
        byte[] result = YuvToBgra.Convert(frame);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, result);
        frame = Uniform(1, 1, Y4mChroma.Mono, 10, 66, 0, 0);
        Assert.Equal(new byte[] { 1, 1, 1, 255 }, YuvToBgra.Convert(frame));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(255, 255)]
    public void OutOfRangeLumaSaturates(int y, int expected)
    {
        byte[] result = YuvToBgra.Convert(Uniform(1, 1, Y4mChroma.Mono, 8, y, 0, 0));
        Assert.Equal(new byte[] { (byte)expected, (byte)expected, (byte)expected, 255 }, result);
    }

    [Fact]
    public void RejectsInvalidShapesFormatsAndLengths()
    {
        Assert.Throws<ArgumentNullException>(() => YuvToBgra.Convert(null!));
        foreach (var header in new[]
        {
            new Y4mHeader { Width = 0, Height = 1 },
            new Y4mHeader { Width = -1, Height = 1 },
            new Y4mHeader { Width = int.MaxValue, Height = int.MaxValue },
            new Y4mHeader { Width = 1, Height = 1, BitDepth = 7 },
            new Y4mHeader { Width = 1, Height = 1, BitDepth = 17 },
            new Y4mHeader { Width = 1, Height = 1, Chroma = (Y4mChroma)100 },
            new Y4mHeader { Width = 1, Height = 1 },
        })
            Assert.Throws<ArgumentException>(() => YuvToBgra.Convert(new(header, null, null, Array.Empty<byte>())));
        var valid = Uniform(1, 1, Y4mChroma.Mono, 8, 16, 0, 0);
        Assert.Throws<ArgumentException>(() => YuvToBgra.Convert(new(valid.Header, null, null, new byte[2])));
    }

    [Fact]
    public void Records1080pConversionTimeWithoutPerformanceThreshold()
    {
        var frame = Uniform(1920, 1080, Y4mChroma.C420, 8, 126, 90, 170);
        var watch = Stopwatch.StartNew();
        byte[] result = YuvToBgra.Convert(frame);
        watch.Stop();
        output.WriteLine($"1080p420 8bit変換時間（参考）: {watch.Elapsed.TotalMilliseconds:F3}ms");
        Assert.Equal(1920 * 1080 * 4, result.Length);
        Assert.Equal(255, result[^1]);
    }

    private static Y4mFrame Uniform(int width, int height, Y4mChroma chroma, int depth, int y, int cb, int cr)
    {
        var header = new Y4mHeader { Width = width, Height = height, Chroma = chroma, BitDepth = depth };
        byte[] data = new byte[header.FrameBytes];
        int yCount = width * height;
        var (cw, ch) = header.ChromaPlaneSize;
        int cCount = chroma == Y4mChroma.Mono ? 0 : cw * ch;
        for (int i = 0; i < yCount + 2 * cCount; ++i)
        {
            int value = i < yCount ? y : i < yCount + cCount ? cb : cr;
            int offset = i * header.BytesPerSample;
            data[offset] = (byte)value;
            if (header.BytesPerSample == 2) data[offset + 1] = (byte)(value >> 8);
        }
        return new(header, null, null, data);
    }
}

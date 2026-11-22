using System.Buffers.Binary;
using HWEncVppPreview.Cli;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class EngineImageTests
{
    [Theory]
    [InlineData(8, Y4mChroma.C420, new[] { 40, 46, 51, 66, 72, 77, 86, 92, 96, 60, 65, 80, 84, 96, 101, 116, 120 })]
    [InlineData(8, Y4mChroma.C422, new[] { 40, 46, 51, 66, 72, 77, 86, 92, 96, 73, 78, 99, 104, 119, 123, 109, 114, 135, 140, 155, 159 })]
    [InlineData(8, Y4mChroma.C444, new[] { 40, 46, 51, 66, 72, 77, 86, 92, 96, 76, 82, 87, 102, 108, 113, 122, 128, 132, 112, 118, 123, 138, 144, 149, 158, 164, 168 })]
    [InlineData(10, Y4mChroma.C420, new[] { 376, 394, 408, 454, 472, 486, 513, 531, 544, 436, 450, 495, 508, 544, 558, 603, 616 })]
    [InlineData(10, Y4mChroma.C422, new[] { 376, 394, 408, 454, 472, 486, 513, 531, 544, 475, 489, 553, 567, 612, 625, 583, 597, 661, 675, 720, 733 })]
    [InlineData(10, Y4mChroma.C444, new[] { 376, 394, 408, 454, 472, 486, 513, 531, 544, 484, 502, 516, 562, 580, 594, 621, 639, 652, 592, 610, 624, 670, 688, 702, 729, 747, 760 })]
    public void 輝度と色差の各平面を平均し端では切出し範囲内だけを使う(int depth, Y4mChroma chroma, int[] expected)
    {
        var header = new Y4mHeader { Width = 9, Height = 9, Chroma = chroma, BitDepth = depth, SarNum = 4, SarDen = 3 };
        var (width, height) = header.ChromaPlaneSize;
        var samples = Plane(9, 9, 0).Concat(Plane(width, height, 36)).Concat(Plane(width, height, 72));
        var input = new Y4mFrame(header, 2, 0.02, Pack(samples, depth));

        var scaled = EngineImage.Transform(input, 2, [2, 2, 5, 5]);
        Assert.Equal((3, 3), (scaled.Header.Width, scaled.Header.Height));
        Assert.Equal((4, 3), (scaled.Header.SarNum, scaled.Header.SarDen));
        Assert.Equal((2d, 0.02), (scaled.Xts.GetValueOrDefault(), scaled.Xdur.GetValueOrDefault()));
        Assert.Equal(Pack(expected, depth), scaled.Data);
        Assert.Equal(input.Data, EngineImage.Transform(input, 1, null).Data);

        IEnumerable<int> Plane(int w, int h, int offset)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int value = offset + 13 * y + 3 * x;
                    yield return depth == 8 ? value : 256 + 3 * value;
                }
        }
    }

    [Theory]
    [InlineData(8, 2, 2, 19)]
    [InlineData(10, 2, 2, 19)]
    [InlineData(8, 3, 6, 21)]
    [InlineData(10, 3, 6, 21)]
    public void 上下の行を合わせて四捨五入する(int depth, int scale, int first, int second)
    {
        var header = new Y4mHeader { Width = 4, Height = 2, Chroma = Y4mChroma.C444, BitDepth = depth };
        int offset = depth == 8 ? 0 : 700;
        int[] plane = [0, 1, 11, 20, 2, 3, 21, 22];
        var samples = Enumerable.Range(0, 3).SelectMany(p => plane.Select(value => offset + p * 30 + value));
        var frame = new Y4mFrame(header, null, null, Pack(samples, depth));
        int[] expected = [offset + first, offset + second, offset + 30 + first, offset + 30 + second,
                          offset + 60 + first, offset + 60 + second];

        Assert.Equal(Pack(expected, depth), EngineImage.Transform(frame, scale, null).Data);
        int[] cropped = [offset + 1, offset + 11, offset + 3, offset + 21,
                         offset + 31, offset + 41, offset + 33, offset + 51,
                         offset + 61, offset + 71, offset + 63, offset + 81];
        Assert.Equal(Pack(cropped, depth), EngineImage.Transform(frame, 1, [1, 0, 2, 2]).Data);
    }

    private static byte[] Pack(IEnumerable<int> values, int depth)
    {
        var samples = values.ToArray();
        if (depth == 8) return samples.Select(value => checked((byte)value)).ToArray();
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), checked((ushort)samples[i]));
        return bytes;
    }
}

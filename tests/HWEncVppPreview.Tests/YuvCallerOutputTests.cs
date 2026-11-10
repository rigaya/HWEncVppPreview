using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class YuvCallerOutputTests
{
    public static IEnumerable<object?[]> Formats()
    {
        foreach (Y4mChroma chroma in Enum.GetValues<Y4mChroma>())
        foreach (int depth in new[] { 8, 10, 16 })
        foreach (string? transfer in new string?[] { null, "smpte2084", "arib-std-b67" })
            yield return new object?[] { chroma, depth, transfer };
    }
    [Theory]
    [MemberData(nameof(Formats))]
    public void 呼出側の厳密長配列と既存Convertの全画素が一致する(Y4mChroma chroma, int depth, string? transfer)
    {
        var tags = new Dictionary<string, string> { ["XCOLORMATRIX"] = "bt2020nc", ["XCOLORRANGE"] = "FULL", ["XCOLORPRIM"] = "bt2020" };
        if (transfer != null) tags["XTRANSFER"] = transfer;
        var header = new Y4mHeader { Width = 5, Height = 3, Chroma = chroma, BitDepth = depth, ExtTags = tags };
        var raw = new byte[header.FrameBytes];
        for (int i = 0; i < raw.Length / header.BytesPerSample; ++i)
        {
            int sample = (i * 29 + 17) & ((1 << depth) - 1);
            raw[i * header.BytesPerSample] = (byte)sample;
            if (header.BytesPerSample == 2) raw[i * 2 + 1] = (byte)(sample >> 8);
        }
        var original = raw.ToArray();
        var frame = new Y4mFrame(header, 0, null, raw);
        byte[] expected = YuvToBgra.Convert(frame);
        byte[] output = Enumerable.Repeat((byte)0xcd, 5 * 3 * 4).ToArray();
        YuvToBgra.ConvertInto(frame, output);
        Assert.Equal(expected, output); Assert.Equal(original, raw);
    }
    [Fact]
    public void 長さ不一致と入力aliasは書込み前に拒否する()
    {
        var header = new Y4mHeader { Width = 2, Height = 2, Chroma = Y4mChroma.C422, BitDepth = 16 };
        var raw = Enumerable.Repeat((byte)7, header.FrameBytes).ToArray();
        var frame = new Y4mFrame(header, null, null, raw);
        Assert.Throws<ArgumentException>(() => YuvToBgra.ConvertInto(frame, new byte[15]));
        Assert.Throws<ArgumentException>(() => YuvToBgra.ConvertInto(frame, new byte[17]));
        Assert.Throws<ArgumentException>(() => YuvToBgra.ConvertInto(frame, raw));
        Assert.All(raw, value => Assert.Equal((byte)7, value));
        Assert.Throws<ArgumentNullException>(() => YuvToBgra.ConvertInto(frame, null!));
    }
}

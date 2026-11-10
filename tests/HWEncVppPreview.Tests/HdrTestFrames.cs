using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

/// <summary>旧変換のgoldenとHDRの参照計算で共有する、再現可能な入力だけを作る。</summary>
internal static class HdrTestFrames
{
    internal static IEnumerable<(Y4mChroma Chroma, int Depth, string Location, bool Full, string Matrix)> SdrFormats()
    {
        foreach (var chroma in Enum.GetValues<Y4mChroma>())
        foreach (int depth in new[] { 8, 10, 16 })
        foreach (string location in new[] { "left", "center", "topleft" })
        foreach (bool full in new[] { false, true })
        foreach (string matrix in new[] { "bt709", "smpte170m", "bt2020nc", "smpte240m", "unknown" })
            yield return (chroma, depth, location, full, matrix);
    }

    internal static string Key(Y4mChroma chroma, int depth, string location, bool full, string matrix)
        => $"{chroma}/{depth}/{location}/{(full ? "full" : "limited")}/{matrix}";

    internal static Y4mFrame Create(Y4mChroma chroma, int depth, string location, bool full,
        string matrix, string? transfer, string primaries = "bt2020", int width = 17, int height = 13)
    {
        var tags = new Dictionary<string, string>
        {
            ["XCOLORMATRIX"] = matrix, ["XCOLORRANGE"] = full ? "FULL" : "LIMITED",
            ["XCHROMALOC"] = location, ["XCOLORPRIM"] = primaries
        };
        if (transfer != null) tags["XTRANSFER"] = transfer;
        var header = new Y4mHeader
        {
            Width = width, Height = height, Chroma = chroma, BitDepth = depth,
            ColorSpaceTag = (chroma switch
            {
                Y4mChroma.C420 => "420", Y4mChroma.C422 => "422",
                Y4mChroma.C444 => "444", _ => "mono"
            }) + (depth == 8 ? "" : chroma == Y4mChroma.Mono ? depth.ToString() : "p" + depth),
            ExtTags = tags
        };
        byte[] bytes = new byte[header.FrameBytes];
        int count = bytes.Length / header.BytesPerSample, scale = 1 << (depth - 8), maximum = (1 << depth) - 1;
        uint state = 0x59b7d43f;
        int[] anchors = [0, 16 * scale - 1, 16 * scale, 16 * scale + 1, 128 * scale,
            235 * scale - 1, 235 * scale, 235 * scale + 1, maximum];
        for (int index = 0; index < count; index++)
        {
            // Randomの実装世代に依存せず、下位ビットと範囲外も固定の列で覆う。
            state = unchecked(state * 1664525 + 1013904223);
            int sample = index < anchors.Length ? anchors[index] : (int)(state >> 8) & maximum;
            Write(bytes, index, header.BytesPerSample, sample);
        }
        return new(header, 0, 1.0 / 30, bytes);
    }

    internal static void Write(byte[] data, int sample, int bytes, int value)
    {
        data[sample * bytes] = (byte)value;
        if (bytes == 2) data[sample * bytes + 1] = (byte)(value >> 8);
    }
}

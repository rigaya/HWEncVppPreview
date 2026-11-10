using System.Security.Cryptography;
using System.Text.Json;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class HdrToneMappingTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> SdrGolden = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Data", "hdr", "sdr-golden.json")))!);
    private static readonly double[,] Gamut = CreateGamutMatrix();

    [Theory]
    [InlineData(0, 0, 0.000001)]
    [InlineData(0.5081, 100, 0.05)]
    [InlineData(0.5807, 203, 0.05)]
    [InlineData(1, 10000, 0.000001)]
    public void PqMatchesAbsoluteLuminanceReferences(double signal, double nits, double error)
        => Assert.InRange(HdrToneMapping.PqToNits(signal), nits - error, nits + error);

    [Theory]
    [InlineData(0, 0, 0.000001)]
    [InlineData(0.75, 203, 0.3)]
    [InlineData(1, 1000, 0.0001)]
    public void HlgNeutralUsesTheNominalThousandNitDisplay(double signal, double nits, double error)
        => Assert.InRange(HdrToneMapping.HlgToNits(signal), nits - error, nits + error);

    [Theory]
    [InlineData(1)]
    [InlineData(4.926108374384237)]
    [InlineData(20)]
    public void ShoulderIsMonotoneAndHasTheIdentitySlopeAtTheKnee(double peak)
    {
        double previous = 0;
        for (int index = 0; index <= 10000; index++)
        {
            double input = peak * index / 10000;
            double result = HdrToneMapping.MapLuminance(input, peak);
            Assert.InRange(result, previous, 1);
            if (input <= 0.75) Assert.Equal(input, result, 12);
            previous = result;
        }
        Assert.Equal(0.75, HdrToneMapping.MapLuminance(0.75, peak), 12);
        const double delta = 0.000001;
        double left = (HdrToneMapping.MapLuminance(0.75, peak) - HdrToneMapping.MapLuminance(0.75 - delta, peak)) / delta;
        double right = (HdrToneMapping.MapLuminance(0.75 + delta, peak) - HdrToneMapping.MapLuminance(0.75, peak)) / delta;
        Assert.InRange(left, 0.99999, 1.00001);
        Assert.InRange(right, 0.99998, 1.00001);
        Assert.Equal(1, HdrToneMapping.MapLuminance(peak, peak), 12);
        Assert.Equal(1, HdrToneMapping.MapLuminance(peak * 2, peak), 12);
    }

    public static IEnumerable<object[]> HdrFormats()
    {
        foreach (string transfer in new[] { "smpte2084", "arib-std-b67" })
        foreach (int depth in new[] { 10, 16 })
        foreach (var chroma in Enum.GetValues<Y4mChroma>())
        foreach (string location in new[] { "left", "center", "topleft" })
        foreach (bool full in new[] { false, true })
        foreach (string primaries in new[] { "bt2020", "bt709" })
        foreach (double white in new[] { 100.0, 203, 500 })
            yield return [transfer, depth, chroma, location, full, primaries, white];
    }

    [Theory]
    [MemberData(nameof(HdrFormats))]
    public void EveryHdrPixelIsWithinOneOfAnIndependentDoubleReference(string transfer, int depth,
        Y4mChroma chroma, string location, bool full, string primaries, double white)
    {
        var settings = new HdrDisplaySettings { ReferenceWhiteNits = white };
        foreach (string matrix in new[] { "bt2020nc", "bt709", "smpte170m", "smpte240m" })
        {
            var frame = HdrTestFrames.Create(chroma, depth, location, full, matrix, transfer, primaries);
            byte[] original = (byte[])frame.Data.Clone();
            AssertPixels(frame, settings);
            Assert.Equal(original, frame.Data);
        }
    }

    [Theory]
    [InlineData("smpte2084", 100)]
    [InlineData("smpte2084", 203)]
    [InlineData("arib-std-b67", 100)]
    [InlineData("arib-std-b67", 203)]
    public void FullSixteenBitGrayRampHasNoLookupStepsBeyondOneCodeValue(string transfer, double white)
    {
        var frame = HdrTestFrames.Create(Y4mChroma.Mono, 16, "left", true,
            "bt2020nc", transfer, width: 65536, height: 1);
        for (int index = 0; index <= ushort.MaxValue; index++)
            HdrTestFrames.Write(frame.Data, index, 2, index);
        var settings = new HdrDisplaySettings { ReferenceWhiteNits = white };
        byte[] actual = AssertPixels(frame, settings);
        for (int index = 1; index <= ushort.MaxValue; index++)
            Assert.InRange((int)actual[index * 4], actual[(index - 1) * 4], 255);
    }

    [Theory]
    [InlineData("smpte2084")]
    [InlineData("arib-std-b67")]
    public void Bt2020GrayRemainsNeutralInBt709(string transfer)
    {
        var frame = HdrTestFrames.Create(Y4mChroma.C444, 10, "left", true, "bt2020nc", transfer);
        int count = frame.Header.Width * frame.Header.Height;
        for (int index = 0; index < count; index++)
        {
            HdrTestFrames.Write(frame.Data, index, 2, 500);
            HdrTestFrames.Write(frame.Data, count + index, 2, 512);
            HdrTestFrames.Write(frame.Data, 2 * count + index, 2, 512);
        }
        byte[] actual = AssertPixels(frame, new());
        for (int index = 0; index < actual.Length; index += 4)
        {
            Assert.InRange(Math.Abs(actual[index] - actual[index + 1]), 0, 1);
            Assert.InRange(Math.Abs(actual[index] - actual[index + 2]), 0, 1);
        }
    }

    public static IEnumerable<object[]> VectorBoundaryFormats()
    {
        foreach (int width in new[] { 1, 2, 7, 8, 9, 15, 16, 17, 31, 32, 33 })
        foreach (int height in new[] { 1, 3 })
        foreach (int depth in new[] { 8, 10, 16 })
        foreach (string location in new[] { "left", "center", "topleft" })
        foreach (string transfer in new[] { "smpte2084", "arib-std-b67" })
        foreach (bool full in new[] { false, true })
            yield return [width, height, depth, location, transfer, full];
    }

    [Theory]
    [MemberData(nameof(VectorBoundaryFormats))]
    public void VectorLoadsAndScalarTailsRespectOddAndTinyPlaneBoundaries(
        int width, int height, int depth, string location, string transfer, bool full)
    {
        // 8画素境界の前後、最小プレーン、奇数クロマの末尾を同じdouble基準で確かめる。
        foreach (string matrix in new[] { "bt2020nc", "bt709" })
        {
            var frame = HdrTestFrames.Create(Y4mChroma.C420, depth, location, full, matrix, transfer,
                width: width, height: height);
            byte[] original = (byte[])frame.Data.Clone();
            AssertPixels(frame, new());
            Assert.Equal(original, frame.Data);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(49)]
    [InlineData(1001)]
    public void HdrRejectsInvalidReferenceWhiteBeforeStartingPixelWork(double white)
    {
        var frame = HdrTestFrames.Create(Y4mChroma.C420, 10, "left", false, "bt2020nc", "smpte2084");
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            YuvToBgra.Convert(frame, new HdrDisplaySettings { ReferenceWhiteNits = white }));
        Assert.Equal(nameof(HdrDisplaySettings.ReferenceWhiteNits), exception.ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(202)]
    public void HdrRejectsANonFinitePeakOrAPeakBelowReferenceWhite(double peak)
    {
        var frame = HdrTestFrames.Create(Y4mChroma.C420, 10, "center", true, "bt2020nc", "arib-std-b67");
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            YuvToBgra.Convert(frame, new HdrDisplaySettings { PeakNits = peak }));
        Assert.Equal(nameof(HdrDisplaySettings.PeakNits), exception.ParamName);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(1000)]
    public void HdrAcceptsBothEndsOfTheReferenceWhiteRange(double white)
        => AssertPixels(HdrTestFrames.Create(Y4mChroma.C420, 10, "left", false, "bt2020nc", "smpte2084"),
            new HdrDisplaySettings { ReferenceWhiteNits = white });

    public static IEnumerable<object[]> SdrFormats()
        => HdrTestFrames.SdrFormats().Select(x => new object[] { x.Chroma, x.Depth, x.Location, x.Full, x.Matrix });

    [Theory]
    [MemberData(nameof(SdrFormats))]
    public void SdrBytesMatchThePreviousConverterForEveryDisplaySetting(
        Y4mChroma chroma, int depth, string location, bool full, string matrix)
    {
        string expected = SdrGolden.Value[HdrTestFrames.Key(chroma, depth, location, full, matrix)];
        foreach (string? transfer in new string?[] { null, "bt709", "iec61966-2-1", "unknown" })
        foreach (double white in new[] { 50.0, 203, 1000 })
        {
            var frame = HdrTestFrames.Create(chroma, depth, location, full, matrix, transfer);
            byte[] bytes = YuvToBgra.Convert(frame, new HdrDisplaySettings { ReferenceWhiteNits = white });
            Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    private static byte[] AssertPixels(Y4mFrame frame, HdrDisplaySettings settings)
    {
        byte[] expected = Reference(frame, settings);
        byte[] actual = YuvToBgra.Convert(frame, settings);
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            int difference = Math.Abs(actual[index] - expected[index]);
            Assert.True(difference <= 1,
                $"画素{index / 4}成分{index % 4}:実測{actual[index]}、double基準{expected[index]}、差{difference}");
            if ((index & 3) == 3) Assert.Equal(255, actual[index]);
        }
        return actual;
    }

    // 製品の参照表・色行列・色差補間・曲線関数を使わず、式をdoubleで直接評価する。
    private static byte[] Reference(Y4mFrame frame, HdrDisplaySettings settings)
    {
        var h = frame.Header;
        int count = h.Width * h.Height, scale = 1 << (h.BitDepth - 8), maximum = (1 << h.BitDepth) - 1;
        bool full = h.ColorRange == "FULL";
        (double kr, double kb) = h.ColorMatrix switch
        {
            "bt2020nc" => (0.2627, 0.0593), "bt709" => (0.2126, 0.0722),
            "smpte240m" => (0.212, 0.087), _ => (0.299, 0.114)
        };
        var (cw, ch) = h.ChromaPlaneSize;
        int Read(int offset)
        {
            int address = offset * h.BytesPerSample;
            return h.BytesPerSample == 1 ? frame.Data[address] : frame.Data[address] + 256 * frame.Data[address + 1];
        }
        double Chroma(int x, int y, int plane)
        {
            if (h.Chroma == Y4mChroma.Mono) return 0;
            double xx = h.Chroma == Y4mChroma.C444 ? x : (x - (h.ChromaLocation == "center" ? 0.5 : 0)) / 2;
            double yy = h.Chroma == Y4mChroma.C420 ? (y - (h.ChromaLocation == "topleft" ? 0 : 0.5)) / 2 : y;
            int x0 = (int)Math.Floor(xx), y0 = (int)Math.Floor(yy);
            double fx = xx - x0, fy = yy - y0;
            int offset = count + plane * cw * ch;
            double At(int a, int b) => Read(offset + Math.Clamp(b, 0, ch - 1) * cw + Math.Clamp(a, 0, cw - 1));
            double top = At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx;
            double bottom = At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx;
            return (top * (1 - fy) + bottom * fy - 128 * scale) / (full ? maximum : 224.0 * scale);
        }
        byte[] result = new byte[count * 4];
        for (int y = 0; y < h.Height; y++)
        for (int x = 0; x < h.Width; x++)
        {
            double luminance = (Read(y * h.Width + x) - (full ? 0 : 16 * scale)) / (full ? maximum : 219.0 * scale);
            double cb = Chroma(x, y, 0), cr = Chroma(x, y, 1);
            double r = Math.Clamp(luminance + 2 * (1 - kr) * cr, 0, 1);
            double b = Math.Clamp(luminance + 2 * (1 - kb) * cb, 0, 1);
            double g = Math.Clamp(luminance - 2 * kb * (1 - kb) / (1 - kr - kb) * cb
                - 2 * kr * (1 - kr) / (1 - kr - kb) * cr, 0, 1);
            if (h.Transfer == "smpte2084")
            {
                r = PqReference(r); g = PqReference(g); b = PqReference(b);
            }
            else
            {
                r = HlgScene(r); g = HlgScene(g); b = HlgScene(b);
                double gain = 1000 * Math.Pow(0.2627 * r + 0.678 * g + 0.0593 * b, 0.2);
                r *= gain; g *= gain; b *= gain;
            }
            r /= settings.ReferenceWhiteNits; g /= settings.ReferenceWhiteNits; b /= settings.ReferenceWhiteNits;
            double brightness = Math.Max(r, Math.Max(g, b));
            double shoulder = ShoulderReference(brightness, settings.PeakNits / settings.ReferenceWhiteNits);
            double factor = brightness > 0 ? shoulder / brightness : 0;
            r *= factor; g *= factor; b *= factor;
            if (h.ColorPrimaries == "bt2020")
            {
                (r, g, b) = (Gamut[0, 0] * r + Gamut[0, 1] * g + Gamut[0, 2] * b,
                    Gamut[1, 0] * r + Gamut[1, 1] * g + Gamut[1, 2] * b,
                    Gamut[2, 0] * r + Gamut[2, 1] * g + Gamut[2, 2] * b);
            }
            static byte Encode(double value) => (byte)Math.Round(255 * Math.Pow(Math.Clamp(value, 0, 1), 1 / 2.4),
                MidpointRounding.AwayFromZero);
            int target = (y * h.Width + x) * 4;
            result[target] = Encode(b); result[target + 1] = Encode(g); result[target + 2] = Encode(r); result[target + 3] = 255;
        }
        return result;
    }

    private static double PqReference(double value)
    {
        double root = Math.Pow(value, 32.0 / 2523);
        return 10000 * Math.Pow(Math.Max(root - 3424.0 / 4096, 0) / (2413.0 / 128 - 2392.0 / 128 * root), 16384.0 / 2610);
    }
    private static double HlgScene(double value) => value <= 0.5 ? value * value / 3
        : (Math.Exp((value - 0.55991073) / 0.17883277) + 0.28466892) / 12;
    private static double ShoulderReference(double value, double peak)
    {
        if (value <= 0.75) return value;
        if (value >= peak) return 1;
        double offset = value - 0.75;
        return 0.75 + offset / (1 + offset * (4 - 1 / (peak - 0.75)));
    }

    // 原色のxyとD65白からXYZ行列を解き、製品側の固定係数と独立に色域行列を作る。
    private static double[,] CreateGamutMatrix()
    {
        static double[,] RgbToXyz((double X, double Y) red, (double X, double Y) green, (double X, double Y) blue)
        {
            (double X, double Y)[] xy = [red, green, blue];
            double[,] matrix = new double[3, 3];
            for (int column = 0; column < 3; column++)
            {
                matrix[0, column] = xy[column].X / xy[column].Y;
                matrix[1, column] = 1;
                matrix[2, column] = (1 - xy[column].X - xy[column].Y) / xy[column].Y;
            }
            double[] white = [0.3127 / 0.3290, 1, (1 - 0.3127 - 0.3290) / 0.3290];
            double[,] inverse = Inverse(matrix);
            for (int column = 0; column < 3; column++)
            {
                double scale = Enumerable.Range(0, 3).Sum(row => inverse[column, row] * white[row]);
                for (int row = 0; row < 3; row++) matrix[row, column] *= scale;
            }
            return matrix;
        }
        var source = RgbToXyz((0.708, 0.292), (0.170, 0.797), (0.131, 0.046));
        var destination = Inverse(RgbToXyz((0.640, 0.330), (0.300, 0.600), (0.150, 0.060)));
        double[,] result = new double[3, 3];
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
        for (int k = 0; k < 3; k++) result[row, column] += destination[row, k] * source[k, column];
        return result;
    }

    private static double[,] Inverse(double[,] matrix)
    {
        double[,] work = new double[3, 6];
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
        { work[row, column] = matrix[row, column]; work[row, column + 3] = row == column ? 1 : 0; }
        for (int pivot = 0; pivot < 3; pivot++)
        {
            double divisor = work[pivot, pivot];
            for (int column = 0; column < 6; column++) work[pivot, column] /= divisor;
            for (int row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                double factor = work[row, pivot];
                for (int column = 0; column < 6; column++) work[row, column] -= factor * work[pivot, column];
            }
        }
        double[,] result = new double[3, 3];
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++) result[row, column] = work[row, column + 3];
        return result;
    }
}

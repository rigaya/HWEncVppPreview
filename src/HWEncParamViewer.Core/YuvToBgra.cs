using System.Buffers;
using System.Runtime.CompilerServices;

namespace HWEncParamViewer.Core;

/// <summary>実際のRGB変換に使う行列・範囲・色差位置と、HDR伝達特性の印。</summary>
public sealed record YuvColorInfo(string Matrix, string Range, string ChromaLocation, bool IsHdr);

/// <summary>y4mの色情報と色差の位置を使ってBGRA8へ変換する。</summary>
public static partial class YuvToBgra
{
    /// <summary>未指定・未対応の行列では従来の解像度による推定、範囲ではlimitedを使う。</summary>
    public static YuvColorInfo GetColorInfo(Y4mHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        string matrix = Normalize(header.ColorMatrix) switch
        {
            "bt709" or "709" => "BT.709",
            "bt601" or "601" or "smpte170m" or "bt470bg" => "BT.601",
            "bt2020nc" or "bt2020ncl" or "bt2020-ncl" => "BT.2020 ncl",
            "smpte240m" or "smpte240" => "SMPTE 240M",
            _ => header.Width > 1024 || header.Height > 576 ? "BT.709" : "BT.601"
        };
        string range = Normalize(header.ColorRange) is "full" or "pc" or "jpeg" ? "full" : "limited";
        string location = Normalize(header.ChromaLocation) switch
        {
            "left" => "left", "center" => "center", "topleft" or "top-left" => "topleft",
            // paldvはU/Vで位相が異なり単一位置では表せないため、今回はleftへフォールバックする。
            _ => header.Chroma == Y4mChroma.C420 && header.ColorSpaceTag == "420jpeg" ? "center" : "left"
        };
        bool hdr = Normalize(header.Transfer) is "smpte2084" or "pq" or "arib-std-b67" or "hlg";
        return new(matrix, range, location, hdr);
    }

    /// <summary>幅×高さ×4バイトのBGRAを返す。入力画像は変更せず、アルファは255にする。</summary>
    /// <remarks>
    /// 8bit超は下位詰めのリトルエンディアン。色差はleft/center/topleftの位置から線形補間する。
    /// 範囲外の値はRGB変換後に飽和させる。PQ/HLGだけを線形化し、基準白と高輝度の肩を使ってSDRへ変換する。
    /// </remarks>
    public static byte[] Convert(Y4mFrame frame, HdrDisplaySettings? settings = null)
        => ConvertCore(frame, settings, null);

    /// <summary>呼出側が所有する幅×高さ×4の配列へ変換し、入力画像は変更しない。</summary>
    public static void ConvertInto(Y4mFrame frame, byte[] output, HdrDisplaySettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ConvertCore(frame, settings, output);
    }

    private static byte[] ConvertCore(Y4mFrame frame, HdrDisplaySettings? settings, byte[]? output)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Header);
        ArgumentNullException.ThrowIfNull(frame.Data);
        var header = frame.Header;
        int width = header.Width, height = header.Height;
        if (width <= 0 || height <= 0) throw new ArgumentException(CoreText.ImageSizePositive, nameof(frame));
        if (header.BitDepth is < 8 or > 16) throw new ArgumentException(CoreText.BitDepthUnsupported, nameof(frame));
        if (!Enum.IsDefined(header.Chroma)) throw new ArgumentException(CoreText.ChromaUnsupported, nameof(frame));
        long pixels = (long)width * height;
        if (pixels > int.MaxValue / 4) throw new ArgumentException(CoreText.ImageTooLarge, nameof(frame));
        if (output != null && (output.Length != pixels * 4 || ReferenceEquals(output, frame.Data)))
            throw new ArgumentException("出力は入力とは別の幅×高さ×4byteの配列にしてください。", nameof(output));
        int lumaSamples = (int)pixels;
        int chromaWidth = header.Chroma is Y4mChroma.C420 or Y4mChroma.C422 ? (width + 1) / 2 : width;
        int chromaHeight = header.Chroma == Y4mChroma.C420 ? (height + 1) / 2 : height;
        int chromaSamples = header.Chroma == Y4mChroma.Mono ? 0 : checked(chromaWidth * chromaHeight);
        int sampleBytes = header.BytesPerSample;
        long expectedBytes = ((long)lumaSamples + 2L * chromaSamples) * sampleBytes;
        if (frame.Data.LongLength != expectedBytes)
            throw new ArgumentException(CoreText.PlaneDataLengthInvalid(expectedBytes, frame.Data.LongLength), nameof(frame));

        var color = GetColorInfo(header);
        if (color.IsHdr) return ConvertHdr(frame, settings ?? new HdrDisplaySettings(), output);
        (double kr, double kb) = color.Matrix switch
        {
            "BT.709" => (0.2126, 0.0722), "BT.2020 ncl" => (0.2627, 0.0593),
            "SMPTE 240M" => (0.212, 0.087), _ => (0.299, 0.114)
        };
        double kg = 1.0 - kr - kb;
        int scale = 1 << (header.BitDepth - 8);
        bool full = color.Range == "full";
        int black = full ? 0 : 16 * scale, neutral = 128 * scale;
        double yFactor = 255.0 / (full ? (1 << header.BitDepth) - 1 : 219 * scale);
        double cFactor = 255.0 / (full ? (1 << header.BitDepth) - 1 : 224 * scale);
        double redCr = 2 * (1 - kr) * cFactor;
        double blueCb = 2 * (1 - kb) * cFactor;
        double greenCb = 2 * kb * (1 - kb) / kg * cFactor;
        double greenCr = 2 * kr * (1 - kr) / kg * cFactor;
        byte[] data = frame.Data;
        byte[] result = output ?? new byte[lumaSamples * 4];
        if (header.Chroma == Y4mChroma.C420)
        {
            // 同じ色差行の水平補間を隣り合う輝度行で共有し、画素ごとの4点読取りを避ける。
            int expandedSamples = checked(width * chromaHeight);
            float[] expanded = ArrayPool<float>.Shared.Rent(checked(expandedSamples * 2));
            try
            {
                Parallel.For(0, chromaHeight, y => ExpandChromaRow(y, color.ChromaLocation == "center", width,
                    chromaWidth, lumaSamples, chromaSamples, expandedSamples, sampleBytes, neutral, data, expanded));
                Parallel.For(0, height, y => ConvertSubsampledRow(y, header.Chroma, color.ChromaLocation != "topleft",
                    width, chromaHeight, expandedSamples, sampleBytes, black, yFactor,
                    redCr, blueCb, greenCb, greenCr, data, expanded, result));
            }
            finally { ArrayPool<float>.Shared.Return(expanded); }
        }
        else if (header.Chroma == Y4mChroma.C422)
        {
            Parallel.For(0, height, y => Convert422Row(y, color.ChromaLocation == "center", width, chromaWidth,
                lumaSamples, chromaSamples, sampleBytes, black, neutral, yFactor,
                redCr, blueCb, greenCb, greenCr, data, result));
        }
        else
        {
            Parallel.For(0, height, y => ConvertFullRow(y, header.Chroma == Y4mChroma.Mono, width,
                lumaSamples, sampleBytes, black, neutral, yFactor,
                redCr, blueCb, greenCb, greenCr, data, result));
        }
        return result;
    }

    // 行ループを初回から最適化し、短いコマ送りでもTiered JITの暖機待ちを発生させない。
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertFullRow(int y, bool mono, int width, int lumaSamples,
        int sampleBytes, int black, int neutral, double yFactor, double redCr, double blueCb,
        double greenCb, double greenCr, byte[] data, byte[] result)
    {
        int source = y * width, destination = source * 4;
        for (int x = 0; x < width; ++x)
        {
            double luminance = (Sample(data, source + x, sampleBytes) - black) * yFactor;
            int cb = 0, cr = 0;
            if (!mono)
            {
                cb = Sample(data, lumaSamples + source + x, sampleBytes) - neutral;
                cr = Sample(data, lumaSamples * 2 + source + x, sampleBytes) - neutral;
            }
            int target = destination + x * 4;
            result[target] = Clip(luminance + blueCb * cb);
            result[target + 1] = Clip(luminance - greenCb * cb - greenCr * cr);
            result[target + 2] = Clip(luminance + redCr * cr);
            result[target + 3] = 255;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Convert422Row(int y, bool centered, int width, int chromaWidth,
        int lumaSamples, int chromaSamples, int sampleBytes, int black, int neutral,
        double yFactor, double redCr, double blueCb, double greenCb, double greenCr,
        byte[] data, byte[] result)
    {
        int source = y * width, chromaRow = y * chromaWidth, destination = source * 4;
        for (int x = 0; x < width; ++x)
        {
            Axis(x, centered, chromaWidth, out int x0, out int x1, out double fraction);
            int u0 = Sample(data, lumaSamples + chromaRow + x0, sampleBytes);
            int v0 = Sample(data, lumaSamples + chromaSamples + chromaRow + x0, sampleBytes);
            double cb = u0 + (Sample(data, lumaSamples + chromaRow + x1, sampleBytes) - u0) * fraction - neutral;
            double cr = v0 + (Sample(data, lumaSamples + chromaSamples + chromaRow + x1, sampleBytes) - v0) * fraction - neutral;
            double luminance = (Sample(data, source + x, sampleBytes) - black) * yFactor;
            int target = destination + x * 4;
            result[target] = Clip(luminance + blueCb * cb);
            result[target + 1] = Clip(luminance - greenCb * cb - greenCr * cr);
            result[target + 2] = Clip(luminance + redCr * cr);
            result[target + 3] = 255;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ExpandChromaRow(int y, bool centered, int width, int chromaWidth, int lumaSamples,
        int chromaSamples, int expandedSamples, int sampleBytes, int neutral, byte[] data, float[] expanded)
    {
        int source = y * chromaWidth, destination = y * width;
        for (int x = 0; x < width; ++x)
        {
            Axis(x, centered, chromaWidth, out int x0, out int x1, out double fraction);
            int u0 = Sample(data, lumaSamples + source + x0, sampleBytes);
            int v0 = Sample(data, lumaSamples + chromaSamples + source + x0, sampleBytes);
            int u1 = Sample(data, lumaSamples + source + x1, sampleBytes);
            int v1 = Sample(data, lumaSamples + chromaSamples + source + x1, sampleBytes);
            // 16bitでも1/4刻みの補間はfloatで正確に保持でき、8bitへの丸めは最後だけになる。
            expanded[destination + x] = (float)(u0 + (u1 - u0) * fraction - neutral);
            expanded[expandedSamples + destination + x] = (float)(v0 + (v1 - v0) * fraction - neutral);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertSubsampledRow(int y, Y4mChroma chroma, bool centeredY, int width,
        int chromaHeight, int expandedSamples, int sampleBytes, int black, double yFactor,
        double redCr, double blueCb, double greenCb, double greenCr,
        byte[] data, float[] expanded, byte[] result)
    {
        int y0 = y, y1 = y;
        double fraction = 0;
        if (chroma == Y4mChroma.C420) Axis(y, centeredY, chromaHeight, out y0, out y1, out fraction);
        int row0 = y0 * width, row1 = y1 * width;
        int source = y * width, targetRow = source * 4;
        for (int x = 0; x < width; ++x)
        {
            double cb = expanded[row0 + x];
            double cr = expanded[expandedSamples + row0 + x];
            cb += (expanded[row1 + x] - cb) * fraction;
            cr += (expanded[expandedSamples + row1 + x] - cr) * fraction;
            double luminance = (Sample(data, source + x, sampleBytes) - black) * yFactor;
            int target = targetRow + x * 4;
            result[target] = Clip(luminance + blueCb * cb);
            result[target + 1] = Clip(luminance - greenCb * cb - greenCr * cr);
            result[target + 2] = Clip(luminance + redCr * cr);
            result[target + 3] = 255;
        }
    }

    // 色差の位置は輝度の画素中心を0として、left/topleftが0、centerが0.5。
    // 床関数を画素ごとに呼ばず、位相が1/4または1/2刻みであることを使う。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Axis(int coordinate, bool centered, int length, out int first, out int second, out double fraction)
    {
        first = coordinate >> 1;
        if (centered)
        {
            bool even = (coordinate & 1) == 0;
            if (even) --first;
            fraction = even ? 0.75 : 0.25;
        }
        else fraction = (coordinate & 1) == 0 ? 0 : 0.5;
        second = Math.Clamp(first + 1, 0, length - 1);
        first = Math.Clamp(first, 0, length - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sample(byte[] data, int index, int bytes)
    {
        if (bytes == 1) return data[index];
        int offset = index * 2;
        return data[offset] | data[offset + 1] << 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip(double value) => (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";
}

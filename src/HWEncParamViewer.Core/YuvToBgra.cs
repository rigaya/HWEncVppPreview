using System.Runtime.CompilerServices;

namespace HWEncParamViewer.Core;

/// <summary>y4mの平面画像をlimited rangeのBT.601/BT.709からBGRA8へ変換する。</summary>
public static class YuvToBgra
{
    /// <summary>幅×高さ×4バイトのBGRAを返す。入力画像は変更せず、アルファは255にする。</summary>
    /// <remarks>
    /// 幅が1024超または高さが576超ならBT.709、それ以外はBT.601を使う。
    /// 8bit超のサンプルはy4mの規則に従う下位詰めのリトルエンディアン。
    /// 色範囲外の値は変換後に飽和させ、クロマの拡大は最寄りのサンプルを使う。
    /// </remarks>
    public static byte[] Convert(Y4mFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Header);
        ArgumentNullException.ThrowIfNull(frame.Data);
        var header = frame.Header;
        int width = header.Width, height = header.Height;
        if (width <= 0 || height <= 0) throw new ArgumentException("幅と高さは正の値が必要です", nameof(frame));
        if (header.BitDepth is < 8 or > 16) throw new ArgumentException("ビット深度は8〜16が必要です", nameof(frame));
        if (!Enum.IsDefined(header.Chroma)) throw new ArgumentException("未対応のクロマ形式です", nameof(frame));
        long pixels = (long)width * height;
        if (pixels > int.MaxValue / 4) throw new ArgumentException("BGRA画像が大きすぎます", nameof(frame));
        int lumaSamples = (int)pixels;
        int chromaWidth = header.Chroma is Y4mChroma.C420 or Y4mChroma.C422 ? (width + 1) / 2 : width;
        int chromaHeight = header.Chroma == Y4mChroma.C420 ? (height + 1) / 2 : height;
        int chromaSamples = header.Chroma == Y4mChroma.Mono ? 0 : checked(chromaWidth * chromaHeight);
        int sampleBytes = header.BytesPerSample;
        long expectedBytes = ((long)lumaSamples + 2L * chromaSamples) * sampleBytes;
        if (frame.Data.LongLength != expectedBytes)
            throw new ArgumentException($"平面データ長が不正です: 期待{expectedBytes}バイト、実際{frame.Data.LongLength}バイト", nameof(frame));

        bool hd = width > 1024 || height > 576;
        double kr = hd ? 0.2126 : 0.299;
        double kb = hd ? 0.0722 : 0.114;
        double kg = 1.0 - kr - kb;
        int scale = 1 << (header.BitDepth - 8);
        int black = 16 * scale, neutral = 128 * scale;
        double yFactor = 255.0 / (219 * scale);
        double cFactor = 255.0 / (224 * scale);
        double redCr = 2 * (1 - kr) * cFactor;
        double blueCb = 2 * (1 - kb) * cFactor;
        double greenCb = 2 * kb * (1 - kb) / kg * cFactor;
        double greenCr = 2 * kr * (1 - kr) / kg * cFactor;
        byte[] data = frame.Data;
        byte[] result = new byte[lumaSamples * 4];
        Parallel.For(0, height, y => ConvertRow(y, header.Chroma, width, chromaWidth, lumaSamples,
            chromaSamples, sampleBytes, black, neutral, yFactor, redCr, blueCb, greenCb, greenCr, data, result));
        return result;
    }

    // 行処理を初回から最適化し、短いコマ送りでもTiered JITの暖機待ちを発生させない。
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertRow(int y, Y4mChroma chroma, int width, int chromaWidth,
        int lumaSamples, int chromaSamples, int sampleBytes, int black, int neutral,
        double yFactor, double redCr, double blueCb, double greenCb, double greenCr,
        byte[] data, byte[] result)
    {
        int sourceRow = y * width;
        int targetRow = sourceRow * 4;
        int chromaRow = (chroma == Y4mChroma.C420 ? y / 2 : y) * chromaWidth;
        for (int x = 0; x < width; ++x)
        {
            double luminance = (Sample(data, sourceRow + x, sampleBytes) - black) * yFactor;
            int cb = 0, cr = 0;
            if (chroma != Y4mChroma.Mono)
            {
                int chromaIndex = chromaRow + (chroma is Y4mChroma.C420 or Y4mChroma.C422 ? x / 2 : x);
                cb = Sample(data, lumaSamples + chromaIndex, sampleBytes) - neutral;
                cr = Sample(data, lumaSamples + chromaSamples + chromaIndex, sampleBytes) - neutral;
            }
            int target = targetRow + x * 4;
            result[target] = Clip(luminance + blueCb * cb);
            result[target + 1] = Clip(luminance - greenCb * cb - greenCr * cr);
            result[target + 2] = Clip(luminance + redCr * cr);
            result[target + 3] = 255;
        }
    }

    private static int Sample(byte[] data, int index, int bytes)
    {
        if (bytes == 1) return data[index];
        int offset = index * 2;
        return data[offset] | data[offset + 1] << 8;
    }

    // 行ループ内へ丸めと飽和を取り込み、小関数のTier0呼び出しを画素ごとに残さない。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip(double value) => (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}

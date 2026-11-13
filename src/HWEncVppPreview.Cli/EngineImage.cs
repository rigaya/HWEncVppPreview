using HWEncVppPreview.Core;
using SkiaSharp;

namespace HWEncVppPreview.Cli;

internal static class EngineImage
{
    public static Y4mFrame Transform(Y4mFrame source, int scale, int[]? crop)
    {
        var h = source.Header;
        if (h.Chroma == Y4mChroma.Mono) throw new ArgumentException("モノクロ形式には対応していません。");
        if (scale < 1 || scale > 16) throw new ArgumentOutOfRangeException(nameof(scale));
        int x = 0, y = 0, w = h.Width, height = h.Height;
        if (crop is not null)
        {
            if (crop.Length != 4) throw new ArgumentException("cropは4要素です。");
            (x, y, w, height) = (crop[0], crop[1], crop[2], crop[3]);
        }
        if (x < 0 || y < 0 || w <= 0 || height <= 0 || (long)x + w > h.Width || (long)y + height > h.Height)
            throw new ArgumentOutOfRangeException(nameof(crop));
        int sx = h.Chroma == Y4mChroma.C444 ? 1 : 2, sy = h.Chroma == Y4mChroma.C420 ? 2 : 1;
        if (x % sx != 0 || y % sy != 0) throw new ArgumentException("cropの開始位置は色差の境界に合わせてください。");
        int ow = (w + scale - 1) / scale, oh = (height + scale - 1) / scale;
        var dest = new Y4mHeader { Width = ow, Height = oh, FpsNum = h.FpsNum, FpsDen = h.FpsDen, SarNum = h.SarNum, SarDen = h.SarDen,
            Chroma = h.Chroma, BitDepth = h.BitDepth, ColorSpaceTag = h.ColorSpaceTag, ExtTags = h.ExtTags };
        var data = new byte[dest.FrameBytes]; var input = source.Data; int bps = h.BytesPerSample;
        var (icw, ich) = h.ChromaPlaneSize; var (ocw, och) = dest.ChromaPlaneSize;
        int inOffset = 0, outOffset = 0;
        for (int plane = 0; plane < 3; plane++)
        {
            int iw = plane == 0 ? h.Width : icw, ih = plane == 0 ? h.Height : ich;
            int dw = plane == 0 ? ow : ocw, dh = plane == 0 ? oh : och;
            int px = plane == 0 ? x : x / sx, py = plane == 0 ? y : y / sy;
            for (int row = 0; row < dh; row++) for (int col = 0; col < dw; col++)
            {
                int from = inOffset + ((py + row * scale) * iw + px + col * scale) * bps;
                int to = outOffset + (row * dw + col) * bps;
                for (int b = 0; b < bps; b++) data[to + b] = input[from + b];
            }
            inOffset += iw * ih * bps; outOffset += dw * dh * bps;
        }
        return new Y4mFrame(dest, source.Xts, source.Xdur, data);
    }
    public static byte[] Encode(Y4mFrame frame, string format)
    {
        if (format == "yuv") return frame.Data;
        if (format is not ("png" or "jpeg")) throw new ArgumentException("未対応の画像形式です。");
        byte[] bgra = YuvToBgra.Convert(frame);
        using var bitmap = new SKBitmap(new SKImageInfo(frame.Header.Width, frame.Header.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        using var pix = bitmap.PeekPixels();
        using SKData? data = format == "jpeg"
            ? pix.Encode(new SKJpegEncoderOptions(90, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
            : pix.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Up, 1));
        return data?.ToArray() ?? throw new InvalidOperationException("画像の圧縮に失敗しました。");
    }
}

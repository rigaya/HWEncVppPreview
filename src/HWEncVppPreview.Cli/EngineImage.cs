using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
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
            Interlace = h.Interlace, Chroma = h.Chroma, BitDepth = h.BitDepth, ColorSpaceTag = h.ColorSpaceTag, ExtTags = h.ExtTags };
        var data = new byte[dest.FrameBytes];
        var input = source.Data;
        if (scale == 1 && x == 0 && y == 0 && w == h.Width && height == h.Height)
        {
            input.AsSpan(0, data.Length).CopyTo(data);
            return new Y4mFrame(dest, source.Xts, source.Xdur, data);
        }

        int rowStep = h.Interlace is 't' or 'b' ? 2 : 1;
        int sampleBytes = h.BytesPerSample;
        var (chromaWidth, chromaHeight) = h.ChromaPlaneSize;
        var (outChromaWidth, outChromaHeight) = dest.ChromaPlaneSize;
        int inputOffset = 0, outputOffset = 0;
        for (int plane = 0; plane < 3; plane++)
        {
            bool luma = plane == 0;
            int stride = luma ? h.Width : chromaWidth;
            int inputBytes = stride * (luma ? h.Height : chromaHeight) * sampleBytes;
            int outputWidth = luma ? ow : outChromaWidth;
            int outputBytes = outputWidth * (luma ? oh : outChromaHeight) * sampleBytes;
            int left = luma ? x : x / sx;
            int top = luma ? y : y / sy;
            int cropWidth = luma ? w : (w + sx - 1) / sx;
            int cropHeight = luma ? height : (height + sy - 1) / sy;
            var inputPlane = input.AsSpan(inputOffset, inputBytes);
            var outputPlane = data.AsSpan(outputOffset, outputBytes);
            if (sampleBytes == 1)
                ScalePlane<byte>(inputPlane, outputPlane, stride, left, top, cropWidth, cropHeight, outputWidth, scale, rowStep);
            else
                ScalePlane<ushort>(MemoryMarshal.Cast<byte, ushort>(inputPlane), MemoryMarshal.Cast<byte, ushort>(outputPlane),
                    stride, left, top, cropWidth, cropHeight, outputWidth, scale, rowStep);
            inputOffset += inputBytes;
            outputOffset += outputBytes;
        }
        return new Y4mFrame(dest, source.Xts, source.Xdur, data);
    }

    private static void ScalePlane<T>(ReadOnlySpan<T> source, Span<T> destination, int stride,
        int left, int top, int width, int height, int outputWidth, int scale, int rowStep) where T : unmanaged, IBinaryInteger<T>
    {
        int start = top * stride + left;
        if (scale == 1)
        {
            for (int y = 0; y < height; y++)
                source.Slice(start + y * stride, width).CopyTo(destination.Slice(y * outputWidth, width));
            return;
        }

        int outputHeight = destination.Length / outputWidth;
        // インタレでは、切り出し後の先頭行を基準に各平面の偶数行と奇数行を分ける。
        int FirstRow(int y) => y / rowStep * rowStep * scale + y % rowStep;

        // 全面を1/2にできる場合は、同じフィールドの2行を使って4画素を平均する。
        if (scale == 2 && width % 2 == 0 && height % (2 * rowStep) == 0)
        {
            for (int y = 0; y < outputHeight; y++)
            {
                int firstRow = FirstRow(y);
                var first = source.Slice(start + firstRow * stride, width);
                var second = source.Slice(start + (firstRow + rowStep) * stride, width);
                var output = destination.Slice(y * outputWidth, outputWidth);
                for (int x = 0; x < output.Length; x++)
                {
                    int i = x * 2;
                    int sum = ReadSample(first[i]) + ReadSample(first[i + 1]) + ReadSample(second[i]) + ReadSample(second[i + 1]);
                    output[x] = StoreSample((sum + 2) / 4);
                }
            }
            return;
        }

        int outputIndex = 0;
        for (int y = 0; y < outputHeight; y++)
        {
            int firstRow = FirstRow(y);
            int rows = Math.Min(scale, (height - 1 - firstRow) / rowStep + 1);
            for (int x = 0; x < width; x += scale)
            {
                int columns = Math.Min(scale, width - x);
                int count = rows * columns;
                int sum = 0;
                for (int dy = 0; dy < rows; dy++)
                {
                    var row = source.Slice(start + (firstRow + dy * rowStep) * stride + x, columns);
                    foreach (var sample in row) sum += ReadSample(sample);
                }
                // 右端・下端は存在する画素だけを数え、0.5は切り上げる。
                destination[outputIndex++] = StoreSample((sum + count / 2) / count);
            }
        }

        static int ReadSample(T sample)
        {
            int value = int.CreateTruncating(sample);
            return !BitConverter.IsLittleEndian && typeof(T) == typeof(ushort)
                ? BinaryPrimitives.ReverseEndianness((ushort)value) : value;
        }

        static T StoreSample(int value)
        {
            if (!BitConverter.IsLittleEndian && typeof(T) == typeof(ushort))
                value = BinaryPrimitives.ReverseEndianness((ushort)value);
            return T.CreateTruncating(value);
        }
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

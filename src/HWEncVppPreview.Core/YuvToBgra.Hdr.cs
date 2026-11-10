using System.Buffers;
using System.Runtime.CompilerServices;

namespace HWEncVppPreview.Core;

public static partial class YuvToBgra
{
    private static byte[] ConvertHdr(Y4mFrame frame, HdrDisplaySettings settings, byte[]? destination)
    {
        settings.Validate();
        var h = frame.Header;
        var color = GetColorInfo(h);
        int width = h.Width, height = h.Height, luma = width * height, bytes = h.BytesPerSample;
        int cw = h.Chroma is Y4mChroma.C420 or Y4mChroma.C422 ? (width + 1) / 2 : width;
        int ch = h.Chroma == Y4mChroma.C420 ? (height + 1) / 2 : height;
        int chroma = cw * ch, scale = 1 << (h.BitDepth - 8), neutral = 128 * scale;
        bool full = color.Range == "full";
        float black = full ? 0 : 16 * scale;
        float yf = 1f / (full ? (1 << h.BitDepth) - 1 : 219 * scale);
        float cf = 1f / (full ? (1 << h.BitDepth) - 1 : 224 * scale);
        (float kr, float kb) = color.Matrix switch
        {
            "BT.709" => (0.2126f, 0.0722f), "BT.2020 ncl" => (0.2627f, 0.0593f),
            "SMPTE 240M" => (0.212f, 0.087f), _ => (0.299f, 0.114f)
        };
        float kg = 1 - kr - kb;
        var parameters = new HdrParameters(
            Normalize(h.Transfer) is "arib-std-b67" or "hlg",
            Normalize(h.ColorPrimaries) is "bt2020" or "2020",
            1f / (float)settings.ReferenceWhiteNits, (float)(settings.PeakNits / settings.ReferenceWhiteNits),
            (float)(1 / (1 - HdrToneMapping.Knee) - 1 / (settings.PeakNits / settings.ReferenceWhiteNits - HdrToneMapping.Knee)),
            2 * (1 - kr) * cf, 2 * (1 - kb) * cf,
            2 * kb * (1 - kb) / kg * cf, 2 * kr * (1 - kr) / kg * cf);
        byte[] output = destination ?? new byte[luma * 4];
        if (h.Chroma == Y4mChroma.C420)
        {
            int expandedSize = width * ch;
            float[] expanded = ArrayPool<float>.Shared.Rent(expandedSize * 2);
            try
            {
                Parallel.For(0, ch, y => ExpandChromaRow(y, color.ChromaLocation == "center", width, cw,
                    luma, chroma, expandedSize, bytes, neutral, frame.RawPixels, expanded));
                Parallel.For(0, height, y =>
                {
                    Axis(y, color.ChromaLocation != "topleft", ch, out int y0, out int y1, out double phase);
                    HdrRow(y, width, luma, bytes, black, yf, parameters, frame.RawPixels, output, expanded,
                        expandedSize, y0 * width, y1 * width, (float)phase, cw, chroma, neutral, false, false);
                });
            }
            finally { ArrayPool<float>.Shared.Return(expanded); }
        }
        else
            Parallel.For(0, height, y => HdrRow(y, width, luma, bytes, black, yf, parameters, frame.RawPixels,
                output, null, 0, 0, 0, 0, cw, chroma, neutral, h.Chroma == Y4mChroma.C422,
                color.ChromaLocation == "center", h.Chroma == Y4mChroma.Mono));
        return output;
    }

    private readonly record struct HdrParameters(bool Hlg, bool Bt2020, float InverseWhite, float Peak,
        float Shoulder, float RedCr, float BlueCb, float GreenCb, float GreenCr);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void HdrRow(int y, int width, int luma, int bytes, float black, float yf,
        HdrParameters p, byte[] input, byte[] output, float[]? expanded, int expandedSize,
        int row0, int row1, float phase, int cw, int chroma, int neutral, bool sub422,
        bool centered, bool mono = false)
    {
        int row = y * width, x = 0;
        if (expanded != null && System.Runtime.Intrinsics.X86.Avx2.IsSupported)
            x = HdrVectorRow(y, width, bytes, black, yf, p, input, output, expanded, expandedSize, row0, row1, phase);
        int target = (row + x) * 4;
        for (; x < width; ++x)
        {
            float cb = 0, cr = 0;
            if (expanded != null)
            {
                cb = expanded[row0 + x] + (expanded[row1 + x] - expanded[row0 + x]) * phase;
                cr = expanded[expandedSize + row0 + x]
                    + (expanded[expandedSize + row1 + x] - expanded[expandedSize + row0 + x]) * phase;
            }
            else if (sub422)
            {
                Axis(x, centered, cw, out int x0, out int x1, out double f);
                int index = y * cw;
                float u = Sample(input, luma + index + x0, bytes);
                float v = Sample(input, luma + chroma + index + x0, bytes);
                cb = u + (Sample(input, luma + index + x1, bytes) - u) * (float)f - neutral;
                cr = v + (Sample(input, luma + chroma + index + x1, bytes) - v) * (float)f - neutral;
            }
            else if (!mono)
            {
                cb = Sample(input, luma + row + x, bytes) - neutral;
                cr = Sample(input, luma * 2 + row + x, bytes) - neutral;
            }
            float yp = (Sample(input, row + x, bytes) - black) * yf;
            float r = HdrToneMapping.Linearize(yp + p.RedCr * cr, p.Hlg);
            float g = HdrToneMapping.Linearize(yp - p.GreenCb * cb - p.GreenCr * cr, p.Hlg);
            float b = HdrToneMapping.Linearize(yp + p.BlueCb * cb, p.Hlg);
            float normalization = p.InverseWhite;
            // HLGのOOTFは輝度から得た倍率を全チャンネルへ同じように掛け、色相を維持する。
            if (p.Hlg) normalization *= HdrToneMapping.HlgOotf(0.2627f * r + 0.6780f * g + 0.0593f * b);
            r *= normalization; g *= normalization; b *= normalization;
            float maximum = MathF.Max(r, MathF.Max(g, b));
            if (maximum > (float)HdrToneMapping.Knee)
            {
                float delta = maximum - (float)HdrToneMapping.Knee;
                float mapped = maximum >= p.Peak ? 1 : (float)HdrToneMapping.Knee + delta / (1 + p.Shoulder * delta);
                float ratio = mapped / maximum;
                r *= ratio; g *= ratio; b *= ratio;
            }
            if (p.Bt2020)
            {
                // D65の標準原色から求めた線形BT.2020→BT.709。各行の和を1として白を保つ。
                float rr = 1.6604910021f * r - 0.5876411388f * g - 0.0728498633f * b;
                float gg = -0.1245504745f * r + 1.1328998971f * g - 0.0083494226f * b;
                float bb = -0.0181507634f * r - 0.1005788980f * g + 1.1187296614f * b;
                r = rr; g = gg; b = bb;
            }
            output[target++] = HdrToneMapping.Encode(b);
            output[target++] = HdrToneMapping.Encode(g);
            output[target++] = HdrToneMapping.Encode(r);
            output[target++] = 255;
        }
    }
}

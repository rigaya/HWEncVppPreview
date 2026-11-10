using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.CompilerServices;

namespace HWEncVppPreview.Core;

public static partial class YuvToBgra
{
    // 420の展開済み色差を8画素ずつ処理する。非対応CPUと端数は同じ精度のスカラーへ戻る。
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int HdrVectorRow(int y, int width, int bytes, float black, float yf,
        HdrParameters p, byte[] input, byte[] output, float[] expanded, int expandedSize,
        int row0, int row1, float phase)
    {
        var zero = Vector256<float>.Zero;
        var one = Vector256.Create(1f);
        var half = Vector256.Create(0.5f);
        var knee = Vector256.Create((float)HdrToneMapping.Knee);
        var fy = Vector256.Create(yf);
        var offset = Vector256.Create(black);
        var fraction = Vector256.Create(phase);
        var white = Vector256.Create(p.InverseWhite);
        var peak = Vector256.Create(p.Peak);
        var shoulder = Vector256.Create(p.Shoulder);
        var rc = Vector256.Create(p.RedCr); var bc = Vector256.Create(p.BlueCb);
        var gc = Vector256.Create(p.GreenCb); var gr = Vector256.Create(p.GreenCr);
        fixed (byte* source = input, destination = output)
        fixed (float* chroma = expanded)
        fixed (float* transfer = HdrToneMapping.TransferLookup(p.Hlg))
        fixed (float* gamma = HdrToneMapping.GammaLookup)
        fixed (float* ootf = HdrToneMapping.OotfLookup)
        {
            int x = 0;
            for (; x <= width - 8; x += 8)
            {
                int index = y * width + x;
                var yi = bytes == 2
                    ? Avx2.ConvertToVector256Int32(Sse2.LoadVector128((ushort*)(source + index * 2)))
                    : Avx2.ConvertToVector256Int32(Sse2.LoadScalarVector128((long*)(source + index)).AsByte());
                var yp = Avx.Multiply(Avx.Subtract(Avx.ConvertToVector256Single(yi), offset), fy);
                var u0 = Avx.LoadVector256(chroma + row0 + x);
                var v0 = Avx.LoadVector256(chroma + expandedSize + row0 + x);
                var cb = Avx.Add(u0, Avx.Multiply(Avx.Subtract(Avx.LoadVector256(chroma + row1 + x), u0), fraction));
                var cr = Avx.Add(v0, Avx.Multiply(Avx.Subtract(Avx.LoadVector256(chroma + expandedSize + row1 + x), v0), fraction));
                var r = LookupVector(transfer, HdrToneMapping.TransferLookupSize, Avx.Add(yp, Avx.Multiply(rc, cr)));
                var g = LookupVector(transfer, HdrToneMapping.TransferLookupSize,
                    Avx.Subtract(Avx.Subtract(yp, Avx.Multiply(gc, cb)), Avx.Multiply(gr, cr)));
                var b = LookupVector(transfer, HdrToneMapping.TransferLookupSize, Avx.Add(yp, Avx.Multiply(bc, cb)));
                var normalization = white;
                if (p.Hlg)
                {
                    var luminance = Avx.Add(Avx.Add(Avx.Multiply(r, Vector256.Create(0.2627f)),
                        Avx.Multiply(g, Vector256.Create(0.6780f))), Avx.Multiply(b, Vector256.Create(0.0593f)));
                    normalization = Avx.Multiply(normalization, LookupVector(ootf, HdrToneMapping.TransferLookupSize,
                        Avx.Sqrt(Avx.Min(one, Avx.Max(zero, luminance)))));
                }
                r = Avx.Multiply(r, normalization); g = Avx.Multiply(g, normalization); b = Avx.Multiply(b, normalization);
                var maximum = Avx.Max(r, Avx.Max(g, b));
                var delta = Avx.Max(zero, Avx.Subtract(maximum, knee));
                var mapped = Avx.Add(knee, Avx.Divide(delta, Avx.Add(one, Avx.Multiply(shoulder, delta))));
                mapped = Avx.BlendVariable(mapped, one, Avx.Compare(maximum, peak, FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling));
                var ratio = Avx.BlendVariable(one, Avx.Divide(mapped, maximum),
                    Avx.Compare(maximum, knee, FloatComparisonMode.OrderedGreaterThanNonSignaling));
                r = Avx.Multiply(r, ratio); g = Avx.Multiply(g, ratio); b = Avx.Multiply(b, ratio);
                if (p.Bt2020)
                {
                    var rr = Avx.Subtract(Avx.Subtract(Avx.Multiply(r, Vector256.Create(1.6604910021f)),
                        Avx.Multiply(g, Vector256.Create(0.5876411388f))), Avx.Multiply(b, Vector256.Create(0.0728498633f)));
                    var gg = Avx.Subtract(Avx.Add(Avx.Multiply(r, Vector256.Create(-0.1245504745f)),
                        Avx.Multiply(g, Vector256.Create(1.1328998971f))), Avx.Multiply(b, Vector256.Create(0.0083494226f)));
                    var bb = Avx.Add(Avx.Subtract(Avx.Multiply(r, Vector256.Create(-0.0181507634f)),
                        Avx.Multiply(g, Vector256.Create(0.1005788980f))), Avx.Multiply(b, Vector256.Create(1.1187296614f)));
                    r = rr; g = gg; b = bb;
                }
                r = LookupVector(gamma, HdrToneMapping.GammaLookupSize, Avx.Sqrt(Avx.Min(one, Avx.Max(zero, r))));
                g = LookupVector(gamma, HdrToneMapping.GammaLookupSize, Avx.Sqrt(Avx.Min(one, Avx.Max(zero, g))));
                b = LookupVector(gamma, HdrToneMapping.GammaLookupSize, Avx.Sqrt(Avx.Min(one, Avx.Max(zero, b))));
                var ri = Avx.ConvertToVector256Int32WithTruncation(Avx.Add(r, half));
                var gi = Avx.ConvertToVector256Int32WithTruncation(Avx.Add(g, half));
                var bi = Avx.ConvertToVector256Int32WithTruncation(Avx.Add(b, half));
                var packed = Avx2.Or(Vector256.Create(unchecked((int)0xff000000)),
                    Avx2.Or(Avx2.ShiftLeftLogical(ri, 16), Avx2.Or(Avx2.ShiftLeftLogical(gi, 8), bi)));
                Avx.Store((int*)(destination + index * 4), packed);
            }
            return x;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<float> LookupVector(float* table, int size, Vector256<float> input)
    {
        var position = Avx.Multiply(Avx.Min(Vector256.Create(1f), Avx.Max(Vector256<float>.Zero, input)), Vector256.Create((float)size));
        var index = Avx2.Min(Avx.ConvertToVector256Int32WithTruncation(position), Vector256.Create(size - 1));
        var a = Avx2.GatherVector256(table, index, 4);
        var b = Avx2.GatherVector256(table, Avx2.Add(index, Vector256.Create(1)), 4);
        return Avx.Add(a, Avx.Multiply(Avx.Subtract(b, a), Avx.Subtract(position, Avx.ConvertToVector256Single(index))));
    }
}

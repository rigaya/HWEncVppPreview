using System.Runtime.CompilerServices;

namespace HWEncParamViewer.Core;

/// <summary>HDRの表示だけに使う基準白と入力ピーク。エンコーダのパラメータには渡さない。</summary>
public sealed record HdrDisplaySettings
{
    public double ReferenceWhiteNits { get; init; } = 203;
    public double PeakNits { get; init; } = 1000;

    internal void Validate()
    {
        if (!double.IsFinite(ReferenceWhiteNits) || ReferenceWhiteNits is < 50 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(ReferenceWhiteNits));
        if (!double.IsFinite(PeakNits) || PeakNits < ReferenceWhiteNits)
            throw new ArgumentOutOfRangeException(nameof(PeakNits));
    }
}

/// <summary>BT.2100の伝達特性と、白付近から滑らかに圧縮する表示用の肩。</summary>
public static class HdrToneMapping
{
    public const double Knee = 0.75;
    private const int TransferSize = 16384;
    private const int GammaSize = 8192;
    private static readonly float[] PqTable = MakeTable(TransferSize, PqToNits);
    private static readonly float[] HlgTable = MakeTable(TransferSize, HlgInverseOetf);
    private static readonly float[] OotfTable = MakeTable(TransferSize, x => 1000 * Math.Pow(x * x, 0.2));
    private static readonly float[] GammaTable = MakeTable(GammaSize, x => 255 * Math.Pow(x * x, 1 / 2.4));

    /// <summary>ST 2084のEOTF。信号の範囲外は飽和させ、表示線形のnitを返す。</summary>
    public static double PqToNits(double signal)
    {
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 32;
        const double c1 = 3424.0 / 4096, c2 = 2413.0 / 128, c3 = 2392.0 / 128;
        double p = Math.Pow(Math.Clamp(signal, 0, 1), 1 / m2);
        return 10000 * Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
    }

    /// <summary>HLGの逆OETF。色付きRGBでは、この後に輝度由来の共通OOTFを適用する。</summary>
    public static double HlgInverseOetf(double signal)
    {
        const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
        signal = Math.Clamp(signal, 0, 1);
        return signal <= 0.5 ? signal * signal / 3 : (Math.Exp((signal - c) / a) + b) / 12;
    }

    /// <summary>公称1000nit、システムガンマ1.2における中立HLG信号の表示輝度。</summary>
    public static double HlgToNits(double signal) => 1000 * Math.Pow(HlgInverseOetf(signal), 1.2);

    /// <summary>基準白で割った輝度を圧縮する。膝で値と傾きがつながり、入力ピークで1になる。</summary>
    public static double MapLuminance(double normalized, double peakNormalized)
    {
        if (!double.IsFinite(peakNormalized) || peakNormalized < 1)
            throw new ArgumentOutOfRangeException(nameof(peakNormalized));
        if (normalized <= Knee) return Math.Max(0, normalized);
        if (normalized >= peakNormalized) return 1;
        double delta = normalized - Knee;
        double a = 1 / (1 - Knee) - 1 / (peakNormalized - Knee);
        return Knee + delta / (1 + a * delta);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Linearize(float signal, bool hlg) => Lookup(hlg ? HlgTable : PqTable, TransferSize, signal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float HlgOotf(float luminance) => Lookup(OotfTable, TransferSize, MathF.Sqrt(Math.Clamp(luminance, 0, 1)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Encode(float linear)
    {
        // 平方根の座標で補間し、暗部を密にする。線形座標の粗い表で黒付近の階段を作らない。
        float encoded = Lookup(GammaTable, GammaSize, MathF.Sqrt(Math.Clamp(linear, 0, 1)));
        return (byte)Math.Clamp((int)(encoded + 0.5f), 0, 255);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lookup(float[] table, int size, float value)
    {
        float position = Math.Clamp(value, 0, 1) * size;
        int first = Math.Min((int)position, size - 1);
        return table[first] + (table[first + 1] - table[first]) * (position - first);
    }

    internal const int TransferLookupSize = TransferSize;
    internal const int GammaLookupSize = GammaSize;
    internal static float[] TransferLookup(bool hlg) => hlg ? HlgTable : PqTable;
    internal static float[] OotfLookup => OotfTable;
    internal static float[] GammaLookup => GammaTable;

    private static float[] MakeTable(int size, Func<double, double> function)
    {
        var table = new float[size + 1];
        for (int i = 0; i <= size; ++i) table[i] = (float)function((double)i / size);
        return table;
    }
}

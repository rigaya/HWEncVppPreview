namespace HWEncParamViewer.Core;

/// <summary>取得した安全域の境界だけに、転送時の時刻量子化を吸収する許容幅を適用する。</summary>
public static class PreviewSafeRange
{
    // 6桁のXts同士とXdurの独立した丸め誤差（最大1.5µs）を一度だけ吸収する。
    // 元のXtsや境界値は丸めず、半duration選択や目標の同一性判定には使わない。
    public const double ToleranceSeconds = 2e-6;

    public static bool Contains(double seconds, double start, double end)
        => seconds >= start - ToleranceSeconds && seconds <= end + ToleranceSeconds;

    public static bool IsAfter(double seconds, double end) => seconds > end + ToleranceSeconds;
}

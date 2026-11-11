namespace HWEncVppPreview.Core;

/// <summary>取得した安全域の境界だけに、転送時の時刻量子化を吸収する許容幅を適用する。</summary>
public static class PreviewSafeRange
{
    // 6桁のXts同士とXdurの独立した丸め誤差（最大1.5µs）を一度だけ吸収する。
    // 元のXtsや境界値は丸めず、半duration選択や目標の同一性判定には使わない。
    public const double ToleranceSeconds = 2e-6;

    public static bool Contains(double seconds, double start, double end)
        => seconds >= start - ToleranceSeconds && seconds <= end + ToleranceSeconds;

    public static bool IsAfter(double seconds, double end) => seconds > end + ToleranceSeconds;

    /// <summary>ログのDurationは小数2桁へ丸められるため、自然EOFの表示時間との最大5msの差だけを許す。</summary>
    public static bool IsKnownInputEnd(PreviewRequest request, PreviewSegmentSnapshot segment, double target)
    {
        if (!segment.ReachedInputEnd || segment.Frames.Count == 0
            || request.InputDurationSeconds is not double end || !double.IsFinite(end)
            || Math.Abs(target - end) > 1e-8) return false;
        var last = segment.Frames[^1];
        double start = segment.Clock.ToInputSeconds(last, segment.Frames.Count - 1, request.Fps);
        return Contains(start, segment.SafeStartSeconds, segment.SafeEndSeconds)
            && start <= end + ToleranceSeconds
            && start + FrameSelector.Duration(last, request.Fps) >= end - 0.005 - ToleranceSeconds;
    }
}

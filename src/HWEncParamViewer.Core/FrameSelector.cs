namespace HWEncParamViewer.Core;

/// <summary>1フレームの時刻情報 (FRAME 行の Xts / Xdur)。無ければ null。</summary>
public readonly record struct FrameTiming(double? Xts, double? Xdur);

/// <summary>目標時刻に表示されているフレームを選ぶ (設計書 3.3)。</summary>
public static class FrameSelector
{
    /// <summary>出力ヘッダを優先し、不明な場合だけ要求のfpsを使う。両方不明なら0。</summary>
    public static double FrameRate(Y4mFrame frame, double requestedFps)
        => double.IsFinite(frame.Header.Fps) && frame.Header.Fps > 0 ? frame.Header.Fps
            : double.IsFinite(requestedFps) && requestedFps > 0 ? requestedFps : 0;

    /// <summary>許容幅に使う表示時間。有効なXdur、出力ヘッダのfps、要求のfpsの順で求める。</summary>
    public static double Duration(Y4mFrame frame, double requestedFps)
    {
        if (frame.Xdur is double duration && double.IsFinite(duration) && duration > 0) return duration;
        double fps = FrameRate(frame, requestedFps);
        return fps > 0 ? 1 / fps : 0;
    }

    /// <summary>各フレームの出力ヘッダを使い、レートが変わるフィルタでも同じ規則で選ぶ。</summary>
    public static int Select(IReadOnlyList<Y4mFrame> frames, double preMargin, double requestedFps)
    {
        double fps = frames.Count > 0 ? FrameRate(frames[0], requestedFps) : requestedFps;
        return Select(frames.Select(frame => new FrameTiming(frame.Xts, Duration(frame, requestedFps))).ToArray(), preMargin, fps);
    }

    /// <summary>
    /// Xts ≤ preMargin を満たす中で Xts が最大のフレームの位置を返す。見つからなければ -1。
    /// 浮動小数の誤差を吸収するため、各フレームの Xdur/2 (Xdur が無ければ fps から 1/(2fps)) の許容幅を持たせる。
    /// 全フレームに Xts が無いときは、fps からフレーム番号で選ぶ。
    /// </summary>
    /// <param name="preMargin">実際の前マージン (PreviewPlan.ActualPreMargin)。出力の Xts は --seek 位置を 0 とする前提。</param>
    /// <param name="fps">Xts / Xdur が無いときの代替。0 以下なら許容幅 0。</param>
    public static int Select(IReadOnlyList<FrameTiming> frames, double preMargin, double fps)
    {
        double fallbackDur = fps > 0 ? 1.0 / fps : 0.0;

        if (frames.All(f => f.Xts == null))
        {
            if (fps <= 0) return -1;
            // Xts 版の「i/fps ≤ Mb + 0.5/fps で最大の i」と同じ規則
            int idx = (int)Math.Floor(preMargin * fps + 0.5 + 1e-9);
            return idx >= 0 && idx < frames.Count ? idx : -1;
        }

        int best = -1;
        double bestTs = double.NegativeInfinity;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Xts is not double ts) continue;
            double tol = (frames[i].Xdur ?? fallbackDur) / 2.0;
            if (ts <= preMargin + tol && ts > bestTs)
            {
                best = i;
                bestTs = ts;
            }
        }
        return best;
    }
}

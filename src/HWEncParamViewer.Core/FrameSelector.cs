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

    /// <summary>表示に使用できる範囲だけから、入力先頭基準の目標を選ぶ。</summary>
    public static int Select(PreviewSegmentSnapshot segment, double inputSeconds, double requestedFps)
    {
        var indices = Enumerable.Range(0, segment.Frames.Count).Where(index =>
        {
            double time = segment.Clock.ToInputSeconds(segment.Frames[index], index, requestedFps);
            return PreviewSafeRange.Contains(time, segment.SafeStartSeconds, segment.SafeEndSeconds);
        }).ToArray();
        // 入力時計へ写像してから選ぶことで、prefix削除後や表示可能範囲の抽出後も番号を失わない。
        bool missing = segment.Frames.All(frame => frame.Xts == null);
        var timings = indices.Select(index => new FrameTiming(
            missing || segment.Frames[index].Xts != null
                ? segment.Clock.ToInputSeconds(segment.Frames[index], index, requestedFps) : null,
            Duration(segment.Frames[index], requestedFps))).ToArray();
        int selected = Select(timings, inputSeconds, requestedFps);
        return selected < 0 ? -1 : indices[selected];
    }

    /// <summary>参照が同じ物理区間に属するかを判定する。rollingによるsnapshot更新も同じ区間とみなす。</summary>
    public static bool FromSameSegment(PreviewFrameReference first, PreviewFrameReference second)
    {
        if (first.SegmentId != 0 && second.SegmentId != 0) return first.SegmentId == second.SegmentId;
        return ReferenceEquals(first.Segment, second.Segment)
            || ReferenceEquals(first.Segment.PhysicalRequest, second.Segment.PhysicalRequest);
    }

    /// <summary>別取得のグリッド差を、両出力フレームの短い方の半durationまで同一とみなす。</summary>
    public static bool AreEquivalent(PreviewFrameReference first, PreviewFrameReference second)
    {
        if (!FrameCache.SameContent(first.Segment.PhysicalRequest, second.Segment.PhysicalRequest)) return false;
        if (FromSameSegment(first, second)) return ReferenceEquals(first.Frame, second.Frame);
        return first.Segment.CanStitch && second.Segment.CanStitch && HaveSameTimestamp(first, second);
    }

    // 画素照合前にはCanStitchがまだfalseなので、時計の対応付けだけを独立させる。
    internal static bool HaveSameTimestamp(PreviewFrameReference first, PreviewFrameReference second)
    {
        if (!FrameCache.SameContent(first.Segment.PhysicalRequest, second.Segment.PhysicalRequest)
            || !first.Clock.TrustedAbsolute || !second.Clock.TrustedAbsolute
            || first.Frame.Xts is not double a || !double.IsFinite(a)
            || second.Frame.Xts is not double b || !double.IsFinite(b)) return false;
        double duration = Math.Min(Duration(first.Frame, first.Segment.PhysicalRequest.Fps),
            Duration(second.Frame, second.Segment.PhysicalRequest.Fps));
        return duration > 0 && Math.Abs(first.InputSeconds - second.InputSeconds) <= duration / 2 + 1e-9;
    }

    /// <summary>安全な別区間の重複だけをまとめた移動用viewを作る。同一区間内の隣と生時計を保つ。</summary>
    public static IReadOnlyList<PreviewFrameReference> MergeNavigationFrames(IEnumerable<PreviewFrameReference> frames)
    {
        var groups = new List<List<PreviewFrameReference>>();
        var safe = frames.Where(x => x.IsSafe && x.Clock.TrustedAbsolute && x.Segment.CanStitch)
            .OrderBy(x => x.SegmentId).ThenBy(x => x.FrameIndex).ToArray();
        var bounds = NavigationBounds(safe);
        // 古い安全な代表を安定して使う。近接の鎖を推移的に結合しない。
        foreach (var frame in safe)
        {
            double half = Duration(frame.Frame, frame.Segment.PhysicalRequest.Fps) / 2 + 1e-9;
            double time = frame.InputSeconds;
            List<PreviewFrameReference>? match = null;
            // 同一とみなせる代表は候補自身の半duration以内に限る。全代表の総当たりを避ける。
            for (int i = Bound(groups, time - half, upper: false); i < groups.Count && groups[i][0].InputSeconds <= time + half; ++i)
            {
                var group = groups[i];
                double representative = group[0].InputSeconds;
                var neighbors = bounds[frame];
                bool preservesOrder = !(representative < time - 1e-9 && neighbors.Before is double before && representative <= before + 1e-9)
                    && !(representative > time + 1e-9 && neighbors.After is double after && representative >= after - 1e-9);
                bool equivalent = AreEquivalent(group[0], frame);
                if (!preservesOrder && equivalent)
                {
                    // 密VFRで代表が真の隣を跨ぐ場合、対応付けを決め打ちせず最新物理区間へ戻す。
                    var latest = safe[^1];
                    return Array.AsReadOnly(safe.Where(candidate => FromSameSegment(candidate, latest))
                        .Select(candidate => new PreviewFrameReference(candidate.Segment with { CanStitch = false }, candidate.FrameIndex)).OrderBy(candidate => candidate.InputSeconds).ToArray());
                }
                if (preservesOrder && equivalent
                    && !group.Any(member => FromSameSegment(member, frame) && !ReferenceEquals(member.Frame, frame.Frame)))
                { match = group; break; }
            }
            if (match != null) match.Add(frame);
            else groups.Insert(Bound(groups, time, upper: true), new() { frame });
        }
        return Array.AsReadOnly(groups.Select(group => group[0]).ToArray());

        static int Bound(List<List<PreviewFrameReference>> groups, double time, bool upper)
        {
            int low = 0, high = groups.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                double candidate = groups[middle][0].InputSeconds;
                if (candidate < time || upper && candidate == time) low = middle + 1;
                else high = middle;
            }
            return low;
        }
    }

    private static Dictionary<PreviewFrameReference, (double? Before, double? After)> NavigationBounds(PreviewFrameReference[] frames)
    {
        var numbered = new Dictionary<long, List<PreviewFrameReference>>();
        var provisional = new Dictionary<PreviewRequest, List<PreviewFrameReference>>(ReferenceEqualityComparer.Instance);
        foreach (var frame in frames)
        {
            List<PreviewFrameReference>? source;
            if (frame.SegmentId != 0)
            {
                if (!numbered.TryGetValue(frame.SegmentId, out source)) numbered.Add(frame.SegmentId, source = new());
            }
            else if (!provisional.TryGetValue(frame.Segment.PhysicalRequest, out source))
                provisional.Add(frame.Segment.PhysicalRequest, source = new());
            source.Add(frame);
        }
        var result = new Dictionary<PreviewFrameReference, (double? Before, double? After)>();
        foreach (var source in numbered.Values.Concat(provisional.Values))
        {
            var unique = source.GroupBy(frame => frame.Frame, ReferenceEqualityComparer.Instance).Select(group => group.First())
                .OrderBy(frame => frame.InputSeconds).ThenBy(frame => frame.FrameIndex).ToArray();
            var byFrame = new Dictionary<Y4mFrame, (double? Before, double? After)>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < unique.Length; ++i)
                byFrame.Add(unique[i].Frame, (i > 0 ? unique[i - 1].InputSeconds : null,
                    i + 1 < unique.Length ? unique[i + 1].InputSeconds : null));
            foreach (var frame in source) result[frame] = byFrame[frame.Frame];
        }
        return result;
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

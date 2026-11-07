using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void ReleaseObsoleteRequestSnapshots(long generation)
    {
        foreach (var context in _afterRequests.Values)
            if (context.Generation < generation) context.Segment = null;
        if (_context is { } current && current.Generation < generation) current.Segment = null;
        Interlocked.Exchange(ref _pendingSegmentUpdate, null);
    }

    private void ReleaseIntervalSnapshotsForSubmission()
    {
        // 新しい取得の間も画像は残すが、過去の区間全体をVM側から固定しない。
        // 移動の基準1枚はcacheから再解決するために残す。cache内のコマ移動はこの経路を通らない。
        var anchor = _navigationAnchor;
        ClearInterval();
        _navigationAnchor = anchor;
        ClearBeforeInterval();
    }

    private void RefreshRetainedSnapshots()
    {
        if (_intervalSegment is { SegmentId: > 0 } after)
        {
            var latest = _cache.GetSegment(after.SegmentId);
            if (latest == null) ClearInterval();
            else { _intervalSegment = latest; _intervalFrames = latest.Frames; }
        }
        if (_beforeIntervalSegment is { SegmentId: > 0 } before)
        {
            var latest = _cache.GetSegment(before.SegmentId);
            if (latest == null) ClearBeforeInterval();
            else { _beforeIntervalSegment = latest; _beforeIntervalFrames = latest.Frames; }
        }
        long generation = Volatile.Read(ref _generation);
        foreach (var context in _afterRequests.Values)
            RefreshContext(context, context.Generation < generation);
        if (_context is { } current) RefreshContext(current, current.Generation < generation);
        if (_beforeExecutionContext is { } background) RefreshContext(background, false);

        void RefreshContext(RequestContext context, bool obsolete)
        {
            if (obsolete) { context.Segment = null; return; }
            if (context.Segment is not { SegmentId: > 0 } held) return;
            // evict後も実範囲の情報は残し、存在しない画像を計画上の窓で再利用可能と見なさない。
            context.Segment = _cache.GetSegment(held.SegmentId)
                ?? held with { Frames = Array.Empty<Y4mFrame>() };
        }
    }

    private PreviewSegmentSnapshot RetainEvictedBeforeCompletion(PreviewSegmentSnapshot segment, double target)
    {
        int selected = ContainsBeforeFrameTarget(segment.PhysicalRequest, segment.Frames, target, segment)
            ? FrameSelector.Select(segment, target, segment.PhysicalRequest.Fps) : -1;
        // 失われたcacheのIDや接続性は復活させない。元番号はRetainFrameOnlyの時計へ引き継ぐ。
        var retained = selected >= 0 ? new PreviewFrameReference(segment, selected).RetainFrameOnly().Segment
            : segment with { Frames = Array.Empty<Y4mFrame>() };
        return retained with { SegmentId = 0, CanStitch = false, IsActive = false };
    }

    private PreviewSegmentSnapshot RetainEvictedAfterCompletion(PreviewSegmentSnapshot segment, RequestContext context)
    {
        var uncached = segment with { SegmentId = 0, CanStitch = false, IsActive = false };
        var request = context.Request;
        int first = -1, count = 0;
        if (context.NeighborDirection != 0 && !context.NeighborResolved)
        {
            if (uncached.Clock.TrustedAbsolute)
            {
                // 半durationで選ぶ中心ではなく、通常の隣移動と同じ実基準を使う。
                var navigation = NavigationFrames(request, uncached);
                int anchor = NavigationAnchorIndex(navigation, request.TargetSeconds, request.Fps);
                if (anchor >= 0)
                {
                    int start = Math.Max(0, anchor - 1);
                    int end = Math.Min(navigation.Count - 1, anchor + 1);
                    var retained = new PreviewFrameReference(uncached, navigation[start].FrameIndex).RetainFrameOnly().Segment;
                    return retained with { Frames = Array.AsReadOnly(navigation.Skip(start).Take(end - start + 1)
                        .Select(reference => reference.Frame).ToArray()) };
                }
            }
            else
            {
                int anchor = NeighborAnchorIndex(uncached.Frames,
                    uncached.Clock.ToSelectionSeconds(request.TargetSeconds, uncached.Frames, request.Fps), request.Fps);
                if (anchor >= 0 && PreviewSafeRange.Contains(uncached.Clock.ToInputSeconds(uncached.Frames[anchor],
                    anchor, request.Fps), uncached.SafeStartSeconds, uncached.SafeEndSeconds))
                {
                    first = Math.Max(0, anchor - 1);
                    int last = Math.Min(uncached.Frames.Count - 1, anchor + 1);
                    // legacy側もunsafeの隣を除き、trustedの移動viewと同じ境界判定へ渡す。
                    if (first < anchor && !PreviewSafeRange.Contains(uncached.Clock.ToInputSeconds(uncached.Frames[first],
                        first, request.Fps), uncached.SafeStartSeconds, uncached.SafeEndSeconds)) first = anchor;
                    if (last > anchor && !PreviewSafeRange.Contains(uncached.Clock.ToInputSeconds(uncached.Frames[last],
                        last, request.Fps), uncached.SafeStartSeconds, uncached.SafeEndSeconds)) last = anchor;
                    count = last - first + 1;
                }
            }
        }
        else
        {
            double target = context.ResolvedTargetSeconds ?? CurrentSeconds;
            if (PreviewSafeRange.Contains(target, uncached.SafeStartSeconds, uncached.SafeEndSeconds)
                && ContainsFrameTarget(uncached.PhysicalRequest, uncached.Frames, target, uncached.Clock))
            {
                first = FrameSelector.Select(uncached, target, request.Fps);
                count = first >= 0 ? 1 : 0;
            }
        }
        if (first < 0 || count == 0) return uncached with { Frames = Array.Empty<Y4mFrame>() };
        // 元番号は先頭1枚の時計へ移し、旧窓全体のsnapshotはVMへ戻さない。
        return new PreviewFrameReference(uncached, first).RetainFrameOnly().Segment with
            { Frames = Array.AsReadOnly(uncached.Frames.Skip(first).Take(count).ToArray()) };
    }

    private static PreviewSegmentSnapshot? RetainSelectedFrame(PreviewSegmentSnapshot? segment, Y4mFrame frame)
    {
        if (segment == null) return null;
        for (int i = 0; i < segment.Frames.Count; i++)
            if (ReferenceEquals(segment.Frames[i], frame)) return new PreviewFrameReference(segment, i).RetainFrameOnly().Segment;
        return segment with { Frames = Array.Empty<Y4mFrame>() };
    }
}

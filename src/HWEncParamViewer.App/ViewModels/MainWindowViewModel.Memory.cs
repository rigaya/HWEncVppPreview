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

    private static PreviewSegmentSnapshot? RetainSelectedFrame(PreviewSegmentSnapshot? segment, Y4mFrame frame)
    {
        if (segment == null) return null;
        for (int i = 0; i < segment.Frames.Count; i++)
            if (ReferenceEquals(segment.Frames[i], frame)) return new PreviewFrameReference(segment, i).RetainFrameOnly().Segment;
        return segment with { Frames = Array.Empty<Y4mFrame>() };
    }
}

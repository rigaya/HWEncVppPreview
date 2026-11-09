using Avalonia.Threading;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private PreviewCapabilities _capabilities = new();
    private Task _metadataTask = Task.CompletedTask;
    private double _lastIntentTarget;
    private bool _latestIntentReverse;
    private PreviewSegmentSnapshot? _intervalSegment, _beforeIntervalSegment;
    private OwnedUiPost? _pendingSegmentUpdate;
    private int _segmentPostQueued;
    private PreviewFrameReference? _navigationAnchor;
    private double _navigationAnchorTarget;

    private void OnSegmentUpdated(PreviewSegmentUpdate update) => OnSegmentUpdatedWithWindow(update, null);

    private void OnSegmentUpdatedWithWindow(PreviewSegmentUpdate update, OwnedUncachedWindow? source)
    {
        var context = ContextFor(update.Request);
        if (_disposed || context?.Kind != RequestKind.After || context.Generation != Volatile.Read(ref _generation)) return;
        // 先読みの各フレームでUIキューに区間全体を積まず、未反映の最新状態だけを保持する。
        // 正IDはUI到着時にcacheを読み直す。待機中はmetadataだけ、ID0の有限窓だけ所有する。
        var frames = update.Segment.SegmentId > 0 ? Array.Empty<Y4mFrame>() : update.Segment.Frames;
        OwnedUiPost pending;
        if (source == null) pending = CaptureRawPost(context, frames, () => ApplySegment(update));
        else
        {
            // 通知側が確保した同じ窓をUIへ渡し、待機するたびに画素の参照を増やさない。
            var shared = source.ShareIfSame(source.Frames)!;
            pending = CaptureOwnershipPost(context, shared, () => ApplySegmentWithWindow(update, shared));
        }
        Interlocked.Exchange(ref _pendingSegmentUpdate, pending)?.Dispose();
        QueueSegmentPost();
    }

    private void QueueSegmentPost()
    {
        if (!_disposed && Interlocked.CompareExchange(ref _segmentPostQueued, 1, 0) == 0)
            Dispatcher.UIThread.Post(ApplyPendingSegment);
    }

    private void ApplyPendingSegment()
    {
        var update = Interlocked.Exchange(ref _pendingSegmentUpdate, null);
        Interlocked.Exchange(ref _segmentPostQueued, 0);
        update?.Execute();
        if (Volatile.Read(ref _pendingSegmentUpdate) != null) QueueSegmentPost();
    }

    private void ApplySegment(PreviewSegmentUpdate update) => ApplySegmentWithWindow(update, null);

    private void ApplySegmentWithWindow(PreviewSegmentUpdate update, OwnedUncachedWindow? source)
    {
        var context = ContextFor(update.Request);
        if (context == null || context.Kind != RequestKind.After || !IsCurrent(context)
            || context.Generation != Volatile.Read(ref _generation)) return;
        RefreshRetainedSnapshots();
        if (update.Segment.SegmentId > 0)
        {
            var latest = _cache.GetSegment(update.Segment.SegmentId);
            if (latest == null) return;
            // UIへ渡るまでにprefixが進むため、通知時点の古い配列ではなく現在のcacheを保持する。
            update = update with { Segment = latest };
        }
        context.SetSegmentSharing(update.Segment, source);
        _intervalSegment = update.Segment;
        _intervalRequest = update.Segment.PhysicalRequest;
        _intervalLogicalRequest = update.Request;
        SetIntervalFrames(update.Segment.Frames, context);
        foreach (var old in _afterRequests.Where(pair => pair.Key < context.Generation && pair.Value.Request.Continuous).ToArray())
            _afterRequests.TryRemove(old.Key, out _);
        bool satisfied = update.DemandSatisfied;
        if (context.NeighborDirection != 0 && !context.NeighborResolved)
        {
            var navigation = NavigationFrames(context.Request, update.Segment);
            bool available;
            if (navigation.Count > 0)
            {
                int anchor = NavigationAnchorIndex(navigation, context.Request.TargetSeconds, context.Request.Fps);
                int next = anchor + context.NeighborDirection;
                available = anchor >= 0 && next >= 0 && next < navigation.Count
                    && NavigationNeighborsConnected(navigation[anchor], navigation[next]);
            }
            else if (!update.Segment.Clock.TrustedAbsolute)
            {
                var clock = update.Segment.Clock;
                int anchor = NeighborAnchorIndex(_intervalFrames, clock.ToSelectionSeconds(context.Request.TargetSeconds, _intervalFrames, context.Request.Fps), context.Request.Fps);
                int next = anchor + context.NeighborDirection;
                available = anchor >= 0 && next >= 0 && next < _intervalFrames.Count
                    && FrameIsSafe(clock.ToInputSeconds(_intervalFrames[next], next, context.Request.Fps));
            }
            else available = false;
            if (available || !update.Segment.IsActive)
                _ = CompleteNeighbor(context, _intervalFrames);
            satisfied = context.NeighborResolved;
        }
        if (satisfied)
        {
            _lastCompletedAfter = Math.Max(_lastCompletedAfter, context.Generation);
            if (context.NeighborDirection == 0 || context.ResolvedTargetSeconds.HasValue)
                _completedAfterStatus = AppMessages.Done;
            ErrorText = null;
        }
        if (update.IsReadPaused && satisfied && context.Generation == Volatile.Read(ref _generation))
            QueueBefore(RetargetInterval(context.Request, CurrentSeconds), context.Generation);
        UpdateNavigationStatus();
    }

    internal IReadOnlyList<PreviewFrameReference> NavigationFrames(PreviewRequest request, PreviewSegmentSnapshot? segment)
    {
        if (segment?.Clock.TrustedAbsolute != true) return Array.Empty<PreviewFrameReference>();
        // 候補選びではdescriptorだけを読む。画素は採用する1枚のTry取得が成功してから使う。
        var current = segment.SegmentId > 0 ? _cache.GetSegment(segment.SegmentId) : segment;
        if (current == null) return Array.Empty<PreviewFrameReference>();
        if (!current.CanStitch)
            return Enumerable.Range(0, current.Frames.Count).Select(index => new PreviewFrameReference(current, index))
                .Where(frame => frame.IsSafe).ToArray();
        var navigation = _cache.GetNavigationFrames(request);
        if (current.SegmentId > 0 && !CacheHasSegment(current.SegmentId))
        {
            ValidateNavigationState();
            return Array.Empty<PreviewFrameReference>();
        }
        return navigation;
    }

    private bool CacheHasSegment(long segmentId) => _cache.ContainsSegment(segmentId);

    private bool IsConnectedNavigationTarget(double target, double previous)
    {
        var logical = _intervalLogicalRequest ?? _intervalRequest;
        if (logical == null) return false;
        var range = _cache.GetConnectedSafeRange(logical with { TargetSeconds = previous });
        if (range is { } connected && PreviewSafeRange.Contains(target, connected.Start, connected.End)) return true;
        // 照合禁止でも同じ物理区間内は再利用できる。別の離れたcacheへ時刻だけで接続しない。
        var segment = _intervalSegment is { SegmentId: > 0 } current
            ? _cache.GetSegment(current.SegmentId) : _intervalSegment;
        return segment != null && PreviewSafeRange.Contains(previous, segment.SafeStartSeconds, segment.SafeEndSeconds)
            && PreviewSafeRange.Contains(target, segment.SafeStartSeconds, segment.SafeEndSeconds);
    }

    private void ValidateNavigationState()
    {
        RefreshRetainedSnapshots();
        if (_navigationAnchor is { SegmentId: > 0 } anchor && !CacheHasSegment(anchor.SegmentId))
            SetRawAnchor(ref _navigationAnchor, ref _anchorRaw, null);
        if (_intervalSegment is { SegmentId: > 0 } interval && !CacheHasSegment(interval.SegmentId))
            ClearInterval();
        if (_beforeIntervalSegment is { SegmentId: > 0 } before && !CacheHasSegment(before.SegmentId))
            ClearBeforeInterval();
    }

    private int NavigationAnchorIndex(IReadOnlyList<PreviewFrameReference> frames, double target, double fps)
    {
        // 変換完了を待たずに確定した目標を基準にし、表示中の古い画像へ戻らない。
        if (_navigationAnchor is { } anchor && Math.Abs(target - _navigationAnchorTarget) <= 1e-8)
        {
            var current = anchor.SegmentId > 0 ? _cache.GetSegment(anchor.SegmentId) : anchor.Segment;
            var refreshed = FindNavigationAnchor(current, anchor);
            // index計算ではanchorをcommitしない。既存1枚の所有は、次の選択取得成功後まで保持する。
            if (refreshed != null)
            {
                for (int i = 0; i < frames.Count; ++i)
                    if (ReferenceEquals(refreshed.Frame, frames[i].Frame)) return i;
                int nearest = -1;
                double distance = double.PositiveInfinity;
                for (int i = 0; i < frames.Count; ++i)
                {
                    if (!FrameSelector.AreEquivalent(refreshed, frames[i])) continue;
                    double currentDistance = Math.Abs(refreshed.InputSeconds - frames[i].InputSeconds);
                    if (currentDistance < distance) { nearest = i; distance = currentDistance; }
                }
                if (nearest >= 0) return nearest;
            }
        }
        for (int i = 0; i < frames.Count; ++i)
            if (Math.Abs(frames[i].InputSeconds - target) <= 1e-8) return i;
        return FrameSelector.Select(frames.Select(frame => new FrameTiming(frame.InputSeconds,
            FrameSelector.Duration(frame.Frame, frame.Segment.PhysicalRequest.Fps))).ToArray(), target, fps);
    }

    private static PreviewFrameReference? FindNavigationAnchor(PreviewSegmentSnapshot? current, PreviewFrameReference anchor)
    {
        if (current != null)
            for (int i = 0; i < current.Frames.Count; ++i)
                if (ReferenceEquals(current.Frames[i], anchor.Frame)) return new PreviewFrameReference(current, i).RetainFrameOnly();
        return null;
    }

    private static bool NavigationNeighborsConnected(PreviewFrameReference first, PreviewFrameReference second)
    {
        if (FrameSelector.FromSameSegment(first, second)) return true;
        // 離れたキャッシュ窓の間を1コマで飛ばさない。判定不能なら有限の隣取得へ戻る。
        double tolerance = Math.Min(FrameSelector.Duration(first.Frame, first.Segment.PhysicalRequest.Fps),
            FrameSelector.Duration(second.Frame, second.Segment.PhysicalRequest.Fps)) / 2;
        return first.Segment.SafeStartSeconds <= second.Segment.SafeEndSeconds + tolerance
            && second.Segment.SafeStartSeconds <= first.Segment.SafeEndSeconds + tolerance;
    }

    private void AdoptNavigationReference(PreviewFrameReference frame)
    {
        _intervalSegment = frame.Segment;
        _intervalRequest = frame.Segment.PhysicalRequest;
        SetIntervalFrames(frame.Segment.Frames);
        SetRawAnchor(ref _navigationAnchor, ref _anchorRaw, frame.RetainFrameOnly());
        _navigationAnchorTarget = frame.InputSeconds;
    }

    private void RememberNavigationAnchor(Y4mFrame frame, RequestContext context)
    {
        if (context.Segment is not { Clock.TrustedAbsolute: true } segment) return;
        for (int i = 0; i < segment.Frames.Count; ++i)
        {
            if (!ReferenceEquals(segment.Frames[i], frame)) continue;
            SetRawAnchor(ref _navigationAnchor, ref _anchorRaw, new PreviewFrameReference(segment, i).RetainFrameOnly());
            _navigationAnchorTarget = context.ResolvedTargetSeconds ?? context.Request.TargetSeconds;
            return;
        }
    }

    private bool FrameIsSafe(double seconds) => _intervalSegment == null
        || PreviewSafeRange.Contains(seconds, _intervalSegment.SafeStartSeconds, _intervalSegment.SafeEndSeconds);

    private PreviewClockMap IntervalClock(PreviewRequest request, IReadOnlyList<Y4mFrame> frames)
        => _intervalSegment != null && SameImageSource(_intervalSegment.PhysicalRequest, request)
            ? _intervalSegment.Clock : ClockFor(request, frames);

    private PreviewClockMap ClockFor(PreviewRequest request, IReadOnlyList<Y4mFrame> frames)
    {
        double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
        double? first = frames.FirstOrDefault()?.Xts;
        bool aligned = !_controller.IsLegacySource(request) && request.TimestampOriginTrusted && request.Capabilities.TimestampPassthrough && request.TimestampOriginSeconds is double origin
            && double.IsFinite(origin) && first is double timestamp && double.IsFinite(timestamp)
            && timestamp >= origin - 1e-6 && timestamp - origin >= seek - 0.5;
        return new(request.TimestampOriginSeconds, seek, aligned);
    }

    private bool IsKnownLegacyPhysicalSource(PreviewRequest physical, PreviewRequest logical)
    {
        if (_intervalSegment?.Clock.TrustedAbsolute != false || physical.Capabilities.TimestampPassthrough
            || _intervalLogicalRequest is not { } saved || !_controller.IsLegacySource(saved)) return false;
        if (FrameCache.SameContent(saved, logical)) return true;
        // F0共有だけは、論理VPPに置かれた時刻flagをBeforeが除いた差も認める。
        return physical.VppArgs.Count == 0 && logical.VppArgs.Count == 0
            && saved.EncoderPath == logical.EncoderPath && saved.InputFile == logical.InputFile
            && saved.InputArgs.SequenceEqual(logical.InputArgs) && saved.Capabilities == logical.Capabilities
            && saved.TimestampOriginSeconds == logical.TimestampOriginSeconds
            && SameArgumentsWithoutPassthrough(saved.VppArgs, logical.VppArgs);
    }

    private static bool SameArgumentsWithoutPassthrough(IReadOnlyList<string> first, IReadOnlyList<string> second)
        => first.Where(value => !value.Equals("--timestamp-passthrough", StringComparison.Ordinal))
            .SequenceEqual(second.Where(value => !value.Equals("--timestamp-passthrough", StringComparison.Ordinal)));

    private bool SameImageSource(PreviewRequest a, PreviewRequest b) => a.EncoderPath == b.EncoderPath
        && a.InputFile == b.InputFile && a.TimestampOriginSeconds == b.TimestampOriginSeconds
        && (a.InputArgs.SequenceEqual(b.InputArgs) && a.VppArgs.SequenceEqual(b.VppArgs)
            || IsKnownLegacyPhysicalSource(a, b) && SameArgumentsWithoutPassthrough(a.InputArgs, b.InputArgs)
                && SameArgumentsWithoutPassthrough(a.VppArgs, b.VppArgs))
        && (a.Capabilities == b.Capabilities || IsKnownLegacyPhysicalSource(a, b)
            && a.Capabilities.OpenClTaskThreads == b.Capabilities.OpenClTaskThreads);
}

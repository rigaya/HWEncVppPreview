namespace HWEncParamViewer.Core;

public sealed partial class PreviewController
{
    private const double ForwardPrefetchThresholdSeconds = 1.5;
    // 同じ境界の再発行を防ぎ、内容変更や明示更新の既存取消で解除する。
    private PreviewRequest? _lastForwardPrefetch;

    // UIへ新しい表示要求を作らず、境界の通常順windowと現在の論理位置を分ける。
    private void TryStartForwardPrefetchCore(PreviewRequest logical)
    {
        if (_disposed || _stream != null || _foregroundPending != 0
            || _reversePrefetch is { } reverse && !reverse.Completion.Task.IsCompleted
            || IsLegacySource(logical) || !logical.Capabilities.TimestampPassthrough
            || logical.TimestampOriginSeconds is not double origin || !double.IsFinite(origin)
            || !_cache.CanStitch(logical)) return;
        var range = _cache.GetConnectedSafeRange(logical);
        if (range == null || !double.IsFinite(range.Value.End)
            || !PreviewSafeRange.Contains(logical.TargetSeconds, range.Value.Start, range.Value.End)
            || range.Value.End - logical.TargetSeconds > ForwardPrefetchThresholdSeconds + 1e-7
            || logical.InputDurationSeconds is double duration && range.Value.End >= duration - 1e-7) return;
        double boundary = range.Value.End;
        if (_lastForwardPrefetch is { } previous && FrameCache.SameContent(previous, logical)
            && Math.Abs(previous.TargetSeconds - boundary) <= 1e-7) return;
        var start = logical with { TargetSeconds = boundary, Continuous = true, SeekSecondsOverride = null,
            // cache移動で増えた物理余白や逆有限窓を通常の順方向windowへ戻す。
            PreMarginSeconds = PreviewRequest.DefaultPreMarginSeconds, PostMarginSeconds = PreviewRequest.DefaultPostMarginSeconds,
            InputArgs = logical.InputArgs.ToArray(), VppArgs = logical.VppArgs.ToArray() };
        _lastForwardPrefetch = start;
        var cancellation = new CancellationTokenSource();
        var scheduled = new CancellationTokenSource();
        var physical = PhysicalAfterRequest(start, false) with { Continuous = true };
        var session = new StreamingSession(physical, start, _version, true, cancellation, CancellationToken.None)
        { LatestRequest = logical, IsForwardPrefetch = true, Satisfied = true };
        // 既存cacheで成立した表示需要を保ち、先読みの読取待機を未解決表示として待たない。
        session.Demand.TrySetResult();
        _stream = session;
        _scheduled = scheduled;
        PulseStreamState();
        var completion = NewSignal();
        session.PhysicalCompletion = completion.Task;
        _tasks.Add(completion.Task);
        if (PerfTrace.Enabled) PerfTrace.Write("core.forward_prefetch", new { logical.CorrelationId,
            target = logical.TargetSeconds, boundary });
        _ = TrackAsync(RunStreamAsync(session, scheduled, true), completion);
    }

    private bool TryKeepForwardPrefetchCore(StreamingSession session, PreviewRequest request,
        PreviewSegmentSnapshot? cached, PreviewSegmentSnapshot? physicalSegment, bool bypassCache, bool navigation,
        CancellationToken externalToken)
    {
        if (!session.IsForwardPrefetch || !navigation || bypassCache || cached == null || IsLegacySource(request)
            || !FrameCache.SameContent(session.PhysicalRequest, request) || !_cache.CanStitch(request)) return false;
        var range = _cache.GetConnectedSafeRange(session.LatestRequest);
        if (range == null || !PreviewSafeRange.Contains(request.TargetSeconds, range.Value.Start, range.Value.End))
            return false;
        // 先読み側に照合済みの実中心があれば通常streamへ合流し、それまでは旧表示区間を保つ。
        // 接続済み範囲内のcache移動は順逆を問わず保持し、逆先読みの実行枠要求は別経路で判定する。
        if (physicalSegment is { CanStitch: true, Clock.TrustedAbsolute: true }
            && PreviewSafeRange.Contains(request.TargetSeconds, physicalSegment.SafeStartSeconds, physicalSegment.SafeEndSeconds)
            && FrameSelector.Select(physicalSegment, request.TargetSeconds, request.Fps) >= 0) return false;
        // 表示元を確保できないcacheでは先読みの需要を書き換えず、通常のmiss処理へ戻す。
        if (!TryPrepareSegmentFrame(request, cached, externalToken, out var frame, out var owned)) return false;
        using (owned)
        {
            session.LatestRequest = request;
            session.Version = ++_version;
            session.Navigation = true;
            session.ExternalToken = externalToken;
            if (externalToken.CanBeCanceled) session.Registrations.Add(externalToken.Register(() => session.Cancellation.Cancel()));
            _cache.PinCurrent(cached.SegmentId, request.TargetSeconds);
            return PublishPreparedSegmentSelection(frame, owned, session.Version, true, false, _lastEditVersion, true);
        }
    }
}

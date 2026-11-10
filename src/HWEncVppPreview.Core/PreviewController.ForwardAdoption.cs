namespace HWEncVppPreview.Core;

public sealed partial class PreviewController
{
    private readonly ForwardStartupHistory _forwardStartups = new();

    private void BeginForwardObservation(StreamingSession session, IPreviewBackend backend)
    {
        lock (_sync)
        {
            session.ObservationBackend = backend;
            session.BackendCallStarted = _clock.GetTimestamp();
            session.BackendFirstObserved = false;
            session.ReadProgress.SetReading(false);
        }
    }

    private bool TryAdoptApproachingForward(StreamingSession? active, PreviewSegmentSnapshot? current,
        PreviewRequest request, bool reuseActiveStream, bool bypassCache, bool navigation, bool capable, bool withinJump, bool notBeforeRetained)
    {
        if (!reuseActiveStream || !bypassCache || !navigation || !capable || !withinJump || !notBeforeRetained
            || !request.TimestampOriginTrusted || active is not { IsForwardPrefetch: true, IsPaused: false, Ended: false, ClockChecked: true }
            || current is not { IsActive: true, CanStitch: true, Clock.TrustedAbsolute: true } || current.Frames.Count == 0
            || active.ObservationBackend == null || !active.ReadProgress.Reading
            || !FrameCache.SameContent(active.PhysicalRequest, request)) return false;
        var last = current.Frames[^1];
        if (last.Xts is not double timestamp || !double.IsFinite(timestamp)) return false;
        double lastInput = current.Clock.ToInputSeconds(last, current.Frames.Count - 1, request.Fps);
        if (!(request.TargetSeconds > lastInput)) return false;
        if (!_forwardStartups.TryMinimum(_clock, active.ObservationBackend, active.PhysicalRequest, last.Header, out double restart)
            || !active.ReadProgress.TryEstimate(_clock, request.TargetSeconds, restart, out double eta, out double rate, out double age, out double gap)) return false;
        // bypassで中心を再通知しないが、待ち始める前に旧中心の実在と接続を確認する。
        if (!_cache.TryGetAt(request, request.TargetSeconds, out var center) || center.SegmentId == current.SegmentId) return false;
        int selected = FrameSelector.Select(center, request.TargetSeconds, request.Fps);
        if (selected < 0 || Math.Abs(center.Clock.ToInputSeconds(center.Frames[selected], selected, request.Fps) - request.TargetSeconds)
            > FrameSelector.Duration(center.Frames[selected], request.Fps) / 2 + 1e-7) return false;
        if (!_cache.AreDirectlyConnected(current.SegmentId, center.SegmentId)) return false;
        // CanAppendは古いprefixを回収する場合があるため、他の許可条件を確認してから呼ぶ。
        if (!_cache.CanAppend(active.SegmentId, active.LastFrameBytes)) return false;
        if (PerfTrace.Enabled) PerfTrace.Write("core.stream.forward_adopt", new
        {
            active.SegmentId, request.CorrelationId, request.TargetSeconds, lastInput,
            etaSeconds = eta, inputSecondsPerSecond = rate, ageSeconds = age, maximumInputGap = gap,
            backendCallToFirstSeconds = restart,
        });
        return true;
    }
}

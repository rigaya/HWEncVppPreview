namespace HWEncVppPreview.Core;

public sealed partial class PreviewController
{
    private Task? QueueEmptyReplay(StreamingSession session)
    {
        lock (_sync)
        {
            if (_disposed || session.ExternalToken.IsCancellationRequested || session.Version != _version
                || !PreviewWindowRecovery.TryBacktrackEmpty(session.LatestRequest, session.PhysicalRequest, 0, out var expanded)) return null;
            _cache.RemoveSegment(session.SegmentId);
            if (ReferenceEquals(_stream, session)) _stream = null;
            PulseStreamState();
            var scheduled = CancellationTokenSource.CreateLinkedTokenSource(session.ExternalToken);
            _scheduled = scheduled;
            if (_foregroundPending++ == 0) _foregroundIdle = NewSignal();
            var completion = NewSignal();
            _tasks.Add(completion.Task);
            _ = TrackAsync(ScheduleAsync(expanded with { Continuous = false }, session.Version, scheduled,
                bypassCache: true, bypassDebounce: true, session.Navigation, background: false, _lastEditVersion,
                session.ExternalToken, emptyRecoveryAttempt: 1), completion);
            return completion.Task;
        }
    }

    // streamの窓が目標より後ろへ着地したときは、backendの終了を待ってから同じ所有世代の有限窓へ戻す。
    private Task? QueueWindowReplay(StreamingSession session, bool withoutOutputStart = false)
    {
        lock (_sync)
        {
            if (_disposed || session.ExternalToken.IsCancellationRequested || session.Version != _version
                || session.WindowShortfall is not { } shortage) return null;
            var logical = session.LatestRequest;
            PreviewRequest expanded;
            if (withoutOutputStart && !session.PhysicalRequest.DisableOutputStart)
            {
                expanded = logical with { Continuous=false, RecoveryPhysicalWindow=session.PhysicalRequest with
                    { TargetSeconds=logical.TargetSeconds, CorrelationId=logical.CorrelationId, Continuous=false, DisableOutputStart=true } };
                if (PerfTrace.Enabled) PerfTrace.Write("core.output_start_replay",new{logical.CorrelationId,seek=CommandBuilder.Plan(session.PhysicalRequest).SeekSeconds});
            }
            else if (!PreviewWindowRecovery.TryExpand(logical, shortage, 0, out expanded))
            {
                Notify(session.Version, session.Navigation, () => Failed?.Invoke(new(logical, CoreText.FrameUnavailable)
                    { WindowShortfall = shortage }), true);
                return null;
            }
            _cache.RemoveSegment(session.SegmentId);
            if (ReferenceEquals(_stream, session)) _stream = null;
            PulseStreamState();
            var scheduled = CancellationTokenSource.CreateLinkedTokenSource(session.ExternalToken);
            _scheduled = scheduled;
            if (_foregroundPending++ == 0) _foregroundIdle = NewSignal();
            var completion = NewSignal();
            _tasks.Add(completion.Task);
            // 最初の拡張はここで消費済み。有限経路へ回復回数も引き継ぐ。
            _ = TrackAsync(ScheduleAsync(expanded, session.Version, scheduled, bypassCache: true,
                bypassDebounce: true, session.Navigation, background: false, _lastEditVersion,
                session.ExternalToken, isReplay: withoutOutputStart, recoveryAttempt: withoutOutputStart ? 0 : 1), completion);
            return completion.Task;
        }
    }
}

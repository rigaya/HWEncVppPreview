namespace HWEncParamViewer.Core;

public sealed partial class PreviewController
{
    private const double ReversePrefetchThresholdSeconds = 1.5;
    private const double ReversePrefetchWarmupSeconds = PreviewWarmupPolicy.DefaultWarmupSeconds;
    private const double ReversePrefetchDisplaySeconds = 3;
    // 末尾処理のguardとは別に、安全な画素照合の重なりを0.5秒残す。
    private const double ReversePrefetchGuardSeconds = PreviewByteWindow.TailGuardSeconds;
    private const double ReversePrefetchOverlapSeconds = 0.5;
    private ReversePrefetchState? _reversePrefetch;
    private PreviewRequest? _lastNavigationRequest;
    private int _reverseSteps;
    private double _reverseDistance;
    private static long _prefetchSerial;

    /// <summary>受理した実際のコマ移動を通知する。キャッシュ内の移動も対象。方向は負が逆、正が順方向。</summary>
    /// <remarks>UIの世代は変更しない。単独1コマでは待機中の連続取得を取り消さない。</remarks>
    public void NotifyNavigation(PreviewRequest logical, int direction)
    {
        ArgumentNullException.ThrowIfNull(logical);
        lock (_sync)
        {
            if (_disposed) return;
            if (_foregroundSource == null || !FrameCache.SameContent(_foregroundSource, logical))
            { CancelReversePrefetchCore(); return; }
            var previous = _lastNavigationRequest;
            if (previous != null && Math.Abs(logical.TargetSeconds - previous.TargetSeconds) > 3)
            { CancelReversePrefetchCore(); _lastNavigationRequest = logical; return; }
            _lastNavigationRequest = logical with { InputArgs = logical.InputArgs.ToArray(), VppArgs = logical.VppArgs.ToArray() };
            if (direction >= 0)
            {
                _reverseSteps = 0;
                _reverseDistance = 0;
                if (direction > 0) TryStartForwardPrefetchCore(logical);
                return;
            }
            ++_reverseSteps;
            if (previous != null) _reverseDistance += Math.Max(0, previous.TargetSeconds - logical.TargetSeconds);
            TryStartReversePrefetchCore();
        }
    }

    /// <summary>境界へ到達した移動が待つ、同じ入力・処理の逆方向先読み。保存と画素照合まで含む。</summary>
    /// <remarks>呼出側の待機取消はこのtask自体を取り消さず、WaitAsyncに渡すこと。</remarks>
    public Task? GetPendingReversePrefetch(PreviewRequest logical)
    {
        lock (_sync)
        {
            if (_reversePrefetch is not { } state || state.Completion.Task.IsCompleted || state.Cancellation.IsCancellationRequested
                || !FrameCache.SameContent(state.Request, logical)) return null;
            var physical = state.Request.RecoveryPhysicalWindow ?? state.Request;
            double start = PreviewWarmupPolicy.SafeStartSeconds(physical, false);
            return logical.TargetSeconds >= start - 1e-7 && logical.TargetSeconds <= state.Boundary + ReversePrefetchThresholdSeconds + 1e-7
                ? state.Completion.Task : null;
        }
    }

    private void TryStartReversePrefetchCore()
    {
        if (_disposed || _reverseSteps == 0 || _lastNavigationRequest is not { } logical || IsLegacySource(logical)
            || !logical.Capabilities.TimestampPassthrough || !logical.TimestampOriginSeconds.HasValue || !_cache.CanStitch(logical)) return;
        var range = _cache.GetConnectedSafeRange(logical);
        if (range == null || range.Value.Start <= 1e-7 || logical.TargetSeconds - range.Value.Start > ReversePrefetchThresholdSeconds + 1e-7) return;
        double boundary = range.Value.Start;
        if (_reversePrefetch is { } prior && FrameCache.SameContent(prior.Request, logical))
        {
            // 同じ窓は完了済みでも再発行しない。失敗時の端では通常の要求単位取得に任せる。
            if (!prior.Completion.Task.IsCompleted || Math.Abs(prior.Boundary - boundary) <= 1e-7) return;
        }
        var stream=_stream is { } activeStream && !activeStream.Cancellation.IsCancellationRequested?activeStream:null;
        if(stream!=null && (!stream.IsPaused || !(_reverseSteps>=5 || _reverseSteps>=2 && _reverseDistance>=0.2-1e-8)))return;
        // 同じ_sync下なので、pause中streamがtrim後にAppendする前に取消を確定できる。
        // 窓とbridgeが容量に収まらない場合、元streamもcacheもそのまま残す。
        if(!TryPrepareReverseWindow(logical,boundary,out var planned))
        {
            if(PerfTrace.Enabled)PerfTrace.Write("core.reverse_prefetch.skip",new{boundary,logical.TargetSeconds,reason="byte_budget_or_bridge"});
            return;
        }
        if(stream!=null)
        {
            // 区間更新は記述子だけを渡し、UIが使う画素は選択時に取得する。
            var trimmed = _cache.GetSegment(stream.SegmentId);
            if (trimmed != null)
                Notify(stream.Version, stream.Navigation, () => EmitSegment(new(stream.LatestRequest, trimmed, true, true)), true);
            CancelStreamCore("reverse_prefetch");
        }
        if (_reversePrefetch is { } previous) previous.Cancellation.Dispose();
        long correlationId=long.MinValue+Interlocked.Increment(ref _prefetchSerial);
        var request=planned with {CorrelationId=correlationId,
            RecoveryPhysicalWindow=planned.RecoveryPhysicalWindow! with {CorrelationId=correlationId}};
        var state = new ReversePrefetchState(request, boundary);
        _reversePrefetch = state;
        var scheduled = CancellationTokenSource.CreateLinkedTokenSource(state.Cancellation.Token);
        if (_foregroundPending++ == 0) _foregroundIdle = NewSignal();
        _tasks.Add(state.Completion.Task);
        if (PerfTrace.Enabled) PerfTrace.Write("core.reverse_prefetch", new { request.CorrelationId, boundary,
            seek = CommandBuilder.Plan(request.RecoveryPhysicalWindow ?? request).SeekSeconds,
            end = boundary + request.PostMarginSeconds, request.RetainedByteLimit });
        _ = TrackAsync(ScheduleAsync(request, _version, scheduled, bypassCache: true, bypassDebounce: true,
            navigation: true, background: false, _lastEditVersion, state.Cancellation.Token, prefetch: state), state.Completion);
    }

    private void CancelReversePrefetchCore()
    {
        if (_reversePrefetch is { } state)
        {
            if (!state.Completion.Task.IsCompleted) state.Cancellation.Cancel();
            _ = DisposeReversePrefetchCancellationAsync(state);
        }
        _reversePrefetch = null;
        _lastNavigationRequest = null;
        _lastForwardPrefetch = null;
        _reverseSteps = 0;
        _reverseDistance = 0;
    }

    private static async Task DisposeReversePrefetchCancellationAsync(ReversePrefetchState state)
    {
        await ObserveShutdownTaskAsync(state.Completion.Task).ConfigureAwait(false);
        state.Cancellation.Dispose();
    }

    private sealed class ReversePrefetchState(PreviewRequest request, double boundary)
    {
        public PreviewRequest Request { get; } = request;
        public double Boundary { get; } = boundary;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = NewSignal();
    }
}

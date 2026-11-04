namespace HWEncParamViewer.Core;

public sealed partial class PreviewController
{
    private const double ReversePrefetchThresholdSeconds = 1.5;
    private const double ReversePrefetchWarmupSeconds = PreviewWarmupPolicy.DefaultWarmupSeconds;
    private const double ReversePrefetchDisplaySeconds = 3;
    // 末尾処理のguardとは別に、安全な画素照合の重なりを0.5秒残す。
    private const double ReversePrefetchGuardSeconds = 0.5;
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
            double start = Math.Max(0, state.Boundary - ReversePrefetchDisplaySeconds);
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
            prior.Cancellation.Dispose();
        }
        if (_stream is { } stream && !stream.Cancellation.IsCancellationRequested)
        {
            if (!stream.IsPaused || !(_reverseSteps >= 5 || _reverseSteps >= 2 && _reverseDistance >= 0.2 - 1e-8)) return;
            // 協調cancel→ENDでAfterの実行枠を空けてから、同じbackendで有限取得する。
            CancelStreamCore();
        }
        var request = logical with
        {
            CorrelationId = long.MinValue + Interlocked.Increment(ref _prefetchSerial),
            TargetSeconds = boundary, Continuous = false, SeekSecondsOverride = null,
            PreMarginSeconds = ReversePrefetchWarmupSeconds + ReversePrefetchDisplaySeconds,
            PostMarginSeconds = ReversePrefetchGuardSeconds + ReversePrefetchOverlapSeconds,
        };
        var state = new ReversePrefetchState(request, boundary);
        _reversePrefetch = state;
        var scheduled = CancellationTokenSource.CreateLinkedTokenSource(state.Cancellation.Token);
        if (_foregroundPending++ == 0) _foregroundIdle = NewSignal();
        _tasks.Add(state.Completion.Task);
        if (PerfTrace.Enabled) PerfTrace.Write("core.reverse_prefetch", new { request.CorrelationId, boundary,
            seek = Math.Max(0, boundary - 5), end = boundary + ReversePrefetchGuardSeconds + ReversePrefetchOverlapSeconds });
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
        public PreviewSegmentSnapshot? Segment { get; set; }
    }
}

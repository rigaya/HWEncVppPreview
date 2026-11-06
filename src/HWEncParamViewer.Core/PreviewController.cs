namespace HWEncParamViewer.Core;

public sealed record PreviewFrameResult(PreviewRequest Request, Y4mFrame Frame, bool TargetNotReached)
{
    public PreviewSegmentSnapshot? Segment { get; init; }
    public bool IsReplay { get; init; }
}
public sealed record PreviewCompletion(PreviewRequest Request, PreviewRunResult Result, bool FromCache, bool TargetNotReached)
{
    /// <summary>成功した区間の全フレーム。LRUに保存されない区間でも、現在位置の前後を選べるようにする。</summary>
    public IReadOnlyList<Y4mFrame> Frames { get; init; } = Array.Empty<Y4mFrame>();
    public PreviewSegmentSnapshot? Segment { get; init; }
    public bool IsReplay { get; init; }
}
public sealed record PreviewFailure(PreviewRequest Request, string Reason, PreviewRunResult? Result = null, Exception? Exception = null)
{
    /// <summary>失敗区分・元の例外を変えず、通常ログへ追加する診断。なければ従来のstderrを使う。</summary>
    public string? Diagnostic { get; init; }
    /// <summary>表示文言に依存せず、watchdogの期限切れを識別する。</summary>
    public bool TimedOut { get; init; }
}

/// <summary>最新要求の実行、画像選択、キャッシュを管理する。イベントは処理中のスレッドで通知するため、GUI側でディスパッチする。</summary>
/// <remarks>
/// イベントは内部ロックを保持して通知する。ハンドラはすぐに戻ること。
/// UIスレッドを同期的に待たず、Postで非同期にディスパッチすること。Dispatcher.Invoke等の同期呼び出しはSubmitとデッドロックしうる。
/// 受け取った画像のDataはキャッシュと共有するので変更しないこと。DisposeAsyncで実行中の子プロセスの回収を試みる。期限内に完了しない場合はShutdownIncompleteと警告に残す。
/// </remarks>
public sealed partial class PreviewController : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _execution = new(1);
    private readonly SemaphoreSlim _backgroundExecution = new(1);
    private readonly IPreviewBackend _backgroundPreferred;
    private readonly IPreviewBackend _backgroundFallback;
    private CancellationTokenSource? _backgroundActive;
    private readonly bool _separateBackground;
    private SemaphoreSlim ExecutionFor(bool background) => background && _separateBackground ? _backgroundExecution : _execution;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _watchdog;
    private readonly TimeSpan _disposeGrace;
    private readonly TimeSpan _disposeTimeout;
    private int _shutdownIncomplete;
    /// <summary>終了期限までに回収できない処理が残った場合はtrue。子プロセス回収完了を意味しない。</summary>
    public bool ShutdownIncomplete => Volatile.Read(ref _shutdownIncomplete) != 0;
    private readonly FrameCache _cache;
    private readonly IPreviewBackend _preferred;
    private readonly IPreviewBackend _fallback;
    private readonly bool _enableFallback;
    private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _tasks = new();
    private readonly Dictionary<IPreviewBackend, List<BackendRun>> _backendRuns = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _scheduled;
    private CancellationTokenSource? _backgroundScheduled;
    private long _backgroundVersion;
    private int _foregroundPending;
    private TaskCompletionSource _foregroundIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _active;
    private long _version;
    private long _lastEditVersion;
    private long _lastNotifiedVersion;
    private bool _disposed;
    private Task? _disposeTask;

    public PreviewController(IPreviewBackend? backend = null, IPreviewBackend? fallback = null,
        FrameCache? cache = null, TimeProvider? timeProvider = null,
        TimeSpan? debounce = null, TimeSpan? watchdog = null, bool enableFallback = true,
        IPreviewBackend? backgroundBackend = null, IPreviewBackend? backgroundFallback = null, TimeSpan? disposeGrace = null, TimeSpan? disposeTimeout = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        _watchdog = watchdog ?? TimeSpan.FromSeconds(30);
        // 通常のcancel/ENDでGPU資源を閉じる機会を設け、応答しない実行だけを強制終了する。
        _disposeGrace = disposeGrace ?? TimeSpan.FromSeconds(2);
        _disposeTimeout = disposeTimeout ?? TimeSpan.FromSeconds(5);
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        if (_watchdog <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(watchdog));
        if (_disposeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(disposeTimeout));
        if (_disposeGrace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(disposeGrace));
        _preferred = backend ?? new ServerBackend(_clock);
        _fallback = fallback ?? new OneShotBackend(timeProvider: _clock, stopTimeout: _disposeTimeout);
        // 実プロセスは最初のBefore miss時に起動する。注入backendは従来テストの既定を保つ。
        _backgroundPreferred = backgroundBackend ?? (backend == null ? new ServerBackend(_clock) : _preferred);
        _backgroundFallback = backgroundFallback ?? (fallback == null && backend == null ? new OneShotBackend(timeProvider: _clock, stopTimeout: _disposeTimeout) : _fallback);
        _separateBackground = !ReferenceEquals(_backgroundPreferred, _preferred) || !ReferenceEquals(_backgroundFallback, _fallback);
        _enableFallback = enableFallback;
        _cache = cache ?? new FrameCache();
    }

    /// <summary>目標フレームが確定したときに通知する。</summary>
    /// <remarks>ハンドラはすぐに戻ること。UIスレッドを同期的に待たず、Postで非同期にディスパッチすること。</remarks>
    public event Action<PreviewFrameResult>? FrameReady;
    /// <summary>要求が正常終了したときに通知する。</summary>
    /// <remarks>ハンドラはすぐに戻ること。UIスレッドを同期的に待たず、Postで非同期にディスパッチすること。</remarks>
    public event Action<PreviewCompletion>? Completed;
    /// <summary>要求が失敗したときに通知する。</summary>
    /// <remarks>ハンドラはすぐに戻ること。UIスレッドを同期的に待たず、Postで非同期にディスパッチすること。</remarks>
    public event Action<PreviewFailure>? Failed;

    /// <summary>引数リストを複製し、最新要求として登録する。イベント購読はSubmitより前に行う。</summary>
    /// <param name="bypassCache">trueなら過去の画像を使わず実行する。reuseActiveStreamの明示例外を除き、直前の要求が後から保存されても再実行を保証する。</param>
    /// <param name="bypassDebounce">内部の追加要求を即座に実行する場合だけtrue。通常の編集操作は既定の待機時間を使う。</param>
    /// <param name="navigation">移動要求なら待機を省き、有限要求は完了後に最新へ進む。連続取得は物理実行を共有し、最新の表示目標へ更新する。</param>
    /// <param name="background">背景要求なら前景の待機枠・世代を変更せず、前景が終了または意図的に読取待機したときに実行する。編集向けdebounceは適用しない。</param>
    /// <param name="cancellationToken">登録後の待機と実行の両方を取り消す。移動要求の置換による待機取消とは分離する。</param>
    /// <param name="reuseActiveStream">正方向の隣不足だけが明示する例外。bypassCacheでも安全な同contentの生存streamだけを再利用し、新たなcache hitへは戻らない。</param>
    public void Submit(PreviewRequest request, bool bypassCache = false, bool bypassDebounce = false, bool navigation = false,
        bool background = false, CancellationToken cancellationToken = default, bool reuseActiveStream = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { InputArgs = request.InputArgs.ToArray(), VppArgs = request.VppArgs.ToArray() };
        _ = CommandBuilder.Plan(request);
        if (!background)
        {
            PrepareForegroundSource(request);
            lock (_sync)
            {
                if (!navigation || _lastNavigationRequest is { } last && Math.Abs(request.TargetSeconds - last.TargetSeconds) > 3)
                    CancelReversePrefetchCore();
            }
        }
        if (!background && TrySubmitStreaming(request, bypassCache, bypassDebounce, navigation, cancellationToken, reuseActiveStream)) return;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var scheduled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            long version;
            if (background)
            {
                _backgroundScheduled?.Cancel();
                _backgroundScheduled = scheduled;
                version = ++_backgroundVersion;
            }
            else
            {
                _scheduled?.Cancel();
                _scheduled = scheduled;
                version = ++_version;
                if (!navigation) _lastEditVersion = version;
                if (_foregroundPending++ == 0)
                    _foregroundIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            long editVersion = _lastEditVersion;
            if (PerfTrace.Enabled) PerfTrace.Write("core.submit", new { request.CorrelationId, version, navigation, background,
                bypassCache, bypassDebounce, request.TargetSeconds, request.PreMarginSeconds,
                request.PostMarginSeconds, vppArgs = request.VppArgs });
            // 同期完了や通知からのDispose再入より先に、回収対象を登録する。
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tasks.Add(completion.Task);
            _ = TrackAsync(ScheduleAsync(request, version, scheduled, bypassCache, bypassDebounce, navigation,
                background, editVersion, cancellationToken), completion);
        }
    }

    private async Task TrackAsync(Task work, TaskCompletionSource completion)
    {
        try
        {
            await work.ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
        finally { lock (_sync) _tasks.Remove(completion.Task); }
    }

    /// <summary>この時点までの表示需要と有限・背景要求の後処理を待つ。連続取得の物理的な終了は待たない。</summary>
    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task wait;
            StreamingSession? paused = null;
            lock (_sync)
            {
                if (_stream is { } stream)
                {
                    if (stream.IsPaused && stream.Demand.Task.IsCompleted && !stream.Cancellation.IsCancellationRequested)
                    {
                        paused = stream;
                        wait = Task.WhenAll(_tasks.Where(task => !ReferenceEquals(task, stream.PhysicalCompletion)).ToArray());
                    }
                    else wait = Task.WhenAny(stream.PhysicalCompletion, _streamStateChanged.Task);
                }
                else { wait = Task.WhenAll(_tasks.ToArray()); paused = null; }
            }
            await wait.ConfigureAwait(false);
            lock (_sync)
            {
                if (_stream == null && _tasks.All(task => task.IsCompleted)) return;
                if (paused != null && ReferenceEquals(_stream, paused) && paused.IsPaused
                    && !paused.Cancellation.IsCancellationRequested && paused.Demand.Task.IsCompleted
                    && _tasks.Where(task => !ReferenceEquals(task, paused.PhysicalCompletion)).All(task => task.IsCompleted)) return;
            }
        }
    }

    private bool IsCurrent(long version) => !_disposed && version == _version;

    private bool IsRequestedCurrent(long version, bool background, long editVersion) => background
        ? !_disposed && version == _backgroundVersion && editVersion == _lastEditVersion : IsCurrent(version);

    private void NotifyBackground(long version, long editVersion, Action notification)
    {
        lock (_sync)
        {
            // 背景の通知順は前景と別にし、同じ区間内の移動だけでは背景結果を捨てない。
            if (!IsRequestedCurrent(version, true, editVersion))
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.notify_suppressed", new { version, background = true,
                    currentVersion = _backgroundVersion, editVersion, lastEditVersion = _lastEditVersion,
                    reason = _disposed ? "disposed" : "background_replaced_or_edited" });
                return;
            }
            notification();
        }
    }

    private async Task WaitForForegroundAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (CanStartBackgroundCore()) return;
                wait = _foregroundPending > 0 ? _foregroundIdle.Task : _streamStateChanged.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private async Task<(IPreviewBackend Backend, bool? History)> PrepareBackendAsync(
        IPreviewBackend backend, PreviewRequest request, bool background, CancellationToken token)
    {
        try { return (backend, await backend.GetHistoryRequirementAsync(request, token).ConfigureAwait(false)); }
        catch (PreviewServerNotSupportedException) when (_enableFallback
            && ReferenceEquals(backend, background ? _backgroundPreferred : _preferred))
        {
            token.ThrowIfCancellationRequested();
            lock (_sync) _unsupported.Add(request.EncoderPath);
            return (background ? _backgroundFallback : _fallback, null);
        }
    }

    private PreviewRequest PhysicalAfterRequest(PreviewRequest request, bool background)
    {
        if (IsLegacySource(request))
            return request with
            {
                Capabilities = request.Capabilities with { TimestampPassthrough = false }, Continuous = false,
                // 長寿命stream由来の固定seekや増大したmarginを引き継がず、最新目標の短い窓を取り直す。
                SeekSecondsOverride = null, PreMarginSeconds = 3, PostMarginSeconds = 0.5,
                WarmupSeconds = PreviewWarmupPolicy.DefaultWarmupSeconds,
                InputArgs = request.InputArgs.Where(argument => !argument.Equals("--timestamp-passthrough", StringComparison.OrdinalIgnoreCase)).ToArray(),
                VppArgs = request.VppArgs.Where(argument => !argument.Equals("--timestamp-passthrough", StringComparison.OrdinalIgnoreCase)).ToArray(),
            };
        return PreviewWarmupPolicy.PreparePhysical(request, background);
    }

    private bool CanStartBackgroundCore() => _foregroundPending == 0 && (_stream == null || _stream.IsPaused);

    private void Notify(long version, bool navigation, Action notification, bool currentOnly = false)
    {
        // 移動中は先行結果も表示する。ただし編集後の結果と、すでに通知した世代より古い結果は戻さない。
        // 判定と通知を同じ排他区間に置き、通知からSubmitへ再入した場合も順序を守る。
        lock (_sync)
        {
            if (_disposed || version < _lastNotifiedVersion)
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.notify_suppressed", new { version, navigation, currentOnly,
                    currentVersion = _version, lastEditVersion = _lastEditVersion, lastNotifiedVersion = _lastNotifiedVersion,
                    reason = _disposed ? "disposed" : "older_than_notified" });
                return;
            }
            if (!IsCurrent(version) && (currentOnly || !navigation || version < _lastEditVersion))
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.notify_suppressed", new { version, navigation, currentOnly,
                    currentVersion = _version, lastEditVersion = _lastEditVersion, lastNotifiedVersion = _lastNotifiedVersion,
                    reason = currentOnly ? "current_only" : !navigation ? "edit_replaced" : "older_than_edit" });
                return;
            }
            _lastNotifiedVersion = version;
            notification();
        }
    }

    private async Task ScheduleAsync(PreviewRequest request, long version, CancellationTokenSource scheduled,
        bool bypassCache, bool bypassDebounce, bool navigation, bool background, long editVersion,
        CancellationToken cancellationToken, bool isReplay = false, ReversePrefetchState? prefetch = null)
    {
        bool entered = false;
        long waitStart = 0;
        long executionStart = 0;
        long backendStart = 0;
        long pendingSource = 0;
        CancellationTokenSource? active = null;
        bool IsCurrent() => prefetch != null
            ? !_disposed && !scheduled.IsCancellationRequested && ReferenceEquals(_reversePrefetch, prefetch)
            : IsRequestedCurrent(version, background, editVersion);
        void Publish(Action notification, bool currentOnly = false)
        {
            if (prefetch != null || cancellationToken.IsCancellationRequested) return;
            if (background)
            {
                if (!scheduled.IsCancellationRequested) NotifyBackground(version, editVersion, notification);
            }
            else Notify(version, navigation, notification, currentOnly);
        }
        try
        {
            await Task.Delay(background || navigation || bypassDebounce ? TimeSpan.Zero : _debounce, _clock, scheduled.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (!IsCurrent()) return;
                if (!background && !navigation)
                {
                    if (PerfTrace.Enabled && _active != null) PerfTrace.Write("core.cancel_active", new { request.CorrelationId, version });
                    _active?.Cancel();
                    _backgroundActive?.Cancel();
                }
            }
            if (PerfTrace.Enabled)
            {
                waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                PerfTrace.Write("core.execution_wait", new { request.CorrelationId, version });
            }
            while (true)
            {
                if (background) await WaitForForegroundAsync(scheduled.Token).ConfigureAwait(false);
                await ExecutionFor(background).WaitAsync(scheduled.Token).ConfigureAwait(false);
                entered = true;
                if (PerfTrace.Enabled) executionStart = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_sync)
                {
                    if (!IsCurrent()) return;
                    // SemaphoreSlimの待ち順には依存せず、取得と実行登録の間にも前景を優先する。
                    if (!background || CanStartBackgroundCore())
                    {
                        // scheduledを連結すると、次の移動による待機置換まで実行中断へ変わってしまう。
                        active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        if (background) _backgroundActive = active; else _active = active;
                        break;
                    }
                }
                ExecutionFor(background).Release();
                entered = false;
            }
            if (PerfTrace.Enabled)
            {
                PerfTrace.Write("core.execution_enter", new { request.CorrelationId, version, background,
                    waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds });
            }
            var physicalRequest = PhysicalAfterRequest(request, background) with { Continuous = false };
            Action<PreviewFrameResult> emit = frame =>
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.target_frame", new { request.CorrelationId, version,
                    frame.Frame.Xts, frame.TargetNotReached });
                Publish(() => FrameReady?.Invoke(frame with { IsReplay = isReplay }));
            };
            var sink = new SelectionSink(request, emit, background, physicalRequest);
            var physicalKey = physicalRequest with
            { Capabilities = request.Capabilities, InputArgs = request.InputArgs, VppArgs = request.VppArgs };
            if (!bypassCache && !IsLegacySource(request) && request.Capabilities.TimestampPassthrough && request.TimestampOriginSeconds.HasValue
                && _cache.TryGetAt(request, request.TargetSeconds, out var cachedSegment))
            {
                if (!background) _cache.PinCurrent(cachedSegment.SegmentId, request.TargetSeconds);
                PublishSegmentSelection(request, cachedSegment, version, navigation, background, editVersion, cancellationToken, true);
                return;
            }
            if (!bypassCache && (_cache.TryGet(request, out var frames, out var cachedPhysical)
                || _cache.TryGet(physicalKey, out frames, out cachedPhysical)))
            {
                // keyが一致する保存windowを使う。新targetのcanonical窓へ置換すると生Xtsの基準がずれる。
                sink = new SelectionSink(request, emit, background, cachedPhysical);
                if (PerfTrace.Enabled) PerfTrace.Write("core.cache_hit", new { request.CorrelationId, version, frames = frames.Count });
                foreach (var frame in frames) sink.OnFrame(frame);
                sink.Finish();
                if (!sink.HasSelection && prefetch == null && !background && request.Capabilities.TimestampPassthrough && request.TimestampOriginSeconds.HasValue)
                { Publish(() => Failed?.Invoke(new(request, CoreText.FrameUnavailable)), currentOnly: true); return; }
                Publish(() => Completed?.Invoke(new(request, WithLegacyDiagnostic(request, new(0, "")), true, sink.TargetNotReached) { Frames = sink.Frames.AsReadOnly(), Segment = sink.Segment, IsReplay = isReplay }));
                return;
            }
            if (!background && prefetch == null) pendingSource = _cache.BeginPendingSource(request);
            IPreviewBackend backend;
            lock (_sync) backend = _unsupported.Contains(request.EncoderPath)
                ? (background ? _backgroundFallback : _fallback) : (background ? _backgroundPreferred : _preferred);
            bool? history;
            (backend, history) = await PrepareBackendAsync(backend, request, background, active.Token).ConfigureAwait(false);
            physicalRequest = PhysicalAfterRequest(request with { DependsOnHistory = history }, background) with { Continuous = false };
            physicalKey = physicalRequest with
            { Capabilities = request.Capabilities, InputArgs = request.InputArgs, VppArgs = request.VppArgs };
            sink = new SelectionSink(request, emit, background, physicalRequest);
            PreviewRunResult result;
            using var progress = new OutputWatchdog(_clock, _watchdog);
            sink.Progress = progress.Touch;
            sink.ReplayRequired = reason => { MarkLegacySource(request, reason); active.Cancel(); };
            sink.ReadState = reading => { if (reading) progress.Resume(); else progress.Suspend(); };
            if (PerfTrace.Enabled)
            {
                backendStart = System.Diagnostics.Stopwatch.GetTimestamp();
                PerfTrace.Write("core.backend_start", new { request.CorrelationId, version, backend = backend.GetType().Name,
                    timestampPassthrough = physicalRequest.Capabilities.TimestampPassthrough, continuous = physicalRequest.Continuous, isReplay,
                    legacyReason = IsLegacySource(request) ? LegacyReason(request) : null });
            }
            var run = RunWithFallbackAsync();
            var winner = await Task.WhenAny(run, progress.Expired).ConfigureAwait(false);
            if (winner == progress.Expired)
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.watchdog", new { request.CorrelationId, version,
                    frames = sink.Frames.Count, bytes = sink.ReceivedBytes });
                active.Cancel();
                await backend.AbortAsync().ConfigureAwait(false);
                PreviewRunResult? stoppedResult = null;
                Exception? stoppedException = null;
                try { stoppedResult = await run.ConfigureAwait(false); }
                catch (Exception error) { stoppedException = error; }
                Publish(() => Failed?.Invoke(new(request, CoreText.OutputTimedOut)
                    { TimedOut = true, Diagnostic = LegacyFailureDiagnostic(request, stoppedException, stoppedResult) }), currentOnly: true);
                return;
            }
            try { result = await run.ConfigureAwait(false); }
            catch (OperationCanceledException) when (sink.NeedsLegacyReplay)
            { result = new(-12, "", true); }
            result = WithLegacyDiagnostic(request, result);
            if (PerfTrace.Enabled) PerfTrace.Write("core.backend_end", new { request.CorrelationId, version,
                result.ExitCode, result.IsCancelled, frames = sink.Frames.Count, bytes = sink.ReceivedBytes,
                elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(backendStart).TotalMilliseconds });
            if (sink.NeedsLegacyReplay && !scheduled.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _cache.EndPendingSource(pendingSource); pendingSource = 0;
                // 先読みの異常はsourceへ記録済み。画像要求ではないので再取得やUI通知を増やさない。
                if (prefetch != null) return;
                Task? replay = QueueLegacyReplay(request, version, navigation, background, editVersion, cancellationToken);
                if (entered) { ExecutionFor(background).Release(); entered = false; }
                if (replay != null) await replay.ConfigureAwait(false);
                return;
            }
            if (active.IsCancellationRequested || result.IsCancelled) return;
            if (result.ExitCode != 0)
            {
                Publish(() => Failed?.Invoke(new(request, CoreText.EncoderExited(result.ExitCode), result)), currentOnly: true);
                return;
            }
            sink.Finish();
            if (!sink.HasSelection && prefetch == null && !background && request.Capabilities.TimestampPassthrough && request.TimestampOriginSeconds.HasValue)
            { Publish(() => Failed?.Invoke(new(request, CoreText.FrameUnavailable, result)), currentOnly: true); return; }
            if (sink.Segment is { Clock.TrustedAbsolute: true } segment)
            {
                var stored = _cache.StoreSegment(segment.PhysicalRequest, sink.Frames, segment.Clock, segment.SafeStartSeconds,
                    segment.SafeEndSeconds, segment.ReachedInputEnd);
                if (stored != null) { sink.Segment = stored; if (!background && prefetch == null) _cache.PinCurrent(stored.SegmentId, request.TargetSeconds); }
            }
            else _cache.Store(physicalKey, sink.Frames, physicalRequest);
            if (prefetch != null) prefetch.Segment = sink.Segment;
            _cache.EndPendingSource(pendingSource); pendingSource = 0;
            if (PerfTrace.Enabled) PerfTrace.Write("core.cache_store", new { request.CorrelationId, version,
                frames = sink.Frames.Count, bytes = sink.ReceivedBytes, cacheBytes = _cache.SizeBytes,
                cacheEntries = _cache.Count });
            Publish(() => Completed?.Invoke(new(request, result, false, sink.TargetNotReached) { Frames = sink.Frames.AsReadOnly(), Segment = sink.Segment, IsReplay = isReplay }));

            async Task<PreviewRunResult> RunWithFallbackAsync()
            {
                try { return await RunTrackedBackendAsync(backend, physicalRequest, sink, active.Token).ConfigureAwait(false); }
                catch (PreviewServerNotSupportedException) when (_enableFallback && ReferenceEquals(backend, background ? _backgroundPreferred : _preferred))
                {
                    lock (_sync) _unsupported.Add(request.EncoderPath);
                    backend = background ? _backgroundFallback : _fallback;
                    physicalRequest = PhysicalAfterRequest(request with { DependsOnHistory = null }, background) with { Continuous = false };
                    physicalKey = physicalRequest with
                    { Capabilities = request.Capabilities, InputArgs = request.InputArgs, VppArgs = request.VppArgs };
                    sink = new SelectionSink(request, emit, background, physicalRequest)
                    {
                        Progress = progress.Touch,
                        ReplayRequired = reason => { MarkLegacySource(request, reason); active.Cancel(); },
                        ReadState = reading => { if (reading) progress.Resume(); else progress.Suspend(); },
                    };
                    progress.Touch();
                    return await RunTrackedBackendAsync(backend, physicalRequest, sink, active.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (scheduled.IsCancellationRequested || active?.IsCancellationRequested == true)
        {
            if (PerfTrace.Enabled) PerfTrace.Write("core.cancelled", new { request.CorrelationId, version, entered });
        }
        catch (Exception ex)
        {
            if (PerfTrace.Enabled) PerfTrace.Write("core.exception", new { request.CorrelationId, version,
                type = ex.GetType().Name, ex.Message });
            Publish(() => Failed?.Invoke(new(request, ex.Message, Exception: ex) { Diagnostic = LegacyFailureDiagnostic(request, ex) }), currentOnly: true);
        }
        finally
        {
            _cache.EndPendingSource(pendingSource);
            lock (_sync)
            {
                if (ReferenceEquals(_active, active)) _active = null;
                if (ReferenceEquals(_backgroundActive, active)) _backgroundActive = null;
                if (background)
                {
                    if (ReferenceEquals(_backgroundScheduled, scheduled)) _backgroundScheduled = null;
                }
                else
                {
                    if (ReferenceEquals(_scheduled, scheduled)) _scheduled = null;
                    if (--_foregroundPending == 0) _foregroundIdle.TrySetResult();
                }
            }
            active?.Dispose();
            scheduled.Dispose();
            if (entered)
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.execution_leave", new { request.CorrelationId, version,
                    elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(executionStart).TotalMilliseconds });
                ExecutionFor(background).Release();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask != null) return new(_disposeTask);
            _disposed = true;
            ++_version;
            TraceDispose("enter", new { tasks = _tasks.Count });
            long started = _clock.GetTimestamp();
            // 同期cancel callbackがUIやdriverの待ちに入っても、終了期限の監視を止めない。
            Task[] cancellations = new[] { _auxiliaryLifetime, _scheduled, _backgroundScheduled, _active, _backgroundActive, _stream?.Cancellation, _reversePrefetch?.Cancellation }
                .Where(source => source != null).Distinct().Select(source => source!.CancelAsync()).ToArray();
            _streamStateChanged.TrySetResult();
            TraceDispose("cancelled");
            _disposeTask = DisposeCoreAsync(_tasks.ToArray(), cancellations, started);
            return new(_disposeTask);
        }
    }

    private async Task<PreviewRunResult> RunTrackedBackendAsync(IPreviewBackend backend, PreviewRequest request,
        IPreviewSink sink, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var running = new BackendRun();
        lock (_sync)
        {
            if (!_backendRuns.TryGetValue(backend, out var runs)) _backendRuns.Add(backend, runs = new());
            runs.Add(running);
        }
        try
        {
            Task<PreviewRunResult> run = backend.RunAsync(request, sink, token);
            lock (_sync) running.Task = run;
            return await run.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (_backendRuns.TryGetValue(backend, out var runs))
                { runs.Remove(running); if (runs.Count == 0) _backendRuns.Remove(backend); }
            }
        }
    }

    private sealed class BackendRun
    {
        public Task<PreviewRunResult>? Task { get; set; }
    }

    private static void TraceDispose(string stage, object? data = null)
    {
        if (!PerfTrace.Enabled) return;
        PerfTrace.Write("core.dispose.stage", new { stage, data, synchronizationContext = SynchronizationContext.Current?.GetType().FullName });
        PerfTrace.Flush();
    }

    private async Task DisposeCoreAsync(Task[] tasks, Task[] cancellations, long started)
    {
        // 呼出元UIのpumpが止まっても、終了期限の監視とbackend回収を開始する。
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Task finished = Task.WhenAll(tasks);
        Task cooperative = Task.WhenAll(cancellations.Append(finished));
        TimeSpan grace = Min(_disposeGrace, Remaining(started));
        TraceDispose("grace_begin", new { tasks = tasks.Length, milliseconds = grace.TotalMilliseconds });
        if (!cooperative.IsCompleted && grace > TimeSpan.Zero)
        {
            using var timer = new CancellationTokenSource();
            await Task.WhenAny(cooperative, Task.Delay(grace, _clock, timer.Token)).ConfigureAwait(false);
            await timer.CancelAsync().ConfigureAwait(false);
        }
        TraceDispose("grace_end", new { completed = cooperative.IsCompleted });
        var backends = new[] { _preferred, _fallback, _backgroundPreferred, _backgroundFallback }
            .Distinct(ReferenceEqualityComparer.Instance).Cast<IPreviewBackend>().ToArray();
        HashSet<IPreviewBackend> pending;
        lock (_sync)
            pending = new(_backendRuns.Where(pair => pair.Value.Any(run => run.Task?.IsCompleted != true)).Select(pair => pair.Key), ReferenceEqualityComparer.Instance);
        // 同期先頭やpipe回収が止まるbackendから、他backendの終了開始を切り離す。
        Task[] cleanup = backends.Select((backend, index) => Task.Run(async () =>
        {
            if (pending.Contains(backend))
            {
                TraceDispose("backend_abort_begin", new { index, backend = backend.GetType().Name });
                await WaitForShutdownAsync(Task.Run(async () => await backend.AbortAsync().ConfigureAwait(false)),
                    started, $"backend_abort_{index}").ConfigureAwait(false);
                TraceDispose("backend_abort_end", new { index });
            }
            TraceDispose("backend_dispose_begin", new { index, backend = backend.GetType().Name });
            await WaitForShutdownAsync(Task.Run(async () => await backend.DisposeAsync().ConfigureAwait(false)),
                started, $"backend_dispose_{index}").ConfigureAwait(false);
            TraceDispose("backend_dispose_end", new { index });
        })).ToArray();
        await Task.WhenAll(cleanup).ConfigureAwait(false);
        await WaitForShutdownAsync(cooperative, started, "tasks").ConfigureAwait(false);
        // 期限後に実行が戻ってもfinallyのReleaseを壊さない。実行完了まではsemaphoreを残す。
        if (finished.IsCompleted) ReleaseExecutionResources();
        else _ = ReleaseExecutionResourcesLaterAsync(finished);
        TraceDispose("complete", new { incomplete = ShutdownIncomplete });
    }

    private TimeSpan Remaining(long started) => Max(TimeSpan.Zero, _disposeTimeout - _clock.GetElapsedTime(started));
    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private async Task WaitForShutdownAsync(Task task, long started, string stage)
    {
        try
        {
            if (!task.IsCompleted)
            {
                TimeSpan remaining = Remaining(started);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                await task.WaitAsync(remaining, _clock).ConfigureAwait(false);
            }
            else await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _shutdownIncomplete, 1);
            System.Diagnostics.Trace.TraceWarning("Preview shutdown incomplete ({0}): {1}", stage, exception.Message);
            TraceDispose("incomplete", new { stage, error = exception.Message });
            // 後から失敗する放棄taskも観測して、未観測例外を残さない。
            _ = ObserveShutdownTaskAsync(task);
        }
    }

    private static async Task ObserveShutdownTaskAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) { System.Diagnostics.Trace.TraceWarning("Preview shutdown late failure: {0}", exception.Message); }
    }

    private async Task ReleaseExecutionResourcesLaterAsync(Task finished)
    {
        await ObserveShutdownTaskAsync(finished).ConfigureAwait(false);
        ReleaseExecutionResources();
    }

    private void ReleaseExecutionResources()
    {
        _reversePrefetch?.Cancellation.Dispose();
        _auxiliaryLifetime.Dispose();
        _execution.Dispose();
        _backgroundExecution.Dispose();
    }

    private sealed class SelectionSink(PreviewRequest request, Action<PreviewFrameResult> emit, bool background = false, PreviewRequest? physicalRequest = null) : IPreviewSink
    {
        private readonly PreviewRequest _physicalRequest = physicalRequest ?? request;
        private readonly PreviewTimestampGuard _timestampGuard = new(request.Fps);
        public Action<string>? ReplayRequired { get; set; }
        public bool NeedsLegacyReplay { get; private set; }
        private double _target = CommandBuilder.Plan(physicalRequest ?? request).ActualPreMargin;
        private PreviewClockMap _clock = new(request.TimestampOriginSeconds, CommandBuilder.Plan(physicalRequest ?? request).SeekSeconds ?? 0, false);
        public PreviewSegmentSnapshot? Segment { get; set; }
        public Action<bool>? ReadState { get; set; }
        public void OnReadStateChanged(bool reading) => ReadState?.Invoke(reading);
        private int _candidate = -1;
        private double _candidateTime = double.NegativeInfinity;
        private double _lastTime = double.NegativeInfinity;
        private bool _confirmed;
        private bool _hasTimestamp;
        public List<Y4mFrame> Frames { get; } = new();
        public long ReceivedBytes { get; private set; }
        public Action? Progress { get; set; }
        public bool TargetNotReached { get; private set; }
        public bool HasSelection => _confirmed;
        public void OnOutput() => Progress?.Invoke();
        public void OnHeader(Y4mHeader header) { }
        public void OnFrame(Y4mFrame frame)
        {
            int index = Frames.Count;
            if (_physicalRequest.Capabilities.TimestampPassthrough)
            {
                string? reason = index == 0 && !ClockFor(_physicalRequest, frame).TrustedAbsolute
                    ? "要求先頭のabsolute時刻が入力原点と一致しません" : null;
                if (reason == null && _timestampGuard.IsInvalid(frame)) reason = _timestampGuard.Reason;
                if (reason != null) { NeedsLegacyReplay = true; ReplayRequired?.Invoke(reason); return; }
            }
            if (PerfTrace.Enabled)
            {
                ReceivedBytes += frame.Data.LongLength;
                if (index == 0) PerfTrace.Write("core.first_frame", new { request.CorrelationId, frame.Xts,
                    bytes = frame.Data.LongLength, frame.Header.Width, frame.Header.Height });
            }
            Frames.Add(frame);
            if (index == 0)
            {
                _clock = ClockFor(_physicalRequest, frame);
                _target = _clock.ToFrameSeconds(request.TargetSeconds);
            }
            UpdateSegment(false);
            // 一部だけ時刻が欠ける場合、V1と同じく欠落フレームを選択対象にしない。
            if (frame.Xts is not double ts) return;
            _hasTimestamp = true;
            double tolerance = FrameSelector.Duration(frame, request.Fps) / 2.0;
            _lastTime = Math.Max(_lastTime, ts);
            if (ts <= _target + tolerance && ts > _candidateTime)
            {
                _candidate = index;
                _candidateTime = ts;
            }
            else if (ts > _target + tolerance && _candidate >= 0
                && (background || !request.Capabilities.TimestampPassthrough || Segment!.SafeEndSeconds >= request.TargetSeconds - tolerance)) Confirm(false);
        }
        public void Finish()
        {
            if (Frames.Count == 0) return;
            bool naturalEnd = false;
            if (request.InputDurationSeconds is double duration && double.IsFinite(duration) && duration >= 0)
            {
                var plan = CommandBuilder.Plan(_physicalRequest);
                double plannedEnd = (plan.SeekSeconds ?? 0) + plan.FrameCount / request.Fps;
                double receivedEnd = _clock.ToInputSeconds(Frames[^1], Frames.Count - 1, request.Fps)
                    + FrameSelector.Duration(Frames[^1], request.Fps);
                // 出力枚数は間引きで変わるためEOF根拠にはしない。既知の入力長と受信した末尾を照合する。
                double endTolerance = Math.Max(0.02, FrameSelector.Duration(Frames[^1], request.Fps) / 2);
                naturalEnd = plannedEnd >= duration - endTolerance && receivedEnd >= duration - endTolerance;
            }
            UpdateSegment(naturalEnd);
            if (_confirmed) return;
            if (!_hasTimestamp)
            {
                // 全フレームが時刻なしと判明するのは終端なので、fpsによる代替選択はここで行う。
                _candidate = FrameSelector.Select(Frames, _target, request.Fps);
                _lastTime = (Frames.Count - 1) / FrameSelector.FrameRate(Frames[^1], request.Fps);
            }
            var lastTimed = Frames.LastOrDefault(f => f.Xts.HasValue) ?? Frames[^1];
            double tolerance = FrameSelector.Duration(lastTimed, request.Fps) / 2.0;
            TargetNotReached = _lastTime + tolerance < _target;
            if (TargetNotReached) _candidate = Frames.Count - 1;
            UpdateSegment(naturalEnd || TargetNotReached);
            Confirm(TargetNotReached);
        }
        private void UpdateSegment(bool reachedEnd)
        {
            if (Frames.Count == 0) return;
            double start = FrameCache.SafeStartIncludingFirst(PreviewWarmupPolicy.SafeStartSeconds(_physicalRequest, background),
                _clock, Frames[0], request.Fps);
            bool guarded = !background && _physicalRequest.Capabilities.TimestampPassthrough && request.TimestampOriginSeconds.HasValue;
            double end = _clock.ToInputSeconds(Frames[^1], Frames.Count - 1, request.Fps) + FrameSelector.Duration(Frames[^1], request.Fps);
            if (guarded && !reachedEnd) end -= 0.5;
            // 有限要求の早期画像は画素照合前なので、完了後にcacheが検証するまでは他区間と混ぜない。
            Segment = new(0, _physicalRequest, _clock, Array.AsReadOnly(Frames.ToArray()), start, end, false, reachedEnd)
                { CanStitch = false };
        }
        private void Confirm(bool notReached)
        {
            if (_confirmed || _candidate < 0) return;
            if (!background && _physicalRequest.Capabilities.TimestampPassthrough && Segment is { } segment)
            {
                double time = segment.Clock.ToInputSeconds(Frames[_candidate], _candidate, request.Fps);
                if (!PreviewSafeRange.Contains(time, segment.SafeStartSeconds, segment.SafeEndSeconds))
                { TargetNotReached = true; return; }
            }
            _confirmed = true;
            TargetNotReached = notReached;
            emit(new(request, Frames[_candidate], notReached) { Segment = Segment });
        }
    }

    private sealed class OutputWatchdog : IDisposable
    {
        private readonly object _sync = new();
        private readonly ITimer _timer;
        private readonly TimeSpan _timeout;
        private readonly TimeProvider _clock;
        private long _lastProgress;
        private bool _disposed;
        private bool _suspended;
        private readonly TaskCompletionSource _expired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OutputWatchdog(TimeProvider clock, TimeSpan timeout)
        {
            _clock = clock;
            _timeout = timeout;
            _lastProgress = clock.GetTimestamp();
            _timer = clock.CreateTimer(_ => Check(), null, timeout, Timeout.InfiniteTimeSpan);
        }
        public Task Expired => _expired.Task;
        public void Touch()
        {
            lock (_sync)
            {
                if (_disposed || _suspended) return;
                _lastProgress = _clock.GetTimestamp();
                _timer.Change(_timeout, Timeout.InfiniteTimeSpan);
            }
        }
        public void Suspend()
        {
            lock (_sync) { if (_disposed) return; _suspended = true; _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        }
        public void Resume()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _suspended = false;
                _lastProgress = _clock.GetTimestamp();
                _timer.Change(_timeout, Timeout.InfiniteTimeSpan);
            }
        }
        private void Check()
        {
            lock (_sync)
            {
                if (_disposed || _suspended) return;
                // Changeより前にキューへ入った古いコールバックでも、最新の受信時刻で再判定する。
                TimeSpan remaining = _timeout - _clock.GetElapsedTime(_lastProgress);
                if (remaining > TimeSpan.Zero) _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                else _expired.TrySetResult();
            }
        }
        public void Dispose() { lock (_sync) { _disposed = true; _timer.Dispose(); } }
    }
}

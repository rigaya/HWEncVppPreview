namespace HWEncParamViewer.Core;

public sealed record PreviewFrameResult(PreviewRequest Request, Y4mFrame Frame, bool TargetNotReached)
{
    public PreviewSegmentSnapshot? Segment { get; init; }
    public bool IsReplay { get; init; }
    /// <summary>受信済みの単調な等間隔列で目標を読み越し、END前のBefore表示を許可する。</summary>
    public bool CanDisplayBeforeEnd { get; init; }
}
public sealed record PreviewCompletion(PreviewRequest Request, PreviewRunResult Result, bool FromCache, bool TargetNotReached)
{
    /// <summary>成功した区間の全フレーム。LRUに保存されない区間でも、現在位置の前後を選べるようにする。</summary>
    public IReadOnlyList<Y4mFrame> Frames { get; init; } = Array.Empty<Y4mFrame>();
    public PreviewSegmentSnapshot? Segment { get; init; }
    public bool IsReplay { get; init; }
    /// <summary>早期表示後に受信時刻の条件が崩れた理由。最終表示の再選択と照合する。</summary>
    public string? EarlyDisplayFallbackReason { get; init; }
}
public sealed record PreviewFailure(PreviewRequest Request, string Reason, PreviewRunResult? Result = null, Exception? Exception = null)
{
    /// <summary>失敗区分・元の例外を変えず、通常ログへ追加する診断。なければ従来のstderrを使う。</summary>
    public string? Diagnostic { get; init; }
    /// <summary>表示文言に依存せず、watchdogの期限切れを識別する。</summary>
    public bool TimedOut { get; init; }
    /// <summary>実受信窓が不足した場合の構造化した診断。文言から回復可否を判定しない。</summary>
    public PreviewWindowShortfall? WindowShortfall { get; init; }
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
    private PreviewRequest? _backgroundActiveRequest;
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
    private readonly PreviewFiniteBudget _finiteBudget = new();
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
                SeekSecondsOverride = null, PreMarginSeconds = 3, PostMarginSeconds = 0.5, RecoveryPhysicalWindow = null,
                WarmupSeconds = PreviewWarmupPolicy.DefaultWarmupSeconds,
                InputArgs = request.InputArgs.Where(argument => !argument.Equals("--timestamp-passthrough", StringComparison.OrdinalIgnoreCase)).ToArray(),
                VppArgs = request.VppArgs.Where(argument => !argument.Equals("--timestamp-passthrough", StringComparison.OrdinalIgnoreCase)).ToArray(),
            };
        var physical = request.RecoveryPhysicalWindow ?? PreviewWarmupPolicy.PreparePhysical(request, background);
        return WithOutputStartHint(request, LimitReverseWindow(request, physical, background));
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
        CancellationToken cancellationToken, bool isReplay = false, ReversePrefetchState? prefetch = null, int recoveryAttempt = 0)
    {
        // 回復窓は通知する論理要求から分離する。Afterの物理VPP列をBeforeのwithコピーへ持ち込まない。
        var recoveryPhysicalWindow = request.RecoveryPhysicalWindow;
        if (recoveryPhysicalWindow != null) request = request with { RecoveryPhysicalWindow = null };
        bool entered = false;
        long waitStart = 0;
        long executionStart = 0;
        long backendStart = 0;
        long pendingSource = 0;
        CancellationTokenSource? active = null;
        using var rawSinks = _rawScope == null ? null : new RawDisposableBag();
        using var finiteWindow = _finiteBudget.Open(_cache.CapacityBytes, request.RetainedByteLimit);
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
                    // 同じ比較組の背景だけを保持し、過去の比較要求は従来どおり取り消す。
                    if (!request.AllowConcurrentBackground || request.CorrelationId == 0
                        || _backgroundActiveRequest?.CorrelationId != -request.CorrelationId) _backgroundActive?.Cancel();
                }
            }
            if (PerfTrace.Enabled)
            {
                waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                PerfTrace.Write("core.execution_wait", new { request.CorrelationId, version });
            }
            while (true)
            {
                if (background && !(request.AllowConcurrentBackground && _separateBackground)) await WaitForForegroundAsync(scheduled.Token).ConfigureAwait(false);
                await ExecutionFor(background).WaitAsync(scheduled.Token).ConfigureAwait(false);
                entered = true;
                if (PerfTrace.Enabled) executionStart = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_sync)
                {
                    if (!IsCurrent()) return;
                    // SemaphoreSlimの待ち順には依存せず、取得と実行登録の間にも前景を優先する。
                    if (!background || request.AllowConcurrentBackground && _separateBackground || CanStartBackgroundCore())
                    {
                        // scheduledを連結すると、次の移動による待機置換まで実行中断へ変わってしまう。
                        active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        if (background) { _backgroundActive = active; _backgroundActiveRequest = request; } else _active = active;
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
            var physicalRequest = PhysicalAfterRequest(request with { RecoveryPhysicalWindow = recoveryPhysicalWindow }, background) with { Continuous = false };
            Action<PreviewFrameResult> emit = frame =>
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.target_frame", new { request.CorrelationId, version,
                    frame.Frame.Xts, frame.TargetNotReached });
                Publish(() => EmitFrame(frame with { IsReplay = isReplay }));
            };
            var sink = new SelectionSink(rawSinks, request, emit, background, physicalRequest);
            var physicalKey = physicalRequest with
            { Capabilities = request.Capabilities, InputArgs = request.InputArgs, VppArgs = request.VppArgs };
            if (!bypassCache && !IsLegacySource(request) && request.Capabilities.TimestampPassthrough && request.TimestampOriginSeconds.HasValue
                && _cache.TryGetAt(request, request.TargetSeconds, out var cachedSegment))
            {
                if (!background)
                    _cache.PinCurrent(cachedSegment!.SegmentId, request.TargetSeconds);
                if (PublishSegmentSelection(request, cachedSegment!, version, navigation, background, editVersion, cancellationToken, true))
                    return;
                if (!IsCurrent() || cancellationToken.IsCancellationRequested) return;
                // 消えた正ID候補をexact窓へ再投入せず、同じ要求の通常取得へ一度だけ戻す。
                bypassCache = true;
            }
            PreviewRequest cachedPhysical = request;
            using var framesOwned = _rawScope != null && !bypassCache ? _cache.TryGetOwned(request, "ControllerCacheRead", out cachedPhysical) : null;
            using var physicalOwned = _rawScope != null && !bypassCache && framesOwned == null ? _cache.TryGetOwned(physicalKey, "ControllerCacheRead", out cachedPhysical) : null;
            IReadOnlyList<Y4mFrame> frames = (framesOwned ?? physicalOwned)?.Frames ?? Array.Empty<Y4mFrame>();
            if (!bypassCache && (_rawScope != null ? framesOwned != null || physicalOwned != null
                : _cache.TryGet(request, out frames, out cachedPhysical) || _cache.TryGet(physicalKey, out frames, out cachedPhysical)))
            {
                // keyが一致する保存windowを使う。新targetのcanonical窓へ置換すると生Xtsの基準がずれる。
                sink = new SelectionSink(rawSinks, request, emit, background, cachedPhysical);
                if (PerfTrace.Enabled) PerfTrace.Write("core.cache_hit", new { request.CorrelationId, version, frames = frames.Count });
                foreach (var frame in frames) sink.OnFrame(frame);
                sink.Finish();
                if (!sink.HasSelection && prefetch == null && !background && request.Capabilities.TimestampPassthrough && request.TimestampOriginSeconds.HasValue)
                { Publish(() => Failed?.Invoke(new(request, CoreText.FrameUnavailable)), currentOnly: true); return; }
                Publish(() => EmitCompletion(new(request, WithLegacyDiagnostic(request, new(0, "")), true, sink.TargetNotReached) { Frames = sink.Frames.AsReadOnly(), Segment = sink.Segment, IsReplay = isReplay }));
                return;
            }
            if (!background && prefetch == null) pendingSource = _cache.BeginPendingSource(request);
            IPreviewBackend backend;
            lock (_sync) backend = _unsupported.Contains(request.EncoderPath)
                ? (background ? _backgroundFallback : _fallback) : (background ? _backgroundPreferred : _preferred);
            bool? history;
            (backend, history) = await PrepareBackendAsync(backend, request, background, active.Token).ConfigureAwait(false);
            physicalRequest = PhysicalAfterRequest(request with { DependsOnHistory = history, RecoveryPhysicalWindow = recoveryPhysicalWindow }, background) with { Continuous = false };
            physicalKey = physicalRequest with
            { Capabilities = request.Capabilities, InputArgs = request.InputArgs, VppArgs = request.VppArgs };
            finiteWindow.Reset();
            sink = new SelectionSink(rawSinks, request, emit, background, physicalRequest, finiteWindow);
            sink.HeaderObserved = header => RememberOutputHeader(request, header);
            sink.FrameObserved = frame => RememberOutputFrame(request, frame);
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
            PreviewWindowShortfall? previousShortfall = null;
            while (true)
            {
                active.Token.ThrowIfCancellationRequested();
                if (!IsCurrent()) return;
                progress.Touch();
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
                var shortfall = PreviewWindowShortfall.FromSegment(request, sink.Segment);
                if (shortfall is { MissingHead: true } && result.OutputStartApplied && !physicalRequest.DisableOutputStart)
                {
                    // 未知のVFRの隙間では、Sを動かす前に転送の省略だけを外して旧窓を正確に再現する。
                    if (PerfTrace.Enabled) PerfTrace.Write("core.output_start_replay", new {request.CorrelationId, seek=CommandBuilder.Plan(physicalRequest).SeekSeconds});
                    isReplay = true;
                    Publish(() => ReplayStarted?.Invoke(request), currentOnly: true);
                    physicalRequest = physicalRequest with { DisableOutputStart = true };
                    physicalKey = physicalRequest with { Capabilities=request.Capabilities, InputArgs=request.InputArgs, VppArgs=request.VppArgs };
                    finiteWindow.Reset();
                    sink = new SelectionSink(rawSinks, request, emit, background, physicalRequest, finiteWindow)
                    {
                        HeaderObserved = header => RememberOutputHeader(request, header),
                        FrameObserved = frame => RememberOutputFrame(request, frame),
                        Progress = progress.Touch,
                        ReplayRequired = reason => { MarkLegacySource(request, reason); active.Cancel(); },
                        ReadState = reading => { if (reading) progress.Resume(); else progress.Suspend(); },
                    };
                    continue;
                }
                if (shortfall != null && prefetch == null)
                {
                    bool madeProgress = previousShortfall == null
                        || shortfall.MissingHead && (shortfall.FirstSeconds < previousShortfall.FirstSeconds - PreviewSafeRange.ToleranceSeconds
                            // 同じkeyに着地しても、探索窓が前へ進んでいれば上限内でさらに戻す。
                            || (CommandBuilder.Plan(shortfall.RequestedWindow).SeekSeconds ?? 0)
                                < (CommandBuilder.Plan(previousShortfall.RequestedWindow).SeekSeconds ?? 0))
                        || shortfall.MissingTail && shortfall.LastSeconds > previousShortfall.LastSeconds + PreviewSafeRange.ToleranceSeconds;
                    if (madeProgress && PreviewWindowRecovery.TryExpand(request, shortfall, recoveryAttempt, out var expanded))
                    {
                        if (PerfTrace.Enabled) PerfTrace.Write("core.window_recovery", new { request.CorrelationId,
                            attempt = recoveryAttempt + 1, shortfall, next = CommandBuilder.Plan(expanded.RecoveryPhysicalWindow!) });
                        previousShortfall = shortfall;
                        ++recoveryAttempt;
                        physicalRequest = PhysicalAfterRequest(expanded, background) with { Continuous = false };
                        physicalKey = physicalRequest with
                        { Capabilities = request.Capabilities, InputArgs = request.InputArgs, VppArgs = request.VppArgs };
                        finiteWindow.Reset();
                        sink = new SelectionSink(rawSinks, request, emit, background, physicalRequest, finiteWindow)
                        {
                            HeaderObserved = header => RememberOutputHeader(request, header),
                            FrameObserved = frame => RememberOutputFrame(request, frame),
                            Progress = progress.Touch,
                            ReplayRequired = reason => { MarkLegacySource(request, reason); active.Cancel(); },
                            ReadState = reading => { if (reading) progress.Resume(); else progress.Suspend(); },
                        };
                        continue;
                    }
                }
                if (prefetch == null && (shortfall != null || !sink.HasSelection && !background
                    && request.Capabilities.TimestampPassthrough && request.TimestampOriginSeconds.HasValue))
                {
                    Publish(() => Failed?.Invoke(new(request, CoreText.FrameUnavailable, result)
                        { WindowShortfall = shortfall }), currentOnly: true);
                    return;
                }
                break;
            }
            if (sink.Segment is { Clock.TrustedAbsolute: true } segment)
            {
                var stored = _cache.StoreSegment(segment.PhysicalRequest, sink.Frames, segment.Clock, segment.SafeStartSeconds,
                    segment.SafeEndSeconds, segment.ReachedInputEnd, prefetch != null, sink.FirstOutputInputSeconds,
                    pinCurrent: !background && prefetch == null);
                if (stored != null) sink.Segment = stored;
                else if (_cache.CapacityBytes > 0) sink.KeepSelectionOnly();
            }
            else
            {
                // 時刻なし列でprefixを捨てた結果は、legacyの相対番号だけのcacheへ戻さない。
                if (sink.Segment?.Clock.FrameIndexOffset is not > 0 || sink.Frames.Any(frame => frame.Xts.HasValue))
                    _cache.Store(physicalKey, sink.Frames, physicalRequest);
                if (_cache.CapacityBytes > 0 && !_cache.TryGet(physicalKey, out _)) sink.KeepSelectionOnly();
            }
            if (sink.RetentionLimited)
                result = result with { StandardError = result.StandardError + "\n[preview] Raw retention window limited by byte budget.\n" };
            _cache.EndPendingSource(pendingSource); pendingSource = 0;
            if (PerfTrace.Enabled) PerfTrace.Write("core.cache_store", new { request.CorrelationId, version,
                frames = sink.Frames.Count, bytes = sink.ReceivedBytes, cacheBytes = _cache.SizeBytes,
                cacheEntries = _cache.Count });
            Publish(() => EmitCompletion(new(request, result, false, sink.TargetNotReached) { Frames = sink.Frames.AsReadOnly(), Segment = sink.Segment, IsReplay = isReplay,
                EarlyDisplayFallbackReason = sink.EarlyDisplayFallbackReason }));

            async Task<PreviewRunResult> RunWithFallbackAsync()
            {
                try { return await RunTrackedBackendAsync(backend, physicalRequest, sink, active.Token).ConfigureAwait(false); }
                catch (PreviewServerNotSupportedException) when (_enableFallback && ReferenceEquals(backend, background ? _backgroundPreferred : _preferred))
                {
                    lock (_sync) _unsupported.Add(request.EncoderPath);
                    backend = background ? _backgroundFallback : _fallback;
                    physicalRequest = PhysicalAfterRequest(request with { DependsOnHistory = null, RecoveryPhysicalWindow = recoveryAttempt > 0 || recoveryPhysicalWindow != null ? physicalRequest : null }, background) with { Continuous = false };
                    physicalKey = physicalRequest with
                    { Capabilities = request.Capabilities, InputArgs = request.InputArgs, VppArgs = request.VppArgs };
                    finiteWindow.Reset();
                    sink = new SelectionSink(rawSinks, request, emit, background, physicalRequest, finiteWindow)
                    {
                        HeaderObserved = header => RememberOutputHeader(request, header),
                        FrameObserved = frame => RememberOutputFrame(request, frame),
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
                if (ReferenceEquals(_backgroundActive, active)) { _backgroundActive = null; _backgroundActiveRequest = null; }
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
        IPreviewSink sink, CancellationToken token, bool forceLegacy = false)
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
            Task<PreviewRunResult> run = _rawScope != null && !forceLegacy
                ? backend is IRawOwnedBackend owned && sink is IRawOwnedSink target
                    ? owned.RunOwnedAsync(request, target, _rawScope, token)
                    : throw new InvalidOperationException("owned経路を公開sinkへ接続できません。")
                : backend.RunAsync(request, sink, token);
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
        public Task? Task { get; set; }
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

    private sealed class SelectionSink : IRawOwnedSink, IDisposable
    {
        private readonly PreviewRequest request;
        private readonly Action<PreviewFrameResult> emit;
        private readonly bool background;
        private readonly PreviewFiniteBudget.Window? budget;
        private readonly object _rawGate = new();
        private bool _rawClosed;
        internal SelectionSink(RawDisposableBag? bag, PreviewRequest request, Action<PreviewFrameResult> emit, bool background = false,
            PreviewRequest? physicalRequest = null, PreviewFiniteBudget.Window? budget = null)
        {
            this.request = request;
            this.emit = emit;
            this.background = background;
            this.budget = budget;
            _physicalRequest = physicalRequest ?? request;
            _timestampGuard = new(request.Fps);
            _target = CommandBuilder.Plan(_physicalRequest).ActualPreMargin;
            _clock = new(request.TimestampOriginSeconds, CommandBuilder.Plan(_physicalRequest).SeekSeconds ?? 0, false);
            bag?.Add(this);
        }
        private readonly PreviewRequest _physicalRequest;
        private readonly PreviewTimestampGuard _timestampGuard;
        public Action<string>? ReplayRequired { get; set; }
        public bool NeedsLegacyReplay { get; private set; }
        private double _target;
        private PreviewClockMap _clock;
        public PreviewSegmentSnapshot? Segment { get; set; }
        public Action<bool>? ReadState { get; set; }
        public void OnReadStateChanged(bool reading) => ReadState?.Invoke(reading);
        private int _candidate = -1;
        private double _candidateTime = double.NegativeInfinity;
        private double _lastTime = double.NegativeInfinity;
        // 59.94fpsの1%は約167µsなので数十µsの量子化を吸収し、RFF等の1.5倍化は除外できる。
        private const double EarlyTimingRelativeTolerance = 0.01;
        private const int MinimumEarlyTimedFrames = 2;
        private double? _earlyPreviousTimestamp;
        private double? _earlyDuration;
        private int _earlyTimedFrames;
        private bool _earlyTimingEligible = true;
        private bool _finishing;
        private bool _earlyOffered;
        public string? EarlyDisplayFallbackReason { get; private set; }
        private bool _confirmed;
        private bool _hasTimestamp;
        public RawFrameList Frames { get; } = new("Finite");
        private int _receivedFrames;
        private double _receivedEnd = double.NegativeInfinity;
        private bool _guardMetadataOnly;
        private double? _firstOutputInputSeconds;
        private bool _discardedRetainedPrefix;
        public bool RetentionLimited { get; private set; }
        public double? FirstOutputInputSeconds => _firstOutputInputSeconds;
        public long ReceivedBytes { get; private set; }
        public Action? Progress { get; set; }
        public bool TargetNotReached { get; private set; }
        public bool HasSelection => _confirmed;
        public void OnOutput() => Progress?.Invoke();
        public Action<Y4mHeader>? HeaderObserved { get; set; }
        public Action<Y4mFrame>? FrameObserved { get; set; }
        public void OnHeader(Y4mHeader header) => HeaderObserved?.Invoke(header);
        public void OnFrame(Y4mFrame frame)
        {
            if (frame.RawOwner == null)
            {
                OnFrameCore(frame);
                return;
            }
            lock (_rawGate)
            {
                if (!_rawClosed)
                    OnFrameCore(frame);
            }
        }
        private void OnFrameCore(Y4mFrame frame)
        {
            int index = _receivedFrames;
            ObserveEarlyTiming(frame);
            if (index == 0) HeaderObserved?.Invoke(frame.Header);
            if (_physicalRequest.Capabilities.TimestampPassthrough)
            {
                string? reason = index == 0 && !ClockFor(_physicalRequest, frame).TrustedAbsolute
                    ? "要求先頭のabsolute時刻が入力原点と一致しません" : null;
                if (reason == null && _timestampGuard.IsInvalid(frame)) reason = _timestampGuard.Reason;
                if (reason != null) { NeedsLegacyReplay = true; ReplayRequired?.Invoke(reason); return; }
            }
            FrameObserved?.Invoke(frame);
            if (PerfTrace.Enabled)
            {
                ReceivedBytes += frame.RawByteLength;
                if (index == 0) PerfTrace.Write("core.first_frame", new { request.CorrelationId, frame.Xts,
                    bytes = frame.RawByteLength,
                    frame.Header.Width,
                    frame.Header.Height
                });
            }
            ++_receivedFrames;
            if (index == 0)
            {
                _clock = ClockFor(_physicalRequest, frame);
                _target = _clock.ToFrameSeconds(request.TargetSeconds);
                // 暖機のpixelは解放しても、実際に届いた元の先頭時刻という安全判定の根拠は残す。
                double firstInput = _clock.ToInputSeconds(frame, 0, request.Fps);
                _firstOutputInputSeconds = double.IsFinite(firstInput) ? firstInput : null;
                var plan = CommandBuilder.Plan(_physicalRequest);
                double warmup = plan.SeekSeconds > 0 ? _physicalRequest.WarmupSeconds : 0;
                double retainedSeconds = Math.Max(0, plan.ActualPreMargin + _physicalRequest.PostMarginSeconds - warmup);
                _guardMetadataOnly = budget != null
                    && (Math.Ceiling(retainedSeconds * FrameSelector.FrameRate(frame, request.Fps)) + 2) * frame.RawByteLength > budget.CapacityBytes;
            }
            bool timed = frame.Xts is double;
            // 全列欠落と判明する前のfps仮候補は、最初のXtsが現れた時点で無効にする。
            if (timed && !_hasTimestamp) { _candidate = -1; _candidateTime = double.NegativeInfinity; }
            double tolerance = FrameSelector.Duration(frame, request.Fps) / 2.0;
            bool replacesCandidate = frame.Xts is double timestamp
                ? timestamp <= _target + tolerance && timestamp > _candidateTime
                : !_hasTimestamp && index <= Math.Floor(_target * FrameSelector.FrameRate(frame, request.Fps) + 0.5 + 1e-9);
            // 正しさのため処理した暖機は表示可能域へ含めず、pixelの参照も持ち続けない。
            double inputSeconds = _clock.ToInputSeconds(frame, index - _clock.FrameIndexOffset, request.Fps);
            _receivedEnd = inputSeconds + FrameSelector.Duration(frame, request.Fps);
            if (timed) _hasTimestamp = true;
            if (frame.Xts is double observedTime) _lastTime = Math.Max(_lastTime, observedTime);
            if (budget != null && _clock.TrustedAbsolute && !background
                && PreviewSafeRange.IsAfter(PreviewWarmupPolicy.SafeStartSeconds(_physicalRequest, background), inputSeconds))
            {
                _clock = _clock with { FrameIndexOffset = _clock.FrameIndexOffset + 1 };
                return;
            }
            // guardは末尾時刻の証拠だけでよい。表示・照合用の窓より後のpixelを保持しない。
            bool guarded = !background && _physicalRequest.Capabilities.TimestampPassthrough && request.TimestampOriginSeconds.HasValue;
            double retainedEnd = request.TargetSeconds + _physicalRequest.PostMarginSeconds - (guarded ? PreviewByteWindow.TailGuardSeconds : 0);
            if (_guardMetadataOnly && budget != null && guarded && Frames.Count > 0 && !replacesCandidate && inputSeconds > retainedEnd + tolerance)
            {
                UpdateSegment(false);
                TryConfirm(frame, tolerance);
                return;
            }
            if (budget != null)
            {
                while (!budget.TryAdd(frame.RawByteLength))
                {
                    // 候補より古いprefixだけを解放する。候補とguardが収まらなければ成功扱いにはしない。
                    if (Frames.Count == 0 || !replacesCandidate && _candidate <= 0)
                        throw new InvalidOperationException("The preview raw byte budget cannot retain the selected frame and its tail guard.");
                    budget.Remove(Frames[0].RawByteLength);
                    Frames.RemoveAt(0);
                    _discardedRetainedPrefix = true;
                    --_candidate;
                    _clock = _clock with { FrameIndexOffset = _clock.FrameIndexOffset + 1 };
                    MarkLimited();
                }
            }
            Frames.Add(frame);
            if (timed) _hasTimestamp = true;
            if (frame.Xts is double ts) _lastTime = Math.Max(_lastTime, ts);
            if (replacesCandidate)
            {
                _candidate = Frames.Count - 1;
                if (frame.Xts is double candidateTime) _candidateTime = candidateTime;
            }
            UpdateSegment(false);
            TryConfirm(frame, tolerance);
        }
        private void ObserveEarlyTiming(Y4mFrame frame)
        {
            if (!_earlyTimingEligible) return;
            string? reason = null;
            double duration = FrameSelector.Duration(frame, request.Fps);
            if (frame.Xts is not double timestamp || !double.IsFinite(timestamp))
                reason = "受信時刻が欠損または非有限";
            else if (!double.IsFinite(duration) || duration <= 0)
                reason = "表示時間を確定できない";
            else
            {
                // 後続の長い表示時間で許容幅が広がらないよう、先頭の有効値を基準に固定する。
                double tolerance = (_earlyDuration ?? duration) * EarlyTimingRelativeTolerance;
                // 同じ時刻や後退を含む列は、後続の確定を待ってから選ぶ。
                if (_earlyPreviousTimestamp is double previous && timestamp <= previous)
                    reason = "受信時刻が単調増加していない";
                // 長い表示時間が後から現れる列では、先の候補も選択範囲に入り得る。
                else if (_earlyDuration is double known && Math.Abs(duration - known) > tolerance)
                    reason = "受信した表示時間が一致しない";
                // 表示時間だけが一定でも、時刻の間隔が異なる列は可変レートとして待つ。
                else if (_earlyPreviousTimestamp is double last && Math.Abs(timestamp - last - duration) > tolerance)
                    reason = "受信時刻の間隔が一定でない";
                _earlyPreviousTimestamp = timestamp;
                _earlyDuration ??= duration;
                ++_earlyTimedFrames;
            }
            if (reason == null) return;
            _earlyTimingEligible = false;
            if (_earlyOffered) EarlyDisplayFallbackReason = reason;
        }

        private void TryConfirm(Y4mFrame frame, double tolerance)
        {
            // 全列が時刻なしだった場合だけFinishでfpsへ戻す。
            if (frame.Xts is double observed && observed > _target + tolerance && _candidate >= 0
                && (background || !request.Capabilities.TimestampPassthrough || Segment!.SafeEndSeconds >= request.TargetSeconds - tolerance)) Confirm(false);
        }
        private void MarkLimited()
        {
            if (RetentionLimited) return;
            RetentionLimited = true;
            if (PerfTrace.Enabled) PerfTrace.Write("core.retention_limited", new { request.CorrelationId,
                capacityBytes = budget?.CapacityBytes, reason = "rolling_prefix" });
        }
        public void Dispose()
        {
            lock (_rawGate)
            {
                _rawClosed = true;
                Frames.Dispose();
            }
        }
        public void KeepSelectionOnly()
        {
            if (_candidate < 0 || Segment == null) return;
            // 保存できなかった窓は、候補と直前直後だけを通知する。guardの根拠は受信済みの安全域を使う。
            int first = Math.Max(0, _candidate - 1);
            int count = Math.Min(Frames.Count - first, 3);
            if (first == 0 && count == Frames.Count) return;
            var retained = Frames.Skip(first).Take(count).ToArray();
            long removed = Frames.Sum(f => f.RawByteLength) - retained.Sum(f => f.RawByteLength);
            budget?.Remove(removed);
            using var keep = RawFrameSetLease.Capture(retained, "FiniteCandidate");
            Frames.Clear();
            Frames.AddRange(keep.Frames);
            _candidate -= first;
            _clock = _clock with { FrameIndexOffset = _clock.FrameIndexOffset + first };
            Segment = Segment with { Frames = Array.AsReadOnly(retained), Clock = _clock,
                SafeStartSeconds = Math.Max(Segment.SafeStartSeconds, _clock.ToInputSeconds(retained[0], 0, request.Fps)),
                SafeEndSeconds = Math.Min(Segment.SafeEndSeconds,
                    _clock.ToInputSeconds(retained[^1], count - 1, request.Fps) + FrameSelector.Duration(retained[^1], request.Fps)) };
            MarkLimited();
        }

        public void Finish()
        {
            _finishing = true;
            if (Frames.Count == 0) return;
            bool naturalEnd = false;
            if (request.InputDurationSeconds is double duration && double.IsFinite(duration) && duration >= 0)
            {
                var plan = CommandBuilder.Plan(_physicalRequest);
                double plannedEnd = (plan.SeekSeconds ?? 0) + plan.FrameCount / request.Fps;
                double receivedEnd = _receivedEnd;
                // 出力枚数は間引きで変わるためEOF根拠にはしない。既知の入力長と受信した末尾を照合する。
                double endTolerance = Math.Max(0.02, FrameSelector.Duration(Frames[^1], request.Fps) / 2);
                naturalEnd = plannedEnd >= duration - endTolerance && receivedEnd >= duration - endTolerance;
            }
            UpdateSegment(naturalEnd);
            if (_confirmed) return;
            if (!_hasTimestamp)
            {
                // 全フレームが時刻なしと判明するのは終端なので、fpsによる代替選択はここで行う。
                _candidate = FrameSelector.Select(Frames, _target - _clock.FrameIndexOffset / FrameSelector.FrameRate(Frames[0], request.Fps), request.Fps);
                _lastTime = (_receivedFrames - 1) / FrameSelector.FrameRate(Frames[^1], request.Fps);
            }
            var lastTimed = Frames.LastOrDefault(f => f.Xts.HasValue) ?? Frames[^1];
            double tolerance = FrameSelector.Duration(lastTimed, request.Fps) / 2.0;
            TargetNotReached = _lastTime + tolerance < _target;
            if (TargetNotReached) _candidate = Frames.Count - 1;
            // 有限窓が目標へ届かなかったことは入力EOFの証拠にならない。長さと実末尾が一致した場合だけ終端にする。
            UpdateSegment(naturalEnd);
            Confirm(TargetNotReached);
        }
        private void UpdateSegment(bool reachedEnd)
        {
            if (Frames.Count == 0) return;
            double start = PreviewWarmupPolicy.SafeStartSeconds(_physicalRequest, background);
            if (_firstOutputInputSeconds is double first) start = Math.Max(start, first);
            if (_discardedRetainedPrefix) start = Math.Max(start, _clock.ToInputSeconds(Frames[0], 0, request.Fps));
            bool guarded = !background && _physicalRequest.Capabilities.TimestampPassthrough && request.TimestampOriginSeconds.HasValue;
            double retainedEnd = _clock.ToInputSeconds(Frames[^1], Frames.Count - 1, request.Fps) + FrameSelector.Duration(Frames[^1], request.Fps);
            double end = Math.Min(retainedEnd, _receivedEnd - (guarded && !reachedEnd ? PreviewByteWindow.TailGuardSeconds : 0));
            // 有限要求の早期画像は画素照合前なので、完了後にcacheが検証するまでは他区間と混ぜない。
            Segment = new(0, _physicalRequest, _clock, Array.AsReadOnly(Frames.ToArray()), start, end, false, reachedEnd)
                { CanStitch = false };
        }
        private void Confirm(bool notReached)
        {
            if (_confirmed || _candidate < 0) return;
            if (PreviewWindowShortfall.FromSegment(request, Segment) != null)
            { TargetNotReached = true; return; }
            if (!background && _physicalRequest.Capabilities.TimestampPassthrough && Segment is { } segment)
            {
                double time = segment.Clock.ToInputSeconds(Frames[_candidate], _candidate, request.Fps);
                if (!PreviewSafeRange.Contains(time, segment.SafeStartSeconds, segment.SafeEndSeconds))
                { TargetNotReached = true; return; }
            }
            _confirmed = true;
            TargetNotReached = notReached;
            // 早期画像のUI待ちで区間全体を固定しない。完了時の区間は別途通知する。
            var anchor = Segment == null ? null : new PreviewFrameReference(Segment, _candidate).RetainFrameOnly();
            bool early = !_finishing && !notReached && _earlyTimingEligible && _earlyTimedFrames >= MinimumEarlyTimedFrames
                && _clock.TrustedAbsolute;
            _earlyOffered = early;
            emit(new(request, Frames[_candidate], notReached) { Segment = anchor?.Segment, CanDisplayBeforeEnd = early });
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

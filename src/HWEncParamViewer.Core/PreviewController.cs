namespace HWEncParamViewer.Core;

public sealed record PreviewFrameResult(PreviewRequest Request, Y4mFrame Frame, bool TargetNotReached);
public sealed record PreviewCompletion(PreviewRequest Request, PreviewRunResult Result, bool FromCache, bool TargetNotReached)
{
    /// <summary>成功した区間の全フレーム。LRUに保存されない区間でも、現在位置の前後を選べるようにする。</summary>
    public IReadOnlyList<Y4mFrame> Frames { get; init; } = Array.Empty<Y4mFrame>();
}
public sealed record PreviewFailure(PreviewRequest Request, string Reason, PreviewRunResult? Result = null, Exception? Exception = null);

/// <summary>最新要求の実行、画像選択、キャッシュを管理する。イベントは処理中のスレッドで通知するため、GUI側でディスパッチする。</summary>
/// <remarks>
/// イベントは内部ロックを保持して通知する。ハンドラはすぐに戻ること。
/// UIスレッドを同期的に待たず、Postで非同期にディスパッチすること。Dispatcher.Invoke等の同期呼び出しはSubmitとデッドロックしうる。
/// 受け取った画像のDataはキャッシュと共有するので変更しないこと。DisposeAsyncで実行中の子プロセスも回収する。
/// </remarks>
public sealed class PreviewController : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _execution = new(1);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _watchdog;
    private readonly FrameCache _cache;
    private readonly IPreviewBackend _preferred;
    private readonly IPreviewBackend _fallback;
    private readonly bool _enableFallback;
    private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _tasks = new();
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
        TimeSpan? debounce = null, TimeSpan? watchdog = null, bool enableFallback = true)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        _watchdog = watchdog ?? TimeSpan.FromSeconds(30);
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        if (_watchdog <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(watchdog));
        _preferred = backend ?? new ServerBackend(_clock);
        _fallback = fallback ?? new OneShotBackend();
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
    /// <param name="bypassCache">trueなら過去の画像を使わず実行する。直前の要求が後からキャッシュへ保存されても再実行を保証する。</param>
    /// <param name="bypassDebounce">内部の追加要求を即座に実行する場合だけtrue。通常の編集操作は既定の待機時間を使う。</param>
    /// <param name="navigation">移動要求なら待機時間を省き、実行中の要求を完了させてから最新の待機要求を実行する。</param>
    /// <param name="background">背景要求なら前景の待機枠・世代を変更せず、前景がすべて終わってから実行する。編集向けdebounceは適用しない。</param>
    /// <param name="cancellationToken">登録後の待機と実行の両方を取り消す。移動要求の置換による待機取消とは分離する。</param>
    public void Submit(PreviewRequest request, bool bypassCache = false, bool bypassDebounce = false, bool navigation = false,
        bool background = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { InputArgs = request.InputArgs.ToArray(), VppArgs = request.VppArgs.ToArray() };
        _ = CommandBuilder.Plan(request);
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

    /// <summary>この時点までに登録された要求が、後処理を含め完了するまで待つ。</summary>
    public Task WaitForIdleAsync()
    {
        lock (_sync) return Task.WhenAll(_tasks.ToArray());
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

    private Task WaitForForegroundAsync(CancellationToken token)
    {
        lock (_sync) return (_foregroundPending == 0 ? Task.CompletedTask : _foregroundIdle.Task).WaitAsync(token);
    }

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
        CancellationToken cancellationToken)
    {
        bool entered = false;
        long waitStart = 0;
        long executionStart = 0;
        long backendStart = 0;
        CancellationTokenSource? active = null;
        void Publish(Action notification, bool currentOnly = false)
        {
            if (cancellationToken.IsCancellationRequested) return;
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
                if (!IsRequestedCurrent(version, background, editVersion)) return;
                if (!background && !navigation)
                {
                    if (PerfTrace.Enabled && _active != null) PerfTrace.Write("core.cancel_active", new { request.CorrelationId, version });
                    _active?.Cancel();
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
                await _execution.WaitAsync(scheduled.Token).ConfigureAwait(false);
                entered = true;
                if (PerfTrace.Enabled) executionStart = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_sync)
                {
                    if (!IsRequestedCurrent(version, background, editVersion)) return;
                    // SemaphoreSlimの待ち順には依存せず、取得と実行登録の間にも前景を優先する。
                    if (!background || _foregroundPending == 0)
                    {
                        // scheduledを連結すると、次の移動による待機置換まで実行中断へ変わってしまう。
                        active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        _active = active;
                        break;
                    }
                }
                _execution.Release();
                entered = false;
            }
            if (PerfTrace.Enabled)
            {
                PerfTrace.Write("core.execution_enter", new { request.CorrelationId, version, background,
                    waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds });
            }
            var sink = new SelectionSink(request, frame =>
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.target_frame", new { request.CorrelationId, version,
                    frame.Frame.Xts, frame.TargetNotReached });
                Publish(() => FrameReady?.Invoke(frame));
            });
            if (!bypassCache && _cache.TryGet(request, out var frames))
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.cache_hit", new { request.CorrelationId, version, frames = frames.Count });
                foreach (var frame in frames) sink.OnFrame(frame);
                sink.Finish();
                Publish(() => Completed?.Invoke(new(request, new(0, ""), true, sink.TargetNotReached) { Frames = sink.Frames.AsReadOnly() }));
                return;
            }
            IPreviewBackend backend = _unsupported.Contains(request.EncoderPath) ? _fallback : _preferred;
            PreviewRunResult result;
            using var progress = new OutputWatchdog(_clock, _watchdog);
            sink.Progress = progress.Touch;
            if (PerfTrace.Enabled)
            {
                backendStart = System.Diagnostics.Stopwatch.GetTimestamp();
                PerfTrace.Write("core.backend_start", new { request.CorrelationId, version, backend = backend.GetType().Name });
            }
            var run = RunWithFallbackAsync();
            var winner = await Task.WhenAny(run, progress.Expired).ConfigureAwait(false);
            if (winner == progress.Expired)
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.watchdog", new { request.CorrelationId, version,
                    frames = sink.Frames.Count, bytes = sink.ReceivedBytes });
                active.Cancel();
                await backend.AbortAsync().ConfigureAwait(false);
                try { await run.ConfigureAwait(false); } catch (Exception) { /* タイムアウトを優先し、回収後に通知する。 */ }
                Publish(() => Failed?.Invoke(new(request, "出力が進まないためタイムアウトしました")), currentOnly: true);
                return;
            }
            result = await run.ConfigureAwait(false);
            if (PerfTrace.Enabled) PerfTrace.Write("core.backend_end", new { request.CorrelationId, version,
                result.ExitCode, result.IsCancelled, frames = sink.Frames.Count, bytes = sink.ReceivedBytes,
                elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(backendStart).TotalMilliseconds });
            if (active.IsCancellationRequested || result.IsCancelled) return;
            if (result.ExitCode != 0)
            {
                Publish(() => Failed?.Invoke(new(request, $"エンコーダがエラー終了しました: {result.ExitCode}", result)), currentOnly: true);
                return;
            }
            sink.Finish();
            _cache.Store(request, sink.Frames);
            if (PerfTrace.Enabled) PerfTrace.Write("core.cache_store", new { request.CorrelationId, version,
                frames = sink.Frames.Count, bytes = sink.ReceivedBytes, cacheBytes = _cache.SizeBytes,
                cacheEntries = _cache.Count });
            Publish(() => Completed?.Invoke(new(request, result, false, sink.TargetNotReached) { Frames = sink.Frames.AsReadOnly() }));

            async Task<PreviewRunResult> RunWithFallbackAsync()
            {
                try { return await backend.RunAsync(request, sink, active.Token).ConfigureAwait(false); }
                catch (PreviewServerNotSupportedException) when (_enableFallback && ReferenceEquals(backend, _preferred))
                {
                    _unsupported.Add(request.EncoderPath);
                    backend = _fallback;
                    progress.Touch();
                    return await backend.RunAsync(request, sink, active.Token).ConfigureAwait(false);
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
            Publish(() => Failed?.Invoke(new(request, ex.Message, Exception: ex)), currentOnly: true);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, active)) _active = null;
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
                _execution.Release();
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
            _scheduled?.Cancel();
            _backgroundScheduled?.Cancel();
            _active?.Cancel();
            _disposeTask = DisposeCoreAsync(_tasks.ToArray());
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task[] tasks)
    {
        await Task.Yield();
        // cancelを無視するサーバでもDisposeを待ち続けないよう、まず強制停止する。
        await _preferred.AbortAsync().ConfigureAwait(false);
        if (!ReferenceEquals(_preferred, _fallback)) await _fallback.AbortAsync().ConfigureAwait(false);
        await Task.WhenAll(tasks).ConfigureAwait(false);
        await _preferred.DisposeAsync().ConfigureAwait(false);
        if (!ReferenceEquals(_preferred, _fallback)) await _fallback.DisposeAsync().ConfigureAwait(false);
        _execution.Dispose();
    }

    private sealed class SelectionSink(PreviewRequest request, Action<PreviewFrameResult> emit) : IPreviewSink
    {
        private readonly double _target = CommandBuilder.Plan(request).ActualPreMargin;
        private int _candidate = -1;
        private double _candidateTime = double.NegativeInfinity;
        private double _lastTime = double.NegativeInfinity;
        private bool _confirmed;
        private bool _hasTimestamp;
        public List<Y4mFrame> Frames { get; } = new();
        public long ReceivedBytes { get; private set; }
        public Action? Progress { get; set; }
        public bool TargetNotReached { get; private set; }
        public void OnOutput() => Progress?.Invoke();
        public void OnHeader(Y4mHeader header) { }
        public void OnFrame(Y4mFrame frame)
        {
            int index = Frames.Count;
            if (PerfTrace.Enabled)
            {
                ReceivedBytes += frame.Data.LongLength;
                if (index == 0) PerfTrace.Write("core.first_frame", new { request.CorrelationId, frame.Xts,
                    bytes = frame.Data.LongLength, frame.Header.Width, frame.Header.Height });
            }
            Frames.Add(frame);
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
            else if (ts > _target + tolerance && _candidate >= 0) Confirm(false);
        }
        public void Finish()
        {
            if (_confirmed || Frames.Count == 0) return;
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
            Confirm(TargetNotReached);
        }
        private void Confirm(bool notReached)
        {
            if (_confirmed || _candidate < 0) return;
            _confirmed = true;
            TargetNotReached = notReached;
            emit(new(request, Frames[_candidate], notReached));
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
                if (_disposed) return;
                _lastProgress = _clock.GetTimestamp();
                _timer.Change(_timeout, Timeout.InfiniteTimeSpan);
            }
        }
        private void Check()
        {
            lock (_sync)
            {
                if (_disposed) return;
                // Changeより前にキューへ入った古いコールバックでも、最新の受信時刻で再判定する。
                TimeSpan remaining = _timeout - _clock.GetElapsedTime(_lastProgress);
                if (remaining > TimeSpan.Zero) _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                else _expired.TrySetResult();
            }
        }
        public void Dispose() { lock (_sync) { _disposed = true; _timer.Dispose(); } }
    }
}

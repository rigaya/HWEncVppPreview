namespace HWEncParamViewer.Core;

public sealed record PreviewFrameResult(PreviewRequest Request, Y4mFrame Frame, bool TargetNotReached);
public sealed record PreviewCompletion(PreviewRequest Request, PreviewRunResult Result, bool FromCache, bool TargetNotReached);
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
    private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _tasks = new();
    private CancellationTokenSource? _scheduled;
    private CancellationTokenSource? _active;
    private long _version;
    private bool _disposed;
    private Task? _disposeTask;

    public PreviewController(IPreviewBackend? backend = null, IPreviewBackend? fallback = null,
        FrameCache? cache = null, TimeProvider? timeProvider = null,
        TimeSpan? debounce = null, TimeSpan? watchdog = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        _watchdog = watchdog ?? TimeSpan.FromSeconds(30);
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        if (_watchdog <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(watchdog));
        _preferred = backend ?? new ServerBackend(_clock);
        _fallback = fallback ?? new OneShotBackend();
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
    public void Submit(PreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { InputArgs = request.InputArgs.ToArray(), VppArgs = request.VppArgs.ToArray() };
        _ = CommandBuilder.Plan(request);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _scheduled?.Cancel();
            var scheduled = new CancellationTokenSource();
            _scheduled = scheduled;
            long version = ++_version;
            // 同期完了や通知からのDispose再入より先に、回収対象を登録する。
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tasks.Add(completion.Task);
            _ = TrackAsync(ScheduleAsync(request, version, scheduled), completion);
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

    private void Notify(long version, Action notification)
    {
        // 世代の確認と通知を同じ排他区間に置き、Submit後に古い結果を通知しない。
        lock (_sync) { if (IsCurrent(version)) notification(); }
    }

    private async Task ScheduleAsync(PreviewRequest request, long version, CancellationTokenSource scheduled)
    {
        bool entered = false;
        CancellationTokenSource? active = null;
        try
        {
            await Task.Delay(_debounce, _clock, scheduled.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (!IsCurrent(version)) return;
                _active?.Cancel();
            }
            await _execution.WaitAsync(scheduled.Token).ConfigureAwait(false);
            entered = true;
            lock (_sync)
            {
                if (!IsCurrent(version)) return;
                active = new CancellationTokenSource();
                _active = active;
            }
            var sink = new SelectionSink(request, frame => Notify(version, () => FrameReady?.Invoke(frame)));
            if (_cache.TryGet(request, out var frames))
            {
                foreach (var frame in frames) sink.OnFrame(frame);
                sink.Finish();
                Notify(version, () => Completed?.Invoke(new(request, new(0, ""), true, sink.TargetNotReached)));
                return;
            }
            IPreviewBackend backend = _unsupported.Contains(request.EncoderPath) ? _fallback : _preferred;
            PreviewRunResult result;
            using var progress = new OutputWatchdog(_clock, _watchdog);
            sink.Progress = progress.Touch;
            var run = RunWithFallbackAsync();
            var winner = await Task.WhenAny(run, progress.Expired).ConfigureAwait(false);
            if (winner == progress.Expired)
            {
                active.Cancel();
                await backend.AbortAsync().ConfigureAwait(false);
                try { await run.ConfigureAwait(false); } catch (Exception) { /* タイムアウトを優先し、回収後に通知する。 */ }
                Notify(version, () => Failed?.Invoke(new(request, "出力が進まないためタイムアウトしました")));
                return;
            }
            result = await run.ConfigureAwait(false);
            if (active.IsCancellationRequested || result.IsCancelled) return;
            if (result.ExitCode != 0)
            {
                Notify(version, () => Failed?.Invoke(new(request, $"エンコーダがエラー終了しました: {result.ExitCode}", result)));
                return;
            }
            sink.Finish();
            _cache.Store(request, sink.Frames);
            Notify(version, () => Completed?.Invoke(new(request, result, false, sink.TargetNotReached)));

            async Task<PreviewRunResult> RunWithFallbackAsync()
            {
                try { return await backend.RunAsync(request, sink, active.Token).ConfigureAwait(false); }
                catch (PreviewServerNotSupportedException) when (ReferenceEquals(backend, _preferred))
                {
                    _unsupported.Add(request.EncoderPath);
                    backend = _fallback;
                    progress.Touch();
                    return await backend.RunAsync(request, sink, active.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (scheduled.IsCancellationRequested || active?.IsCancellationRequested == true) { }
        catch (Exception ex)
        {
            Notify(version, () => Failed?.Invoke(new(request, ex.Message, Exception: ex)));
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, active)) _active = null;
                if (ReferenceEquals(_scheduled, scheduled)) _scheduled = null;
            }
            active?.Dispose();
            scheduled.Dispose();
            if (entered) _execution.Release();
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
        public Action? Progress { get; set; }
        public bool TargetNotReached { get; private set; }
        public void OnOutput() => Progress?.Invoke();
        public void OnHeader(Y4mHeader header) { }
        public void OnFrame(Y4mFrame frame)
        {
            int index = Frames.Count;
            Frames.Add(frame);
            // 一部だけ時刻が欠ける場合、V1と同じく欠落フレームを選択対象にしない。
            if (frame.Xts is not double ts) return;
            _hasTimestamp = true;
            double tolerance = (frame.Xdur ?? 1.0 / request.Fps) / 2.0;
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
                _candidate = FrameSelector.Select(Frames.Select(f => new FrameTiming(f.Xts, f.Xdur)).ToArray(), _target, request.Fps);
                _lastTime = (Frames.Count - 1) / request.Fps;
            }
            var lastTimed = Frames.LastOrDefault(f => f.Xts.HasValue) ?? Frames[^1];
            double tolerance = (lastTimed.Xdur ?? 1.0 / request.Fps) / 2.0;
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

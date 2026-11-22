using System.Diagnostics;
using System.Threading.Channels;

namespace HWEncVppPreview.Core;

/// <summary>画像の入力先頭基準の時刻と、エンコーダが返した生の時刻・持続。</summary>
public readonly record struct EncoderPreviewFrameTiming(double InputSeconds, double? Xts, double? Xdur, double DurationSeconds);

public enum EncoderPreviewDisplayMode { After, Before, Split, SideBySide }
public enum EncoderPreviewSeekPhase { Drag, Commit }

/// <summary>操作の世代、表示の更新と処理状態。画像を保持しない。</summary>
public sealed record EncoderPreviewState(long Generation, long ViewRevision, double CurrentSeconds,
    double? DurationSeconds, InputInfo? Input, EncoderPreviewDisplayMode DisplayMode, PreviewProgress Progress,
    bool IsBusy, bool IsBeforeBusy, string? Error, string Diagnostics, bool BeforeUnavailable);

/// <summary>表示スナップショットに属する、選択済みの画像と時刻。</summary>
public sealed record EncoderPreviewFrame(Y4mFrame Frame, EncoderPreviewFrameTiming Timing, long Generation, bool IsProvisional = false);

/// <summary>ある表示更新で取得した画像。使用後は破棄し、破棄後は画像へアクセスしない。</summary>
public sealed class EncoderPreviewView : IDisposable
{
    private IDisposable? _lease;

    internal EncoderPreviewView(EncoderPreviewState state, EncoderPreviewFrame? after, EncoderPreviewFrame? before,
        IDisposable? lease)
    {
        State = state;
        After = after;
        Before = before;
        _lease = lease;
    }

    public EncoderPreviewState State { get; }
    public EncoderPreviewFrame? After { get; }
    public EncoderPreviewFrame? Before { get; }

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

public sealed partial class EncoderPreviewSession
{
    /// <summary>画面に依存せずプレビュー操作を受け付けるセッションを作る。</summary>
    public EncoderPreviewSession()
        : this(TimeProvider.System, PreviewTimeouts.Default, null, null, null, null)
    {
    }

    internal EncoderPreviewSession(TimeProvider clock, PreviewTimeouts timeouts, IPreviewBackend? backend,
        InputProbe? probe, TimeSpan? debounce, Func<string, string, CancellationToken, Task<string>>? readTextOverride,
        IPreviewBackend? backgroundBackend = null, RawOwnershipScope? ownershipScope = null)
        : this(new HeadlessHost(), clock, timeouts, backend, backgroundBackend, probe, debounce, readTextOverride,
            ownershipScope ?? (backend == null && backgroundBackend == null ? new RawOwnershipScope(new RawBufferPool()) : null))
    {
    }

    private EncoderPreviewSession(HeadlessHost host, TimeProvider clock, PreviewTimeouts timeouts,
        IPreviewBackend? backend, IPreviewBackend? backgroundBackend, InputProbe? probe, TimeSpan? debounce,
        Func<string, string, CancellationToken, Task<string>>? readTextOverride, RawOwnershipScope? scope)
        : this(clock, timeouts, backend, backgroundBackend, scope == null ? new FrameCache() : new FrameCache(scope),
            scope, probe, debounce, readTextOverride, host, host.Post, beforeEnabled: true)
    {
        host.Start(this);
    }

    /// <summary>状態の変化を画像とは別に通知する。購読側は同じセッションの操作を同期待ちしない。</summary>
    public event Action<EncoderPreviewState>? StateChanged;

    /// <summary>設定を適用し、入力検査と画像要求の登録を待つ。画像の到着は状態通知で確認できる。</summary>
    public async Task<EncoderPreviewState> ConfigureAsync(EncoderPreviewConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Encoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Input);
        // 受付後に呼び出し側のリストが変わっても、登録した設定を変えない。
        var settings = configuration with
        {
            InputArgs = Array.AsReadOnly(configuration.InputArgs.ToArray()),
            VppArgs = Array.AsReadOnly(configuration.VppArgs.ToArray()),
        };
        Task preparation = await DispatchOperationAsync(host =>
        {
            var previous = host.Configuration;
            bool encoderChanged = previous?.Encoder != settings.Encoder;
            bool inputChanged = encoderChanged || previous is null || previous.Input != settings.Input
                || !previous.InputArgs.SequenceEqual(settings.InputArgs);
            host.Configuration = settings;

            if (encoderChanged)
            {
                CancelEncoderInitializationTracked();
                ClearMetadata();
            }
            if (inputChanged)
            {
                ClearInputInfo();
                ClearInterval();
                DiscardInputRequests();
                IsInitialized = true;
                return QueuePreview(bypassDebounce: true);
            }

            return FiltersChanged(bypassDebounce: true);
        }, cancellationToken).ConfigureAwait(false);
        // 世代の置換は次の操作に任せ、呼び出し元の取消はこの待機だけに適用する。
        await preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await GetStateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>入力先頭を基準に時刻を指定する。ドラッグ確定は同じ目標でも比較画像を進める。</summary>
    public Task<EncoderPreviewState> SeekAsync(double seconds, EncoderPreviewSeekPhase phase = EncoderPreviewSeekPhase.Commit,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        return DispatchOperationAsync(host =>
        {
            if (phase == EncoderPreviewSeekPhase.Drag)
            {
                BeginSeekDrag();
                SliderChanged(seconds);
            }
            else if (_seekDragging) EndSeekDrag(ClampTime(seconds));
            else SeekTo(seconds, allowConcurrentBefore: true);
            return CaptureOperationState(host);
        }, cancellationToken);
    }

    /// <summary>実際の出力の並びで、負方向または正方向に一コマ移動する。</summary>
    public Task<EncoderPreviewState> StepAsync(int direction, CancellationToken cancellationToken = default)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        return DispatchOperationAsync(host =>
        {
            StepFrame(direction);
            return CaptureOperationState(host);
        }, cancellationToken);
    }

    /// <summary>選択時刻を保って表示モードを変更する。</summary>
    public Task<EncoderPreviewState> SetDisplayAsync(EncoderPreviewDisplayMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return DispatchOperationAsync(host =>
        {
            if (host.DisplayMode != mode)
            {
                host.DisplayMode = mode;
                ++_viewRevision;
                UpdateBeforeVisibility(mode != EncoderPreviewDisplayMode.After, mode == EncoderPreviewDisplayMode.Before);
                if (mode == EncoderPreviewDisplayMode.Before && !_hasBeforeImage) PrioritizeBefore();
            }
            return CaptureOperationState(host);
        }, cancellationToken);
    }

    /// <summary>現在の時刻をキャッシュを使わずに再取得する。</summary>
    public Task<EncoderPreviewState> ReloadAsync(CancellationToken cancellationToken = default)
        => DispatchOperationAsync(host =>
        {
            _ = Reload();
            return CaptureOperationState(host);
        }, cancellationToken);

    public Task<EncoderPreviewState> GetStateAsync(CancellationToken cancellationToken = default)
        => DispatchOperationAsync(CaptureOperationState, cancellationToken);

    /// <summary>選択済み画像を取得する。指定した表示更新が失効していればnullを返す。</summary>
    /// <remarks>取得した画像はセッションの終了前に破棄する。</remarks>
    public Task<EncoderPreviewView?> AcquireViewAsync(long? revision = null, CancellationToken cancellationToken = default)
        => DispatchOperationAsync(host => revision.HasValue && revision.Value != _viewRevision
            ? null : CaptureView(CaptureOperationState(host)), cancellationToken);

    /// <summary>表示状態と同じ時点の画素を通知する。受け取り側が画像を破棄し、通知中は待機しない。</summary>
    public event Action<EncoderPreviewView>? ViewChanged;

    private EncoderPreviewView CaptureView(EncoderPreviewState state)
    {
        var after = _hasAfterImage && _displayedFrame is { } currentAfter
            ? new EncoderPreviewFrame(currentAfter, _afterPresentation, _afterPresentedGeneration) : null;
        var before = _hasBeforeImage && _beforeDisplayedFrame is { } currentBefore
            ? new EncoderPreviewFrame(currentBefore, _beforePresentation, _beforePresentedGeneration, _beforeProvisional) : null;
        var frames = new[] { after?.Frame, before?.Frame }.OfType<Y4mFrame>().Distinct().ToArray();
        return new(state, after, before, RetainRawFrames(frames, "EncoderPreviewView"));
    }

    private EncoderPreviewState CaptureOperationState(HeadlessHost host)
        => new(Volatile.Read(ref _generation), _viewRevision, CurrentSeconds,
            HasDuration ? DurationSeconds : null, _inputInfo, host.DisplayMode, host.Progress,
            host.IsBusy, host.IsBeforeBusy, host.ErrorText, host.LogText, _beforeUnavailableGeneration == _generation);

    private Task<T> DispatchOperationAsync<T>(Func<HeadlessHost, T> operation, CancellationToken cancellationToken)
    {
        if (_host is not HeadlessHost host)
            throw new InvalidOperationException("画面と接続したセッションでは操作を直接呼び出せません。");
        return host.DispatchAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return operation(host);
        });
    }

    private Task DispatchOperationShutdown(Func<Task> shutdown)
        => _host is HeadlessHost host ? host.StartShutdown(shutdown) : shutdown();

    private Task StopOperationDispatcherAsync()
    {
        if (_host is not HeadlessHost host) return Task.CompletedTask;
        Task stopped = host.StopAsync();
        if (Volatile.Read(ref _shutdownIncomplete) == 0) return stopped;
        // 終了の総期限に達したときは、配送の残りを観測しながら回収する。
        ObserveVmShutdownTask(stopped);
        return Task.CompletedTask;
    }

    private sealed class HeadlessHost : IEncoderPreviewHost
    {
        private readonly Channel<Action> _operations = Channel.CreateUnbounded<Action>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly object _shutdownSync = new();
        private Task? _shutdown;
        private Task _worker = Task.CompletedTask;
        private EncoderPreviewSession _owner = null!;

        internal EncoderPreviewConfiguration? Configuration { get; set; }
        internal EncoderPreviewDisplayMode DisplayMode { get; set; }
        public bool IsBusy { get; set; }
        public bool IsBeforeBusy { get; set; }
        public string? ErrorText { get; set; }
        public PreviewProgress Progress { get; set; } = PreviewProgress.SelectEncoderAndInput;
        public string LogText { get; set; } = "";
        public string EncoderPath => Configuration?.Encoder ?? "";
        public string InputPath => Configuration?.Input ?? "";

        internal void Start(EncoderPreviewSession owner)
        {
            _owner = owner;
            _worker = RunAsync();
        }

        internal void Post(Action operation)
        {
            if (!_operations.Writer.TryWrite(operation))
                throw new ObjectDisposedException(nameof(EncoderPreviewSession));
        }

        internal Task<T> DispatchAsync<T>(Func<T> operation)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_operations.Writer.TryWrite(() =>
            {
                try { result.SetResult(operation()); }
                catch (OperationCanceledException exception) { result.SetCanceled(exception.CancellationToken); }
                catch (Exception exception) { result.SetException(exception); }
            }))
                result.SetException(new ObjectDisposedException(nameof(EncoderPreviewSession)));
            return result.Task;
        }

        internal Task StartShutdown(Func<Task> shutdown)
        {
            lock (_shutdownSync)
                return _shutdown ??= DispatchAsync(shutdown).Unwrap();
        }

        internal Task StopAsync()
        {
            _operations.Writer.TryComplete();
            return _worker;
        }

        private async Task RunAsync()
        {
            var previous = _owner.CaptureOperationState(this);
            await foreach (var operation in _operations.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try { operation(); }
                catch (Exception exception) { Trace.TraceError("プレビューの配送処理に失敗しました: {0}", exception); }

                var state = _owner.CaptureOperationState(this);
                if (state == previous) continue;
                previous = state;
                try { _owner.StateChanged?.Invoke(state); }
                catch (Exception exception) { Trace.TraceError("プレビューの状態通知に失敗しました: {0}", exception); }
                if (_owner.ViewChanged is not { } observers) continue;
                foreach (Action<EncoderPreviewView> observer in observers.GetInvocationList())
                {
                    var view = _owner.CaptureView(state);
                    try { observer(view); }
                    catch (Exception exception)
                    {
                        view.Dispose();
                        Trace.TraceError("プレビューの画像通知に失敗しました: {0}", exception);
                    }
                }
            }
        }

        public EncoderPreviewConfiguration GetConfiguration(string encoder, string input)
            => Configuration! with { Encoder = encoder, Input = input };
        public IReadOnlyList<string> GetInputArgs() => Configuration?.InputArgs ?? Array.Empty<string>();
        public void ApplyMetadata(string encoder, EncoderPreviewMetadata metadata) { }
        public void MetadataReady() { }
        public void MetadataUnavailable(string message) => LogText = message;
        public void ShowInputInfo(InputInfo info) { }

        public void ShowExceptionError(string message, string diagnostics, Exception? exception)
        {
            IsBusy = false;
            Progress = PreviewProgress.Error;
            ErrorText = message;
            LogText = diagnostics;
        }

        public void ShowPreviewFailure(PreviewFailureKind kind, string diagnostics, Exception? exception)
            => ShowExceptionError(kind == PreviewFailureKind.NoDisplayableFrames
                ? "表示できるフレームがありません。" : "フレームを表示できませんでした。", diagnostics, exception);

        public Task ConvertAndPresentAsync(ImageWork image)
            => _owner.PostAsync(() => _owner.ApplyImage(image));

        public void CancelInputEdit() { }
        public void ClearBeforeImage() => _owner.ClearBeforePresentation();
        public void StateChanged(string property) { }
        public void ShowTime(double seconds) { }
        public void ShowDuration(double? seconds) { }
        public void ShowElapsed(double seconds) { }
        public void BeginShutdown() { }
        public void ClearAfterImage() => _owner.ClearAfterPresentation();
        public void RefreshColorInfo() { }
    }
}

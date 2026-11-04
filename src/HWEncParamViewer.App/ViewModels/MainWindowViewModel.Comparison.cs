using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

/// <summary>プレビュー画像の比較表示方式。</summary>
public enum PreviewCompareMode { Single, Toggle, Split, SideBySide }

public sealed partial class MainWindowViewModel
{
    private PreviewCompareMode _compareMode;
    private bool _isShowingBefore;
    private Bitmap? _beforeImage;
    private bool _isBeforeBusy;
    private double _beforeDisplayWidth = 1920, _beforeDisplayHeight = 1080;
    private double _splitPosition = 0.5;
    private bool _useSar = true;
    private long _beforeImageGeneration;
    private Y4mFrame? _beforeDisplayedFrame;
    private PreviewRequest? _beforeIntervalRequest;
    private IReadOnlyList<Y4mFrame> _beforeIntervalFrames = Array.Empty<Y4mFrame>();
    private CancellationTokenSource? _beforeCancellation;
    private RequestContext? _beforeExecutionContext;
    private PreviewRequest? _beforeSelectionRequest;
    private TaskCompletionSource? _beforeImmediate;
    private bool _beforeFinished;
    private string _beforeFallbackDiagnostic = "";
    private long? _lastNavigationIntent;
    private static readonly TimeSpan BeforeQuietPeriod = TimeSpan.FromMilliseconds(300);

    /// <summary>Toggleは単一表示に正規化し、Spaceの押下状態はIsShowingBeforeで表す。</summary>
    public PreviewCompareMode CompareMode
    {
        get => _compareMode;
        set
        {
            var normalized = value is PreviewCompareMode.Split or PreviewCompareMode.SideBySide ? value : PreviewCompareMode.Single;
            if (SetProperty(ref _compareMode, normalized) && normalized != PreviewCompareMode.Single) IsShowingBefore = false;
        }
    }
    public bool IsShowingBefore
    {
        get => _isShowingBefore;
        set
        {
            if (!SetProperty(ref _isShowingBefore, value && CompareMode == PreviewCompareMode.Single)) return;
            RefreshColorInfo();
            if (!_isShowingBefore) return;
            // Spaceは遅延だけを解除し、すでに実行中の元画像は取り消さない。
            if (BeforeImage == null)
            {
                if (_beforeFinished && _beforeSelectionRequest is { } request) QueueBefore(request, Volatile.Read(ref _generation));
                _beforeImmediate?.TrySetResult();
            }
        }
    }
    /// <summary>前画像は独立したビットマップとして所有し、後画像と同じインスタンスを共有しない。</summary>
    public Bitmap? BeforeImage
    {
        get => _beforeImage;
        set { var previous = _beforeImage; if (SetProperty(ref _beforeImage, value)) { previous?.Dispose(); RefreshColorInfo(); } }
    }
    public bool IsBeforeBusy { get => _isBeforeBusy; set => SetProperty(ref _isBeforeBusy, value); }
    public double BeforeDisplayWidth { get => _beforeDisplayWidth; set => SetProperty(ref _beforeDisplayWidth, value); }
    public double BeforeDisplayHeight { get => _beforeDisplayHeight; set => SetProperty(ref _beforeDisplayHeight, value); }
    public double SplitPosition
    {
        get => _splitPosition;
        set => SetProperty(ref _splitPosition, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5);
    }
    public bool UseSar
    {
        get => _useSar;
        set { if (SetProperty(ref _useSar, value)) RecalculateDisplaySizes(); }
    }
    public ICommand SetCompareModeCommand { get; }

    private void CancelBefore(bool updatePresentation = true)
    {
        _beforeFallbackDiagnostic = "";
        if (PerfTrace.Enabled) PerfTrace.Write("vm.cancel.before", new { active = _beforeCancellation != null });
        // キャンセルと破棄を同じ排他区間に置き、古い要求の後処理との競合を防ぐ。
        lock (_taskLock)
        {
            _beforeCancellation?.Cancel();
            _beforeCancellation = null;
            _pendingBeforeImage = null;
        }
        Volatile.Write(ref _beforeExecutionContext, null);
        _beforeSelectionRequest = null;
        _beforeImmediate = null;
        _beforeFinished = false;
        Interlocked.Increment(ref _beforeImageGeneration);
        if (!updatePresentation) return;
        _beforeDisplayedFrame = null;
        IsBeforeBusy = false;
        BeforeImage = null;
    }

    private void ClearBeforeInterval()
    {
        _beforeIntervalRequest = null;
        _beforeIntervalSegment = null;
        _beforeIntervalFrames = Array.Empty<Y4mFrame>();
    }

    private void RecordNavigationIntent()
    {
        if (_initialized && !_suspendChanges && !_disposed)
        {
            long timestamp = _clock.GetTimestamp();
            lock (_taskLock) _lastNavigationIntent = timestamp;
        }
    }

    private TimeSpan BeforeDelayRemaining()
    {
        long? last;
        lock (_taskLock) last = _lastNavigationIntent;
        return last is long timestamp ? BeforeQuietPeriod - _clock.GetElapsedTime(timestamp) : TimeSpan.Zero;
    }

    private void CheckBeforeInterval(PreviewRequest afterRequest)
    {
        if (_beforeExecutionContext is { } context && !ContainsBeforeTarget(context.Request, afterRequest)) CancelBefore();
    }

    private void QueueBefore(PreviewRequest afterRequest, long generation, double? cachedTarget = null)
    {
        if (!IsCurrent(generation)) return;
        if (PerfTrace.Enabled && Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_DISABLE_BEFORE") == "1")
        {
            PerfTrace.Write("vm.queue.before.disabled", new { generation });
            return;
        }
        if (PerfTrace.Enabled) PerfTrace.Write("vm.queue.before", new { generation, cachedTarget });
        // 元画像は独立した有限窓。Afterの長寿命seekや拡大した前余白を引き継がない。
        var before = afterRequest with { VppArgs = Array.Empty<string>(), CorrelationId = -generation,
            Continuous = false, SeekSecondsOverride = null, PreMarginSeconds = 2, PostMarginSeconds = BenchmarkPostMargin() };
        if (cachedTarget is double target) before = RetargetInterval(before, target);
        CheckBeforeInterval(before);
        _beforeSelectionRequest = before;
        IsBeforeBusy = true;
        if (TryGetBeforeInterval(before, out var frames))
        {
            if (PerfTrace.Enabled) PerfTrace.Write("vm.before.cache", new { generation });
            _beforeIntervalFrames = frames;
            var frame = SelectBeforeFrame(before, frames);
            if (frame == null) { IsBeforeBusy = false; return; }
            DisplayBeforeFrame(frame, generation, finishBusy: true);
            return;
        }
        // 同一区間なら、待機や実行を保ったまま選択する目標だけを更新する。
        if (_beforeExecutionContext is { } existing && !_beforeFinished && ContainsBeforeTarget(existing.Request, before)) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var context = new RequestContext(generation, before, _clock.GetTimestamp(), RequestKind.Before, cancellation,
            editEpoch: Volatile.Read(ref _editEpoch));
        var immediate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_taskLock) _beforeCancellation = cancellation;
        _beforeImmediate = immediate;
        if (IsShowingBefore) immediate.TrySetResult();
        _beforeFinished = false;
        Volatile.Write(ref _beforeExecutionContext, context);
        Track(ScheduleBeforeAsync(context, immediate));
    }

    private async Task ScheduleBeforeAsync(RequestContext context, TaskCompletionSource immediate)
    {
        var token = context.BeforeCancellation!.Token;
        bool submitted = false;
        try
        {
            while (true)
            {
                while (!immediate.Task.IsCompleted)
                {
                    TimeSpan remaining = BeforeDelayRemaining();
                    if (remaining <= TimeSpan.Zero) break;
                    // Spaceで待ちだけを終えられるよう、実行用とは別の取消源でタイマーを回収する。
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var delay = Task.Delay(remaining, _clock, wait.Token);
                    await Task.WhenAny(delay, immediate.Task).ConfigureAwait(false);
                    wait.Cancel();
                    try { await delay.ConfigureAwait(false); } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
                    token.ThrowIfCancellationRequested();
                }
                token.ThrowIfCancellationRequested();
                bool retry = false;
                await PostAsync(() =>
                {
                    if (!IsCurrent(context) || _beforeSelectionRequest is not { } selection) return;
                    if (TryGetBeforeInterval(selection, out var frames))
                    {
                        CompleteBefore(context, frames);
                        return;
                    }
                    // Postを待つ間にも移動できるため、実行登録の直前に静止期間を再確認する。
                    if (!immediate.Task.IsCompleted && BeforeDelayRemaining() > TimeSpan.Zero)
                    {
                        retry = true;
                        return;
                    }
                    if (PerfTrace.Enabled) PerfTrace.Write("vm.before.execute", new { generation = context.Generation });
                    // Coreの背景枠を使い、前景の待機・実行・通知の世代を変更しない。
                    // 実受信範囲で確認済み。CoreのsafeEndは末尾の全durationまで含むが、
                    // Beforeの再利用は半durationまでなので、miss後に同じ旧末尾へ戻さない。
                    _controller.Submit(context.Request, bypassCache: true, bypassDebounce: true, background: true, cancellationToken: token);
                    submitted = true;
                    Track(FinishBeforeAsync(context));
                }).ConfigureAwait(false);
                if (!retry) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await PostAsync(() =>
            {
                if (!IsCurrent(context)) return;
                _beforeFinished = true;
                IsBeforeBusy = false;
                BeforeImage = null;
                if (PerfTrace.Enabled) PerfTrace.Write("vm.before.error", new { exception.Message });
            }).ConfigureAwait(false);
        }
        finally { if (!submitted) ReleaseBefore(context); }
    }

    private async Task FinishBeforeAsync(RequestContext context)
    {
        try { await _controller.WaitForIdleAsync().ConfigureAwait(false); }
        finally { ReleaseBefore(context); }
    }

    private void ReleaseBefore(RequestContext context)
    {
        lock (_taskLock)
        {
            if (ReferenceEquals(_beforeCancellation, context.BeforeCancellation)) _beforeCancellation = null;
            context.BeforeCancellation?.Dispose();
        }
        // 完了通知はUIへPostされているため、対応するcontextは反映まで保持する。
    }

    private void CompleteBefore(RequestContext context, IReadOnlyList<Y4mFrame> frames)
    {
        _beforeIntervalRequest = context.Request;
        _beforeIntervalSegment = context.Segment;
        _beforeIntervalFrames = frames;
        _beforeFinished = true;
        IsBeforeBusy = false;
        // 実行開始時の目標ではなく、同じ区間で最後に移動した位置を選ぶ。
        var selection = RetargetInterval(context.Request, CurrentSeconds);
        if (!ContainsBeforeTarget(context.Request, selection)) return;
        var frame = SelectBeforeFrame(selection, frames);
        if (frame != null) DisplayBeforeFrame(frame, Volatile.Read(ref _generation), finishBusy: true, revisionContext: context);
    }

    private bool TryGetBeforeInterval(PreviewRequest request, out IReadOnlyList<Y4mFrame> frames)
    {
        ValidateNavigationState();
        if (request.Capabilities.TimestampPassthrough && _cache.TryGetAt(request, request.TargetSeconds, out var segment)
            && ContainsBeforeFrameTarget(segment.PhysicalRequest, segment.Frames, request.TargetSeconds, segment))
        {
            _beforeIntervalRequest = segment.PhysicalRequest;
            _beforeIntervalSegment = segment;
            frames = segment.Frames;
            return true;
        }
        if (_cache.TryGet(request, out frames, out var physical)
            && ContainsFrameTarget(physical, frames, request.TargetSeconds, ClockFor(physical, frames)))
        { _beforeIntervalRequest = physical; _beforeIntervalSegment = null; return true; }
        // フィルタなしなら、長寿命Afterの同じフレームを共有し、元画像用のプロセスを起動しない。
        if (_intervalRequest is { VppArgs.Count: 0 } after && SameBeforeInput(after, request)
            && ContainsBeforeFrameTarget(after, _intervalFrames, request.TargetSeconds, _intervalSegment))
        { _beforeIntervalRequest = after; _beforeIntervalSegment = _intervalSegment; frames = _intervalFrames; return true; }
        // 最新目標で計画窓を上書きせず、取得した区間の実時刻と安全範囲だけで再利用を判断する。
        if (_beforeIntervalRequest is { } previous && SameBeforeInput(previous, request)
            && ContainsBeforeFrameTarget(previous, _beforeIntervalFrames, request.TargetSeconds, _beforeIntervalSegment)
            && (_beforeIntervalSegment is not { } held
                || request.TargetSeconds >= held.SafeStartSeconds - 1e-8 && request.TargetSeconds <= held.SafeEndSeconds + 1e-8))
        { frames = _beforeIntervalFrames; return true; }
        frames = Array.Empty<Y4mFrame>();
        return false;
    }

    private bool ContainsBeforeFrameTarget(PreviewRequest request, IReadOnlyList<Y4mFrame> frames, double target, PreviewSegmentSnapshot? segment)
    {
        if (ContainsFrameTarget(request, frames, target, segment?.Clock)) return true;
        // 自然終端が確定した既知Durationへの移動だけは、Afterと同じ最終画像を共有する。
        // 通常の取得窓の末尾を、この終端例外で延長しない。
        if (segment?.ReachedInputEnd != true || frames.Count == 0 || request.InputDurationSeconds is not double end
            || Math.Abs(target - end) > 1e-8) return false;
        var last = frames[^1];
        double lastSeconds = segment.Clock.ToInputSeconds(last, frames.Count - 1, request.Fps);
        return lastSeconds <= end + 1e-8 && lastSeconds + FrameSelector.Duration(last, request.Fps) >= end - 1e-8;
    }

    private bool SameBeforeInput(PreviewRequest a, PreviewRequest b) => a.EncoderPath == b.EncoderPath
        && a.InputFile == b.InputFile && a.Fps == b.Fps
        && (a.InputArgs.SequenceEqual(b.InputArgs) || IsKnownLegacyPhysicalSource(a, b)
            && SameArgumentsWithoutPassthrough(a.InputArgs, b.InputArgs))
        && a.Capabilities.OpenClTaskThreads == b.Capabilities.OpenClTaskThreads
        && (a.Capabilities.TimestampPassthrough == b.Capabilities.TimestampPassthrough || IsKnownLegacyPhysicalSource(a, b))
        && a.TimestampOriginSeconds == b.TimestampOriginSeconds;

    // 保持している有限窓に最新目標が含まれる間は、実行中の元画像を取り消さない。
    private bool ContainsBeforeTarget(PreviewRequest first, PreviewRequest second)
    {
        if (!SameBeforeInput(first, second)) return false;
        var plan = CommandBuilder.Plan(first);
        double start = plan.SeekSeconds ?? 0;
        double end = start + (plan.FrameCount - 1) / first.Fps;
        return second.TargetSeconds >= start - 1e-8 && second.TargetSeconds <= end + 1e-8;
    }

    private bool ContainsFrameTarget(PreviewRequest request, IReadOnlyList<Y4mFrame> frames, double target, PreviewClockMap? clock)
    {
        if (frames.Count == 0) return false;
        clock ??= ClockFor(request, frames);
        double first = clock.ToInputSeconds(frames[0], 0, request.Fps);
        double last = clock.ToInputSeconds(frames[^1], frames.Count - 1, request.Fps);
        return target >= first - FrameSelector.Duration(frames[0], request.Fps) / 2
            && target <= last + FrameSelector.Duration(frames[^1], request.Fps) / 2;
    }

    private Y4mFrame? SelectBeforeFrame(PreviewRequest request, IReadOnlyList<Y4mFrame> frames)
    {
        if (frames.Count == 0) return null;
        if (_beforeIntervalSegment is { Clock.TrustedAbsolute: true } segment)
        {
            var navigation = NavigationFrames(request, segment);
            int selected = FrameSelector.Select(navigation.Select(reference => new FrameTiming(reference.InputSeconds,
                FrameSelector.Duration(reference.Frame, reference.Segment.PhysicalRequest.Fps))).ToArray(), request.TargetSeconds, request.Fps);
            if (selected >= 0)
            {
                _beforeIntervalSegment = navigation[selected].Segment;
                return navigation[selected].Frame;
            }
            return null;
        }
        var clock = _beforeIntervalSegment?.Clock ?? ClockFor(request, frames);
        double target = clock.ToFrameSeconds(request.TargetSeconds);
        int index = FrameSelector.Select(frames, target, request.Fps);
        var last = frames.LastOrDefault(frame => frame.Xts.HasValue) ?? frames[^1];
        double lastTime = last.Xts ?? (frames.Count - 1) / FrameSelector.FrameRate(last, request.Fps);
        if (lastTime + FrameSelector.Duration(last, request.Fps) / 2 < target) index = frames.Count - 1;
        return index < 0 ? null : frames[index];
    }

    private void DisplayBeforeFrame(Y4mFrame frame, long generation, bool finishBusy = false, RequestContext? revisionContext = null)
    {
        revisionContext ??= Volatile.Read(ref _context);
        long revision = revisionContext == null ? 0 : Volatile.Read(ref revisionContext.DisplayRevision);
        long imageGeneration = Interlocked.Increment(ref _beforeImageGeneration);
        var request = _beforeSelectionRequest;
        var clock = _beforeIntervalSegment?.Clock ?? (request == null ? null : ClockFor(request, new[] { frame }));
        EnqueueImage(new ImageWork(frame, generation, Volatile.Read(ref _editEpoch), Before: true,
            BeforeVersion: imageGeneration, FinishBeforeBusy: finishBusy,
            DisplaySeconds: PerfTrace.Enabled && frame.Xts.HasValue && request != null && clock != null
                ? clock.ToInputSeconds(frame, 0, request.Fps) : null,
            SegmentId: _beforeIntervalSegment?.SegmentId, TrustedAbsolute: clock?.TrustedAbsolute ?? false,
            RevisionContext: revisionContext, DisplayRevision: revision));
    }

    private void CaptureBeforeFallbackDiagnostic(string diagnostics)
    {
        const string prefix = "[preview] timestamp fallback: ";
        string? line = diagnostics.Split('\n').Select(value => value.TrimEnd('\r'))
            .LastOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (line == null) return;
        _beforeFallbackDiagnostic = line;
        LogText = AppendBeforeFallbackDiagnostic(LogText);
    }

    private string AppendBeforeFallbackDiagnostic(string diagnostics)
    {
        if (_beforeFallbackDiagnostic.Length == 0 || diagnostics.Contains(_beforeFallbackDiagnostic, StringComparison.Ordinal))
            return diagnostics;
        // Beforeの理由だけを追記し、Afterの画像・状態・既存ログは書き換えない。
        return diagnostics.Length == 0 ? _beforeFallbackDiagnostic
            : diagnostics.TrimEnd('\r', '\n') + Environment.NewLine + _beforeFallbackDiagnostic;
    }

    private void RecalculateDisplaySizes()
    {
        RefreshColorInfo();
        if (_displayedFrame is { } after)
        {
            DisplayWidth = DisplayWidthFor(after.Header);
            DisplayHeight = after.Header.Height;
        }
        if (_beforeDisplayedFrame is { } before)
        {
            BeforeDisplayWidth = DisplayWidthFor(before.Header);
            BeforeDisplayHeight = before.Header.Height;
        }
    }
    private double DisplayWidthFor(Y4mHeader header) => header.Width * (UseSar && header.SarNum > 0 && header.SarDen > 0
        ? (double)header.SarNum / header.SarDen : 1);

    private static WriteableBitmap CreateBitmap(Y4mFrame frame, byte[] data, long traceId = 0)
    {
        if (PerfTrace.Enabled) PerfTrace.Write("vm.bitmap.start", new { traceId, frame.Xts });
        var header = frame.Header;
        var bitmap = new WriteableBitmap(new PixelSize(header.Width, header.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        try
        {
            using var locked = bitmap.Lock();
            for (int y = 0; y < header.Height; y++) Marshal.Copy(data, y * header.Width * 4, locked.Address + y * locked.RowBytes, header.Width * 4);
            if (PerfTrace.Enabled) PerfTrace.Write("vm.bitmap.end", new { traceId, frame.Xts });
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}

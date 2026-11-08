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
    private readonly bool _beforeEnabled;
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
    private BeforePhysicalWindow? _beforePhysicalWindow;
    private PreviewRequest? _beforeRecoverySelection;
    private PreviewRequest? _beforeRescueRetrySelection;
    private sealed record BeforePhysicalWindow(RequestContext Context, PreviewRequest Request);
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
            if (!SetProperty(ref _compareMode, normalized)) return;
            if (normalized != PreviewCompareMode.Single) IsShowingBefore = false;
            RefreshBeforeSegmentProtection();
        }
    }
    public bool IsShowingBefore
    {
        get => _isShowingBefore;
        set
        {
            if (!SetProperty(ref _isShowingBefore, value && CompareMode == PreviewCompareMode.Single)) return;
            RefreshColorInfo();
            RefreshBeforeSegmentProtection();
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
        set
        {
            if (value == null) SelectBeforePresentationSegment(null);
            var previous = _beforeImage;
            if (!SetProperty(ref _beforeImage, value)) return;
            previous?.Dispose();
            if (value == null && _usesOwnedRaw) SetRawFrame(ref _beforeDisplayedFrame, ref _beforeDisplayedRaw, null, "VmBeforeDisplayed");
            RefreshColorInfo();
        }
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
        if (updatePresentation) _beforeRescueRetrySelection = null;
        if (PerfTrace.Enabled) PerfTrace.Write("vm.cancel.before", new { active = _beforeCancellation != null });
        // キャンセルと破棄を同じ排他区間に置き、古い要求の後処理との競合を防ぐ。
        lock (_taskLock)
        {
            _beforeCancellation?.Cancel();
            _beforeCancellation = null;
            DropImage(ref _pendingBeforeImage);
            _beforePhysicalWindow = null;
        }
        if (_beforeExecutionContext is { } previousContext)
        {
            DropRawPosts(context => ReferenceEquals(context, previousContext));
            previousContext.Segment = null;
        }
        Volatile.Write(ref _beforeExecutionContext, null);
        _beforeSelectionRequest = null;
        _beforeImmediate = null;
        _beforeFinished = false;
        Interlocked.Increment(ref _beforeImageGeneration);
        if (!updatePresentation) return;
        SetRawFrame(ref _beforeDisplayedFrame, ref _beforeDisplayedRaw, null, "VmBeforeDisplayed");
        IsBeforeBusy = false;
        BeforeImage = null;
    }

    private void ClearBeforeInterval()
    {
        SelectBeforePresentationSegment(null);
        _beforeIntervalRequest = null;
        _beforeIntervalSegment = null;
        SetBeforeIntervalFrames(Array.Empty<Y4mFrame>());
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

    private void ObserveBeforePhysical(RequestContext context, PreviewRequest physical)
    {
        // backendからの通知はUI外。物理計画だけを取消と同じ排他で受け渡す。
        lock (_taskLock)
            if (ReferenceEquals(Volatile.Read(ref _beforeExecutionContext), context)
                && context.BeforeCancellation?.IsCancellationRequested != true)
                _beforePhysicalWindow = new(context, physical);
    }

    private void CheckBeforeInterval(PreviewRequest afterRequest)
    {
        if (_beforeExecutionContext is { } context && !ContainsBeforeTarget(context, afterRequest)) CancelBefore();
    }

    private PreviewRequest BeforeRequest(PreviewRequest afterRequest, long generation) => afterRequest with
    {
        VppArgs = Array.Empty<string>(), CorrelationId = -generation, Continuous = false,
        SeekSecondsOverride = null, PreMarginSeconds = 2, PostMarginSeconds = BenchmarkPostMargin(),
    };

    private void QueueConcurrentBefore(PreviewRequest afterRequest, long generation)
    {
        var before = BeforeRequest(afterRequest, generation);
        CheckBeforeInterval(before);
        // cache hitはAfter反映後に表示すればよく、先に変換を始めてAfterを待たせない。
        if (TryGetBeforeInterval(before, out _)) return;
        QueueBefore(afterRequest, generation);
    }

    private void QueueBefore(PreviewRequest afterRequest, long generation, double? cachedTarget = null, PreviewRequest? recoveryRequest = null)
    {
        if (!IsCurrent(generation)) return;
        if (!_beforeEnabled || PerfTrace.Enabled && Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_DISABLE_BEFORE") == "1")
        {
            if (PerfTrace.Enabled) PerfTrace.Write("vm.queue.before.disabled", new { generation });
            BeforeUnavailableForDuration();
            return;
        }
        if (PerfTrace.Enabled) PerfTrace.Write("vm.queue.before", new { generation, cachedTarget });
        // 元画像は独立した有限窓。Afterの長寿命seekや拡大した前余白を引き継がない。
        var before = recoveryRequest is null ? BeforeRequest(afterRequest, generation)
            : recoveryRequest with { CorrelationId = -generation };
        if (cachedTarget is double target) before = RetargetInterval(before, target);
        if (_beforeRecoverySelection is { } recovery
            && (!SameBeforeInput(recovery, before) || Math.Abs(recovery.TargetSeconds - before.TargetSeconds) > 1e-8))
            _beforeRecoverySelection = null;
        if (_beforeRescueRetrySelection is { } rescueRetry
            && (!SameBeforeInput(rescueRetry, before) || Math.Abs(rescueRetry.TargetSeconds - before.TargetSeconds) > 1e-8))
            _beforeRescueRetrySelection = null;
        CheckBeforeInterval(before);
        _beforeSelectionRequest = before;
        IsBeforeBusy = true;
        if (TryGetBeforeInterval(before, out var frames))
        {
            if (PerfTrace.Enabled) PerfTrace.Write("vm.before.cache", new { generation });
            SetBeforeIntervalFrames(frames);
            var frame = SelectBeforeFrame(before, frames, out var frameOwnership);
            using var selectedRaw = frameOwnership;
            if (frame != null)
            {
                DisplayBeforeFrame(frame, generation, finishBusy: true);
                return;
            }
        }
        // 同一区間なら、待機や実行を保ったまま選択する目標だけを更新する。
        if (_beforeExecutionContext is { } existing && !_beforeFinished && ContainsBeforeTarget(existing, before)) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var context = new RequestContext(generation, before, _clock.GetTimestamp(), RequestKind.Before, cancellation,
            editEpoch: Volatile.Read(ref _editEpoch));
        var immediate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_taskLock) _beforeCancellation = cancellation;
        _beforeImmediate = immediate;
        if (IsShowingBefore || before.AllowConcurrentBackground) immediate.TrySetResult();
        _beforeFinished = false;
        var previous = Interlocked.Exchange(ref _beforeExecutionContext, context);
        // 完了済みBeforeを置き換えると、旧contextは後続のDisposeから辿れなくなる。
        // 交換を先に確定し、Post/worker自身の少数frame所有を残したまま区間の所有をlock外で返す。
        if (previous != null) ReleaseDetachedContext(previous);
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
                    if (TryGetBeforeInterval(selection, out var frames, forCompletion: context.Segment == null))
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
                BeforeUnavailableForDuration();
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
        => CompleteBeforeWithWindows(context, frames, null);

    private void CompleteBeforeWithWindows(RequestContext context, IReadOnlyList<Y4mFrame> frames,
        CompletedWindowSources? sources, bool retryLimitedRescue = false)
    {
        _beforeIntervalRequest = context.Request;
        _beforeIntervalSegment = context.Segment;
        using var selectedSource = sources?.ShareMatch(frames);
        if (selectedSource != null) SetBeforeFramesFromWindow(frames, selectedSource);
        else SetBeforeIntervalFrames(frames, context);
        _beforeFinished = true;
        IsBeforeBusy = false;
        // 実行開始時の目標ではなく、同じ区間で最後に移動した位置を選ぶ。
        var selection = RetargetInterval(context.Request, CurrentSeconds);
        IDisposable? frameOwnership = null;
        var frame = ContainsBeforeFrameTarget(_beforeIntervalSegment?.PhysicalRequest ?? context.Request,
            frames, selection.TargetSeconds, _beforeIntervalSegment) ? SelectBeforeFrame(selection, frames, out frameOwnership) : null;
        using var selectedRaw = frameOwnership;
        if (PerfTrace.Enabled) PerfTrace.Write("vm.before.selection", new { generation = context.Generation,
            target = CurrentSeconds, requestedTarget = context.Request.TargetSeconds,
            safeStart = _beforeIntervalSegment?.SafeStartSeconds, safeEnd = _beforeIntervalSegment?.SafeEndSeconds,
            selectedXts = frame?.Xts, missing = frame == null, busy = IsBeforeBusy });
        if (frame != null)
        {
            if (!CompleteEarlyBeforeFrame(context, frame))
                DisplayBeforeFrame(frame, Volatile.Read(ref _generation), finishBusy: true, revisionContext: context);
            return;
        }
        if (retryLimitedRescue)
        {
            // 少数救済で省いた画像は入力末尾の不足ではないため、窓の拡張とは別に最新目標を一度だけ取り直す。
            if (_beforeRescueRetrySelection is { } previous && SameBeforeInput(previous, selection)
                && Math.Abs(previous.TargetSeconds - selection.TargetSeconds) <= 1e-8)
            { BeforeUnavailableForDuration(); return; }
            _beforeRescueRetrySelection = selection;
            if (PerfTrace.Enabled) PerfTrace.Write("vm.before.completion_rescue_retry", new {
                generation = context.Generation, target = selection.TargetSeconds, context.Request.CorrelationId,
                rescuedFrameCount = frames.Count, rawCaptureFailed = sources?.RescueUnavailable == true,
                firstXts = frames.FirstOrDefault()?.Xts });
            CancelBefore(updatePresentation: false);
            ClearBeforeInterval();
            QueueBefore(selection, Volatile.Read(ref _generation));
            _beforeImmediate?.TrySetResult();
            return;
        }
        // 同じ入力・目標での回復は1回に制限し、着地が毎回遅い入力でも再帰し続けない。
        if (_beforeRecoverySelection is { } prior && SameBeforeInput(prior, selection)
            && Math.Abs(prior.TargetSeconds - selection.TargetSeconds) <= 1e-8)
        { BeforeUnavailableForDuration(); return; }
        var shortfall = PreviewWindowShortfall.FromSegment(selection, _beforeIntervalSegment);
        if (shortfall == null || !PreviewWindowRecovery.TryExpand(selection, shortfall, 0, out var recoveryRequest))
        { BeforeUnavailableForDuration(); return; }
        _beforeRecoverySelection = selection;
        CancelBefore();
        QueueBefore(selection, Volatile.Read(ref _generation), recoveryRequest: recoveryRequest);
        _beforeImmediate?.TrySetResult();
    }

    private bool TryGetBeforeInterval(PreviewRequest request, out IReadOnlyList<Y4mFrame> frames, bool forCompletion = false)
    {
        ValidateNavigationState();
        // 通常検索は候補のmetadataだけを読み、表示する画素は選択後のTry取得で保護する。
        using var completionRead = forCompletion && request.Capabilities.TimestampPassthrough
            ? ReadCacheAt(request, request.TargetSeconds, "VmId0BeforeWindow") : null;
        var matchedSegment = completionRead?.Segment;
        if (!forCompletion && request.Capabilities.TimestampPassthrough
            && _cache.TryGetAt(request, request.TargetSeconds, out var metadata)) matchedSegment = metadata;
        if (matchedSegment is { } segment
            && ContainsBeforeFrameTarget(segment.PhysicalRequest, segment.Frames, request.TargetSeconds, segment))
        {
            _beforeIntervalRequest = segment.PhysicalRequest;
            _beforeIntervalSegment = segment;
            // Scheduleの完了枝は従来のnull時計を保ち、Readの生存中に最終窓へ所有を移す。
            if (forCompletion) SetBeforeIntervalWindow(completionRead!.TakeWindow());
            else SetBeforeIntervalFrames(segment.Frames);
            frames = _beforeIntervalFrames;
            return true;
        }
        using var exact = ReadCacheExact(request, forCompletion ? "VmId0BeforeWindow" : "VmBeforeExact", out var physical);
        if (exact != null && ContainsFrameTarget(physical, exact.Frames, request.TargetSeconds, ClockFor(physical, exact.Frames)))
        {
            _beforeIntervalRequest = physical;
            _beforeIntervalSegment = null;
            if (forCompletion) SetBeforeIntervalWindow(exact.TakeWindow());
            else SetBeforeIntervalFrames(exact.Frames);
            frames = _beforeIntervalFrames;
            return true;
        }
        // フィルタなしなら、長寿命Afterの同じフレームを共有し、元画像用のプロセスを起動しない。
        if (_intervalRequest is { VppArgs.Count: 0 } after && SameBeforeInput(after, request)
            && ContainsBeforeFrameTarget(after, _intervalFrames, request.TargetSeconds, _intervalSegment))
        {
            _beforeIntervalRequest = after;
            _beforeIntervalSegment = _intervalSegment;
            if (forCompletion && _intervalSegment is { SegmentId: > 0 } cachedAfter)
            {
                if (!TryAdoptBeforeCompletion(cachedAfter.SegmentId, after, request.TargetSeconds))
                { frames = Array.Empty<Y4mFrame>(); return false; }
            }
            else SetBeforeIntervalFrames(_intervalFrames, shared: _intervalRaw);
            frames = _beforeIntervalFrames;
            return true;
        }
        // 最新目標で計画窓を上書きせず、取得した区間の実時刻と安全範囲だけで再利用を判断する。
        if (_beforeIntervalRequest is { } previous && SameBeforeInput(previous, request)
            && ContainsBeforeFrameTarget(previous, _beforeIntervalFrames, request.TargetSeconds, _beforeIntervalSegment)
            && (_beforeIntervalSegment is not { } held
                || PreviewSafeRange.Contains(request.TargetSeconds, held.SafeStartSeconds, held.SafeEndSeconds)))
        {
            if (forCompletion && _beforeIntervalSegment is { SegmentId: > 0 } cachedBefore
                && !TryAdoptBeforeCompletion(cachedBefore.SegmentId, previous, request.TargetSeconds))
            { frames = Array.Empty<Y4mFrame>(); return false; }
            frames = _beforeIntervalFrames;
            return true;
        }
        frames = Array.Empty<Y4mFrame>();
        return false;
    }

    private bool TryAdoptBeforeCompletion(long id, PreviewRequest physical, double target)
    {
        // 正IDのmetadataだけでは全列を所有できない。gate内で取得した最終窓をそのまま移す。
        // 最新prefixで包含を再確認し、消失時は既存missへ戻す。追加の一時bridgeは作らない。
        using var current = ReadCacheSegment(id, "VmId0BeforeWindow");
        if (current?.Segment is not { } latest
            || !ContainsBeforeFrameTarget(physical, current.Frames, target, latest)) return false;
        SetBeforeIntervalWindow(current.TakeWindow());
        return true;
    }

    private bool ContainsBeforeFrameTarget(PreviewRequest request, IReadOnlyList<Y4mFrame> frames, double target, PreviewSegmentSnapshot? segment)
    {
        bool inside = segment == null || PreviewSafeRange.Contains(target, segment.SafeStartSeconds, segment.SafeEndSeconds);
        if (inside && ContainsFrameTarget(request, frames, target, segment?.Clock)
            && (segment == null || FrameSelector.Select(segment, target, request.Fps) >= 0)) return true;
        // 自然終端が確定した既知Durationへの移動だけは、Afterと同じ最終画像を共有する。
        // 通常の取得窓の末尾を、この終端例外で延長しない。
        if (segment?.ReachedInputEnd != true || frames.Count == 0 || request.InputDurationSeconds is not double end
            || Math.Abs(target - end) > 1e-8) return false;
        var last = frames[^1];
        double lastSeconds = segment.Clock.ToInputSeconds(last, frames.Count - 1, request.Fps);
        return PreviewSafeRange.Contains(lastSeconds, segment.SafeStartSeconds, segment.SafeEndSeconds)
            && lastSeconds <= end + 1e-8 && lastSeconds + FrameSelector.Duration(last, request.Fps) >= end - 1e-8;
    }

    private bool SameBeforeInput(PreviewRequest a, PreviewRequest b) => a.EncoderPath == b.EncoderPath
        && a.InputFile == b.InputFile && a.Fps == b.Fps
        && (a.InputArgs.SequenceEqual(b.InputArgs) || IsKnownLegacyPhysicalSource(a, b)
            && SameArgumentsWithoutPassthrough(a.InputArgs, b.InputArgs))
        && a.Capabilities.OpenClTaskThreads == b.Capabilities.OpenClTaskThreads
        && (a.Capabilities.TimestampPassthrough == b.Capabilities.TimestampPassthrough || IsKnownLegacyPhysicalSource(a, b))
        && a.TimestampOriginSeconds == b.TimestampOriginSeconds;

    // 実受信済みなら安全域、未出力ならprobe後の物理窓だけを再利用する。
    private bool ContainsBeforeTarget(RequestContext context, PreviewRequest second)
    {
        if (!SameBeforeInput(context.Request, second)) return false;
        if (context.Segment is { } segment)
            return ContainsBeforeFrameTarget(segment.PhysicalRequest, segment.Frames, second.TargetSeconds, segment);
        PreviewRequest? physical;
        lock (_taskLock)
            physical = _beforePhysicalWindow is { } window && ReferenceEquals(window.Context, context) ? window.Request : null;
        // probeの結論が未着なら別目標への窓拡張を推測しない。
        if (physical == null) return Math.Abs(context.Request.TargetSeconds - second.TargetSeconds) <= 1e-8;
        var plan = CommandBuilder.Plan(physical);
        double start = plan.SeekSeconds ?? 0;
        double end = start + (plan.FrameCount - 1) / physical.Fps;
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

    private Y4mFrame? SelectBeforeFrame(PreviewRequest request, IReadOnlyList<Y4mFrame> frames, out IDisposable? ownership)
    {
        ownership = null;
        if (frames.Count == 0) return null;
        if (_beforeIntervalSegment is { Clock.TrustedAbsolute: true } segment)
        {
            var navigation = NavigationFrames(request, segment);
            int selected = FrameSelector.Select(navigation.Select(reference => new FrameTiming(reference.InputSeconds,
                FrameSelector.Duration(reference.Frame, reference.Segment.PhysicalRequest.Fps))).ToArray(), request.TargetSeconds, request.Fps);
            if (selected >= 0)
            {
                var frame = navigation[selected].Frame;
                // metadata選択後は取得成功まで区間を変えず、using終了後も選択1枚を保つ。
                if (!TryRetainSelection(frame, "VmBeforeSelection", out ownership)) return null;
                _beforeIntervalSegment = navigation[selected].Segment;
                return frame;
            }
            return null;
        }
        var clock = _beforeIntervalSegment?.Clock ?? ClockFor(request, frames);
        double target = clock.ToSelectionSeconds(request.TargetSeconds, frames, request.Fps);
        int index = FrameSelector.Select(frames, target, request.Fps);
        var last = frames.LastOrDefault(frame => frame.Xts.HasValue) ?? frames[^1];
        double lastTime = last.Xts ?? (frames.Count - 1) / FrameSelector.FrameRate(last, request.Fps);
        if (lastTime + FrameSelector.Duration(last, request.Fps) / 2 < target) index = frames.Count - 1;
        if (index < 0) return null;
        if (!TryRetainSelection(frames[index], "VmBeforeSelection", out ownership)) return null;
        return frames[index];
    }

    private void DisplayBeforeFrame(Y4mFrame frame, long generation, bool finishBusy = false, RequestContext? revisionContext = null,
        PreviewSegmentSnapshot? sourceSegment = null, bool earlyDisplay = false)
    {
        // 変換中に選択済みの区間が回収されると再取得になるため、表示準備を始めた時点から守る。
        SelectBeforePresentationSegment((sourceSegment ?? _beforeIntervalSegment)?.SegmentId);
        revisionContext ??= Volatile.Read(ref _context);
        long revision = revisionContext == null ? 0 : Volatile.Read(ref revisionContext.DisplayRevision);
        long imageGeneration = Interlocked.Increment(ref _beforeImageGeneration);
        var request = _beforeSelectionRequest;
        var source = sourceSegment ?? _beforeIntervalSegment;
        var clock = source?.Clock ?? (request == null ? null : ClockFor(request, new[] { frame }));
        var work = new ImageWork(frame, generation, Volatile.Read(ref _editEpoch), Before: true,
            BeforeVersion: imageGeneration, FinishBeforeBusy: finishBusy,
            DisplaySeconds: PerfTrace.Enabled && frame.Xts.HasValue && request != null && clock != null
                ? clock.ToInputSeconds(frame, 0, request.Fps) : null,
            SegmentId: source?.SegmentId, TrustedAbsolute: clock?.TrustedAbsolute ?? false,
            RevisionContext: revisionContext, DisplayRevision: revision);
        EnqueueImage(work);
        if (earlyDisplay && PerfTrace.Enabled) PerfTrace.Write("vm.before.early.queued", new {
            generation, correlationId = revisionContext?.Request.CorrelationId, work.TraceId,
            frame.FrameId, frame.Xts, frame.Xdur, target = CurrentSeconds,
            reason = "単調な等間隔の受信列で目標を読み越した" });
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

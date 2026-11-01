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
            if (!SetProperty(ref _isShowingBefore, value && CompareMode == PreviewCompareMode.Single) || !_isShowingBefore) return;
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
        set { var previous = _beforeImage; if (SetProperty(ref _beforeImage, value)) previous?.Dispose(); }
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
        if (_beforeExecutionContext is { } context && !SameBeforeInterval(context.Request, afterRequest)) CancelBefore();
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
        var before = afterRequest with { VppArgs = Array.Empty<string>(), CorrelationId = -generation };
        if (cachedTarget is double target) before = RetargetInterval(before, target);
        CheckBeforeInterval(before);
        _beforeSelectionRequest = before;
        IsBeforeBusy = true;
        if (TryGetBeforeInterval(before, out var frames))
        {
            if (PerfTrace.Enabled) PerfTrace.Write("vm.before.cache", new { generation });
            _beforeIntervalRequest = before;
            _beforeIntervalFrames = frames;
            var frame = SelectBeforeFrame(before, frames);
            if (frame == null) { IsBeforeBusy = false; return; }
            DisplayBeforeFrame(frame, generation, finishBusy: true);
            return;
        }
        // 同一区間なら、待機や実行を保ったまま選択する目標だけを更新する。
        if (_beforeExecutionContext is { } existing && !_beforeFinished && SameBeforeInterval(existing.Request, before)) return;
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
                    _controller.Submit(context.Request, bypassDebounce: true, background: true, cancellationToken: token);
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
        _beforeIntervalFrames = frames;
        _beforeFinished = true;
        IsBeforeBusy = false;
        // 実行開始時の目標ではなく、同じ区間で最後に移動した位置を選ぶ。
        var selection = RetargetInterval(context.Request, CurrentSeconds);
        if (!SameBeforeInterval(context.Request, selection)) return;
        var frame = SelectBeforeFrame(selection, frames);
        if (frame != null) DisplayBeforeFrame(frame, Volatile.Read(ref _generation), finishBusy: true);
    }

    private bool TryGetBeforeInterval(PreviewRequest request, out IReadOnlyList<Y4mFrame> frames)
    {
        if (_cache.TryGet(request, out frames)) return true;
        // フィルタなしなら、LRUの容量外でも保持中の後画像区間をそのまま元画像に使える。
        if (_intervalRequest is { VppArgs.Count: 0 } after && SameBeforeInterval(after, request))
        { frames = _intervalFrames; return true; }
        if (_beforeIntervalRequest is { } previous && SameBeforeInterval(previous, request))
        { frames = _beforeIntervalFrames; return true; }
        frames = Array.Empty<Y4mFrame>();
        return false;
    }

    private static bool SameBeforeInterval(PreviewRequest a, PreviewRequest b)
    {
        var first = CommandBuilder.Plan(a);
        var second = CommandBuilder.Plan(b);
        return a.EncoderPath == b.EncoderPath && a.InputFile == b.InputFile && a.Fps == b.Fps
            && a.InputArgs.SequenceEqual(b.InputArgs) && first.SeekSeconds == second.SeekSeconds && first.FrameCount == second.FrameCount;
    }

    private static Y4mFrame? SelectBeforeFrame(PreviewRequest request, IReadOnlyList<Y4mFrame> frames)
    {
        if (frames.Count == 0) return null;
        double target = CommandBuilder.Plan(request).ActualPreMargin;
        int index = FrameSelector.Select(frames, target, request.Fps);
        var last = frames.LastOrDefault(frame => frame.Xts.HasValue) ?? frames[^1];
        double lastTime = last.Xts ?? (frames.Count - 1) / FrameSelector.FrameRate(last, request.Fps);
        if (lastTime + FrameSelector.Duration(last, request.Fps) / 2 < target) index = frames.Count - 1;
        return index < 0 ? null : frames[index];
    }

    private void DisplayBeforeFrame(Y4mFrame frame, long generation, bool finishBusy = false)
    {
        long imageGeneration = Interlocked.Increment(ref _beforeImageGeneration);
        EnqueueImage(new ImageWork(frame, generation, Volatile.Read(ref _editEpoch), Before: true,
            BeforeVersion: imageGeneration, FinishBeforeBusy: finishBusy));
    }

    private void RecalculateDisplaySizes()
    {
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

    private static WriteableBitmap CreateBitmap(Y4mFrame frame, byte[] data)
    {
        if (PerfTrace.Enabled) PerfTrace.Write("vm.bitmap.start", new { frame.Xts });
        var header = frame.Header;
        var bitmap = new WriteableBitmap(new PixelSize(header.Width, header.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        try
        {
            using var locked = bitmap.Lock();
            for (int y = 0; y < header.Height; y++) Marshal.Copy(data, y * header.Width * 4, locked.Address + y * locked.RowBytes, header.Width * 4);
            if (PerfTrace.Enabled) PerfTrace.Write("vm.bitmap.end", new { frame.Xts });
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}

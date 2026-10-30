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
        set => SetProperty(ref _isShowingBefore, value && CompareMode == PreviewCompareMode.Single);
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
        // キャンセルと破棄を同じ排他区間に置き、古い要求の後処理との競合を防ぐ。
        lock (_taskLock)
        {
            _beforeCancellation?.Cancel();
            _beforeCancellation = null;
            _pendingBeforeImage = null;
        }
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

    private void QueueBefore(PreviewRequest afterRequest, long generation, double? cachedTarget = null)
    {
        if (!IsCurrent(generation)) return;
        var before = afterRequest with { VppArgs = Array.Empty<string>(), CorrelationId = -generation };
        if (cachedTarget is double target)
        {
            // コマ送りでは、元の区間のseekと要求枚数を保って選択対象の時刻だけを動かす。
            // 単にTargetSecondsだけを書き換えるとseekや枚数も変わり、キャッシュに当たらなくなる。
            var interval = CommandBuilder.Plan(afterRequest);
            double margin = target - (interval.SeekSeconds ?? 0);
            before = before with
            {
                TargetSeconds = target, PreMarginSeconds = Math.Max(0, margin),
                PostMarginSeconds = Math.Max(0, (interval.FrameCount - 1) / afterRequest.Fps - margin),
            };
        }
        IsBeforeBusy = true;
        if (TryGetBeforeInterval(before, out var frames))
        {
            _beforeIntervalRequest = before;
            _beforeIntervalFrames = frames;
            var frame = SelectBeforeFrame(before, frames);
            if (frame == null) { IsBeforeBusy = false; return; }
            DisplayBeforeFrame(frame, generation, finishBusy: true);
            return;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var context = new RequestContext(generation, before, _clock.GetTimestamp(), RequestKind.Before, cancellation);
        lock (_taskLock) _beforeCancellation = cancellation;
        Volatile.Write(ref _beforeExecutionContext, context);
        Volatile.Write(ref _context, context);
        // 後画像が確定してから追加取得するので、編集向けのdebounceは重ねない。
        _controller.Submit(before, bypassDebounce: true);
        Track(FinishBeforeAsync(context));
    }

    private async Task FinishBeforeAsync(RequestContext context)
    {
        try { await _controller.WaitForIdleAsync().ConfigureAwait(false); }
        finally
        {
            lock (_taskLock)
            {
                if (ReferenceEquals(_beforeCancellation, context.BeforeCancellation)) _beforeCancellation = null;
                context.BeforeCancellation?.Dispose();
            }
            if (ReferenceEquals(Volatile.Read(ref _beforeExecutionContext), context)) Volatile.Write(ref _beforeExecutionContext, null);
        }
    }

    private RequestContext? ExecutionContextFor(PreviewRequest request)
    {
        var context = ContextFor(request);
        if (context != null) return context;
        // 新しい後画像の準備中でも、実行待ちだった旧Beforeへ中断済みトークンを渡す。
        context = Volatile.Read(ref _beforeExecutionContext);
        return context != null && SameRequest(context.Request, request) ? context : null;
    }

    private bool TryGetBeforeInterval(PreviewRequest request, out IReadOnlyList<Y4mFrame> frames)
    {
        if (_cache.TryGet(request, out frames)) return true;
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
        var header = frame.Header;
        var bitmap = new WriteableBitmap(new PixelSize(header.Width, header.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        try
        {
            using var locked = bitmap.Lock();
            for (int y = 0; y < header.Height; y++) Marshal.Copy(data, y * header.Width * 4, locked.Address + y * locked.RowBytes, header.Width * 4);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}

using System.Collections.Concurrent;
using Avalonia.Threading;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ConcurrentDictionary<long, RequestContext> _afterRequests = new();
    private long _editEpoch, _lastQueuedAfter, _lastDisplayedAfter, _lastCompletedAfter, _lastFailedAfter;
    private long _lastQueuedRevision, _lastDisplayedRevision;
    private bool _latestIntentNavigation;
    private enum NavigationOperation { Seek, Page, Frame }
    private long _concurrentComparisonGeneration;
    private string _completedAfterStatus = AppMessages.Done;
    private ImageWork? _pendingAfterImage, _pendingBeforeImage;
    private bool _conversionActive;
    private NeighborIntent? _pendingNeighbor;

    private long BeginIntent(bool navigation)
    {
        long generation = Interlocked.Increment(ref _generation);
        ReleaseObsoleteRequestSnapshots(generation);
        _latestIntentNavigation = navigation;
        _pendingNeighbor = null;
        _reversePrefetchWait = null;
        Volatile.Write(ref _context, null);
        if (!navigation)
        {
            CancelBefore();
            Interlocked.Increment(ref _editEpoch);
            _afterRequests.Clear();
            lock (_taskLock) DropImage(ref _pendingAfterImage);
        }
        return generation;
    }

    private void RegisterContext(RequestContext context)
    {
        _afterRequests[context.Generation] = context;
        Volatile.Write(ref _context, context);
    }

    private Task QueuePreview(bool bypassCache = false, bool navigation = false, int neighborDirection = 0, bool requirePhysicalRefetch = false, bool bypassDebounce = false, bool allowConcurrentBefore = false, NavigationOperation operation = NavigationOperation.Seek)
    {
        if (!_initialized || _suspendChanges || _disposed) return Task.CompletedTask;
        CancelInputEdit();
        // シークは向きにかかわらず目標を早く確定する。逆の広い窓はコマ移動と接続範囲内の秒移動だけ。
        if (neighborDirection != 0) operation = NavigationOperation.Frame;
        bool reverse = navigation && (operation == NavigationOperation.Frame && neighborDirection < 0
            || operation == NavigationOperation.Page && CurrentSeconds < _lastIntentTarget - 1e-8
                && IsConnectedNavigationTarget(CurrentSeconds, _lastIntentTarget));
        _lastIntentTarget = CurrentSeconds;
        _latestIntentReverse = reverse;
        long generation = BeginIntent(navigation);
        if (!navigation || allowConcurrentBefore) _concurrentComparisonGeneration = generation;
        if (PerfTrace.Enabled) PerfTrace.Write("vm.queue.after", new { generation, navigation, neighborDirection, bypassCache, target = CurrentSeconds });
        long editEpoch = Volatile.Read(ref _editEpoch);
        if (neighborDirection != 0) _pendingNeighbor = new NeighborIntent(generation, neighborDirection);
        // 入力検査中の移動連打では検査そのものを止めず、取得後の最新位置だけを使う。
        if (navigation && _inputInfo == null && _preparationCancellation != null)
        { IsBusy = true; StatusText = AppMessages.Processing; return _preparation; }
        CancellationTokenSource cancellation;
        lock (_taskLock)
        {
            _preparationCancellation?.Cancel();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _preparationCancellation = cancellation;
        }
        string encoder = EncoderPath, input = InputPath;
        if (!string.IsNullOrWhiteSpace(encoder) && _metadataEncoder != encoder)
        {
            _metadataEncoder = encoder;
            _metadataTask = LoadMetadataAsync(encoder, _lifetime.Token);
            Track(_metadataTask);
        }
        if (string.IsNullOrWhiteSpace(encoder) || string.IsNullOrWhiteSpace(input))
        {
            _pendingNeighbor = null;
            IsBusy = false;
            ErrorText = null;
            StatusText = AppMessages.SelectEncoderAndInput;
            _preparation = Task.CompletedTask;
            lock (_taskLock)
            {
                if (ReferenceEquals(_preparationCancellation, cancellation)) _preparationCancellation = null;
                cancellation.Dispose();
            }
            return _preparation;
        }
        IsBusy = true;
        ErrorText = null;
        StatusText = navigation ? AppMessages.Processing : AppMessages.ProbingInput;
        string[] inputArgs = OptionTextParser.ParseText(InputArgsText);
        string[] vpp = OptionTextParser.Parse(FilterLines.Select(line => new OptionLine(line.Text, line.IsEnabled)));
        double seconds = CurrentSeconds;
        _preparation = PrepareAsync();
        Track(_preparation);
        return _preparation;

        async Task PrepareAsync()
        {
            try
            {
                await _metadataTask.WaitAsync(cancellation.Token).ConfigureAwait(false);
                PreviewCapabilities capabilities = _capabilities;
                InputInfo info = _inputInfo ?? await (_borrowInputProbe
                    ? _controller.ProbeInputAsync(_probe, encoder, input, inputArgs, capabilities, cancellation.Token)
                    : _probe.ProbeAsync(encoder, input, inputArgs, cancellation.Token, capabilities)).ConfigureAwait(false);
                await PostAsync(() =>
                {
                    if (_disposed || editEpoch != Volatile.Read(ref _editEpoch) || cancellation.IsCancellationRequested) return;
                    long actualGeneration = generation;
                    bool actualNavigation = navigation;
                    bool actualReverse = reverse;
                    double target = seconds;
                    if (!IsCurrent(generation))
                    {
                        if (!_latestIntentNavigation) return;
                        actualGeneration = Volatile.Read(ref _generation);
                        actualNavigation = true;
                        actualReverse = _latestIntentReverse;
                        target = CurrentSeconds;
                    }
                    ApplyInputInfo(info);
                    target = HasDuration ? Math.Min(target, DurationSeconds) : target;
                    // 古い移動の準備完了で目標時刻を過去へ戻さない。
                    if (actualGeneration == Volatile.Read(ref _generation)) SetTimeOnly(target);
                    var request = new PreviewRequest
                    {
                        EncoderPath = encoder, InputFile = input, InputArgs = inputArgs, VppArgs = vpp,
                        AllowConcurrentBackground = _concurrentComparisonGeneration == actualGeneration,
                        Fps = info.Fps, TargetSeconds = target, CorrelationId = actualGeneration,
                        InputFormatHint = new Y4mHeader { Width = info.Width, Height = info.Height,
                            FpsNum = info.FpsNum, FpsDen = info.FpsDen, Chroma = info.Chroma,
                            BitDepth = info.BitDepth, ColorSpaceTag = info.ColorSpaceTag },
                        // 計測時だけ後マージンを切り替え、通常の要求値は維持する。
                        PostMarginSeconds = capabilities.TimestampPassthrough && info.TimestampOriginSeconds.HasValue
                            ? actualReverse ? 0.5 : BenchmarkPostMargin() + 0.5 : BenchmarkPostMargin(),
                        // 絶対時計の逆方向は、暖機2秒を除いても手前3秒を表示できる物理窓にする。
                        // 順方向の論理2秒はCoreで物理3秒へ広げ、legacyの逆方向3秒は維持する。
                        PreMarginSeconds = actualReverse
                            ? capabilities.TimestampPassthrough && info.TimestampOriginSeconds.HasValue ? 5 : 3 : 2,
                        Capabilities = capabilities, TimestampOriginSeconds = info.TimestampOriginSeconds,
                        TimestampOriginTrusted = info.TimestampOriginTrusted,
                        InputDurationSeconds = info.Duration?.TotalSeconds ?? (HasDuration ? DurationSeconds : null),
                        Continuous = capabilities.TimestampPassthrough && info.TimestampOriginSeconds.HasValue && !actualReverse,
                    };
                    if (_controller.IsLegacySource(request))
                        request = request with { Continuous = false, PreMarginSeconds = actualReverse ? 3 : request.PreMarginSeconds };
                    bool squarePixel = info.SarNum <= 0 || info.SarDen <= 0 || info.SarNum == info.SarDen;
                    string sar = squarePixel ? "" : $" (SAR {info.SarNum}:{info.SarDen})";
                    InputInfoText = $"{info.Width}x{info.Height}{sar}  {info.Fps:0.###}fps";
                    var neighbor = _pendingNeighbor?.Generation == actualGeneration ? _pendingNeighbor : null;
                    var context = new RequestContext(actualGeneration, request, _clock.GetTimestamp(),
                        editEpoch: editEpoch, neighborDirection: neighbor?.Direction ?? 0);
                    CheckBeforeInterval(request);
                    RegisterContext(context);
                    StatusText = AppMessages.Processing;
                    // 隣不足では旧cache hitを避ける。正方向だけ、生存中の同じ物理streamへ合流を明示する。
                    bool reuseNeighbor = requirePhysicalRefetch && !bypassCache && actualNavigation && !actualReverse
                        && neighbor is { Direction: > 0 } && request.Continuous;
                    ReleaseIntervalSnapshotsForSubmission();
                    _controller.Submit(request, bypassCache || requirePhysicalRefetch, navigation: actualNavigation,
                        reuseActiveStream: reuseNeighbor, bypassDebounce: bypassDebounce);
                    // 無フィルタではAfterの確定frameがそのままBeforeなので、別の有限取得を競合させない。
                    if (request.AllowConcurrentBackground && request.VppArgs.Count > 0) QueueConcurrentBefore(request, actualGeneration);
                    Track(ReleaseContextAsync(context));
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                string diagnostics = exception switch
                {
                    InputProbeException probe => probe.StandardError, InputProbeTimeoutException timeout => timeout.StandardError,
                    PreviewBackendException backend => backend.StandardError, _ => "",
                };
                await PostAsync(() =>
                {
                    if (_disposed || editEpoch != Volatile.Read(ref _editEpoch) || cancellation.IsCancellationRequested) return;
                    if (!IsCurrent(generation) && !_latestIntentNavigation) return;
                    _pendingNeighbor = null;
                    _lastFailedAfter = Volatile.Read(ref _generation);
                    ShowError(exception.Message, diagnostics);
                }).ConfigureAwait(false);
            }
            finally
            {
                lock (_taskLock)
                {
                    if (ReferenceEquals(_preparationCancellation, cancellation)) _preparationCancellation = null;
                    cancellation.Dispose();
                }
            }
        }
    }

    private async Task ReleaseContextAsync(RequestContext context)
    {
        try
        {
            if (context.Request.Continuous) await _controller.WaitForCurrentDemandAsync().ConfigureAwait(false);
            else await _controller.WaitForIdleAsync().ConfigureAwait(false);
        }
        finally
        {
            // Coreが実行を始める直前の要求も、タスク完了までは通知の対応表から外さない。
            if (!context.Request.Continuous) _afterRequests.TryRemove(context.Generation, out _);
        }
    }

    private void UpdateNavigationStatus()
    {
        if (_disposed) return;
        long latest = Volatile.Read(ref _generation);
        if (_lastFailedAfter == latest) return;
        IsBusy = _lastDisplayedAfter < latest || _lastCompletedAfter < latest;
        StatusText = IsBusy ? AppMessages.Processing : _completedAfterStatus;
    }

    private static PreviewRequest RetargetInterval(PreviewRequest request, double target)
    {
        var interval = CommandBuilder.Plan(request);
        double margin = target - (interval.SeekSeconds ?? 0);
        return request with
        {
            TargetSeconds = target, PreMarginSeconds = Math.Max(0, margin),
            PostMarginSeconds = Math.Max(0, (interval.FrameCount - 1) / request.Fps - margin),
        };
    }

    private long _nextImageTraceId;

    private void EnqueueImage(ImageWork image)
    {
        // 計測時だけ識別子を付け、同じXtsのBefore/Afterや間引かれた仕事を区別する。
        if (PerfTrace.Enabled) image.TraceId = Interlocked.Increment(ref _nextImageTraceId);
        TaskCompletionSource? completion = null;
        lock (_taskLock)
        {
            if (_disposed)
            {
                image.Dispose();
                return;
            }
            ImageWork? pending = image.DisplayOnly ? (image.Before ? _pendingBeforeDisplayRefresh : _pendingAfterDisplayRefresh)
                : image.Before ? _pendingBeforeImage : _pendingAfterImage;
            long? replacedTraceId = PerfTrace.Enabled ? pending?.TraceId : null;
            if (image.DisplayOnly)
            {
                if (image.Before) _pendingBeforeDisplayRefresh = image; else _pendingAfterDisplayRefresh = image;
            }
            else if (image.Before) _pendingBeforeImage = image;
            else if (_pendingAfterImage == null || image.Generation > _pendingAfterImage.Generation
                || image.Generation == _pendingAfterImage.Generation && image.DisplayRevision >= _pendingAfterImage.DisplayRevision)
                _pendingAfterImage = image;
            if (PerfTrace.Enabled) PerfTrace.Write("vm.image.enqueue", new { image.TraceId, image.Generation,
                image.Before, image.Frame.Xts, replacedTraceId,
                accepted = ReferenceEquals(image.DisplayOnly ? (image.Before ? _pendingBeforeDisplayRefresh : _pendingAfterDisplayRefresh)
                    : image.Before ? _pendingBeforeImage : _pendingAfterImage, image) });
            bool accepted = ReferenceEquals(image.DisplayOnly ? (image.Before ? _pendingBeforeDisplayRefresh : _pendingAfterDisplayRefresh)
                : image.Before ? _pendingBeforeImage : _pendingAfterImage, image);
            if (accepted) pending?.Dispose();
            else image.Dispose();
            if (!_conversionActive)
            {
                _conversionActive = true;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // Disposeが変換開始と競合しても、変換の回収を取りこぼさない。
                _tasks.Add(completion.Task);
            }
        }
        if (completion != null) _ = RunImageLoopAsync(completion);
    }

    private async Task RunImageLoopAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                ImageWork? image;
                lock (_taskLock)
                {
                    // 設定変更の表示2枚だけを先に更新し、連続AfterでBeforeの再描画を待たせ続けない。
                    image = _pendingAfterDisplayRefresh ?? _pendingBeforeDisplayRefresh ?? _pendingAfterImage ?? _pendingBeforeImage;
                    if (image == null) { _conversionActive = false; break; }
                    if (image.DisplayOnly)
                    { if (image.Before) _pendingBeforeDisplayRefresh = null; else _pendingAfterDisplayRefresh = null; }
                    else if (image.Before) _pendingBeforeImage = null;
                    else _pendingAfterImage = null;
                }
                try
                {
                    if (PerfTrace.Enabled) PerfTrace.Write("vm.convert.start", new { image.TraceId, image.Generation, image.Before, image.Frame.Xts });
                    HdrDisplaySettings settings;
                    long displaySettingsRevision;
                    lock (_taskLock) { settings = _hdrDisplaySettings; displaySettingsRevision = _hdrDisplaySettingsRevision; }
                    await WithConvertedBgraAsync(image.Frame, settings, _lifetime.Token, async data =>
                    {
                        if (PerfTrace.Enabled) PerfTrace.Write("vm.convert.end", new { image.TraceId, image.Generation, image.Before, image.Frame.Xts, bytes = data.Length });
                        await PostAsync(() =>
                        {
                            if (displaySettingsRevision != Volatile.Read(ref _hdrDisplaySettingsRevision) && YuvToBgra.GetColorInfo(image.Frame.Header).IsHdr)
                                RequeueForDisplaySettings(image);
                            else ApplyImage(image, data);
                        }).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    await PostAsync(() =>
                    {
                        if (_disposed || image.EditEpoch != Volatile.Read(ref _editEpoch)) return;
                        if (image.DisplayOnly)
                        {
                            if (PerfTrace.Enabled) PerfTrace.Write("vm.display_refresh.failed", new { exception.Message });
                            return;
                        }
                        if (image.RevisionContext is { } context && image.DisplayRevision != Volatile.Read(ref context.DisplayRevision))
                        {
                            if (image.Before && IsCurrent(image.Generation) && image.BeforeVersion == _beforeImageGeneration) IsBeforeBusy = false;
                            return;
                        }
                        if (image.Before) { if (IsCurrent(image.Generation) && image.BeforeVersion == _beforeImageGeneration) { IsBeforeBusy = false; BeforeImage = null; BeforeUnavailableForDuration(); } }
                        else if (image.Generation == Volatile.Read(ref _generation))
                        { _lastFailedAfter = image.Generation; ShowError(AppMessages.ImageDisplayFailed, exception.Message); }
                    }).ConfigureAwait(false);
                }
                finally
                {
                    image.Dispose();
                }
            }
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            lock (_taskLock) _conversionActive = false;
            completion.TrySetException(exception);
        }
        finally { lock (_taskLock) _tasks.Remove(completion.Task); }
    }

    private void ApplyImage(ImageWork image, byte[] data)
    {
        if (_disposed || image.EditEpoch != Volatile.Read(ref _editEpoch)) return;
        if (image.DisplayOnly)
        {
            if (!ReferenceEquals(image.Frame, image.Before ? _beforeDisplayedFrame : _displayedFrame)) return;
            if (image.Before && BeforeImage != null) BeforeImage = CreateBitmap(image.Frame, data, image.TraceId);
            else if (!image.Before && PreviewImage != null) PreviewImage = CreateBitmap(image.Frame, data, image.TraceId);
            return;
        }
        if (image.RevisionContext is { } context && image.DisplayRevision != Volatile.Read(ref context.DisplayRevision))
        {
            if (image.Before && IsCurrent(image.Generation) && image.BeforeVersion == _beforeImageGeneration) IsBeforeBusy = false;
            return;
        }
        if (image.TrustedAbsolute && image.SegmentId is > 0 && !CacheHasSegment(image.SegmentId.Value))
        {
            // 変換待ちの間に画素照合で破棄された区間を、UIから再び採用しない。
            ValidateNavigationState();
            if (image.Generation == Volatile.Read(ref _generation))
            {
                if (image.Before)
                {
                    if (_context is { Kind: RequestKind.After } after) QueueBefore(after.Request, after.Generation);
                }
                else _ = QueuePreview(navigation: true);
            }
            return;
        }
        if (image.Before)
        {
            if (!IsCurrent(image.Generation) || image.BeforeVersion != _beforeImageGeneration) return;
            var bitmap = CreateBitmap(image.Frame, data, image.TraceId);
            SetRawFrame(ref _beforeDisplayedFrame, ref _beforeDisplayedRaw, image.Frame, "VmBeforeDisplayed");
            SelectBeforePresentationSegment(image.SegmentId);
            RecalculateDisplaySizes();
            BeforeImage = bitmap;
            if (PerfTrace.Enabled) PerfTrace.Write("vm.apply", new { image.TraceId, image.Generation, image.DisplayRevision, image.Before, image.Frame.Xts, target = CurrentSeconds, displaySeconds = image.DisplaySeconds, segmentId = image.SegmentId, trustedAbsolute = image.TrustedAbsolute });
            QueueDurationAfterComparison(image.Before, image.Generation);
            if (image.FinishBeforeBusy) IsBeforeBusy = false;
            return;
        }
        // replay前の変換は除外し、通常移動では実行中だった途中画像を表示する。
        if (image.Generation < _lastDisplayedAfter || image.Generation == _lastDisplayedAfter && image.DisplayRevision <= _lastDisplayedRevision) return;
        var afterBitmap = CreateBitmap(image.Frame, data, image.TraceId);
        SetRawFrame(ref _displayedFrame, ref _displayedRaw, image.Frame, "VmDisplayed");
        _lastDisplayedAfter = image.Generation;
        _lastDisplayedRevision = image.DisplayRevision;
        RecalculateDisplaySizes();
        PreviewImage = afterBitmap;
        if (PerfTrace.Enabled) PerfTrace.Write("vm.apply", new { image.TraceId, image.Generation, image.DisplayRevision, image.Before, image.Frame.Xts, target = CurrentSeconds, displaySeconds = image.DisplaySeconds, segmentId = image.SegmentId, trustedAbsolute = image.TrustedAbsolute });
        if (_context is { Kind: RequestKind.After } current && IsCurrent(current)
            && current.Request.AllowConcurrentBackground && (current.Request.VppArgs.Count > 0 || current.Request.Continuous))
            QueueBefore(RetargetInterval(current.Request, CurrentSeconds), current.Generation);
        QueueDurationAfterComparison(image.Before, image.Generation);
        UpdateNavigationStatus();
    }

    // 入力fpsから時刻を推測せず、取り直した実フレーム列の隣へ1回だけ動かす。
    private PreviewRequest? CompleteNeighbor(RequestContext context, IReadOnlyList<Y4mFrame> frames)
    {
        var request = context.Request;
        if (context.NeighborResolved) return RetargetInterval(request, context.ResolvedTargetSeconds ?? request.TargetSeconds);
        var navigation = NavigationFrames(request, context.Segment);
        if (navigation.Count > 0)
        {
            int anchor = NavigationAnchorIndex(navigation, request.TargetSeconds, request.Fps);
            int next = anchor + context.NeighborDirection;
            if (anchor >= 0 && next >= 0 && next < navigation.Count
                && NavigationNeighborsConnected(navigation[anchor], navigation[next]))
            {
                var reference = navigation[next];
                return ResolveNeighbor(reference.Frame, reference.InputSeconds, reference);
            }
        }
        else if (context.Segment?.Clock.TrustedAbsolute == true)
        {
            // 検証barrierやpurgeで候補が消えた場合、old配列を境界扱いせず新しい物理窓へ戻す。
            if (_pendingNeighbor?.Generation == context.Generation) _pendingNeighbor = null;
            if (IsCurrent(context.Generation))
                _ = QueuePreview(navigation: true, neighborDirection: context.NeighborDirection, requirePhysicalRefetch: true);
            return null;
        }
        else
        {
            var clock = context.Segment?.Clock ?? IntervalClock(request, frames);
            int index = NeighborAnchorIndex(frames, clock.ToSelectionSeconds(request.TargetSeconds, frames, request.Fps), request.Fps);
            int next = index + context.NeighborDirection;
            if (index >= 0 && next >= 0 && next < frames.Count)
            {
                var frame = frames[next];
                double seconds = clock.ToInputSeconds(frame, next, request.Fps);
                if (context.Segment is { } segment && !PreviewSafeRange.Contains(seconds, segment.SafeStartSeconds, segment.SafeEndSeconds))
                {
                    // 範囲外は画素選択をしない確定判断。取得失敗と分け、従来どおり保留だけ解除する。
                    if (_pendingNeighbor?.Generation == context.Generation) _pendingNeighbor = null;
                    return null;
                }
                return ResolveNeighbor(frame, seconds);
            }
        }
        if (_pendingNeighbor?.Generation == context.Generation) _pendingNeighbor = null;
        context.NeighborResolved = true;
        if (IsCurrent(context.Generation))
        {
            if (context.Generation > _lastDisplayedAfter)
            {
                _lastDisplayedAfter = context.Generation;
                _lastDisplayedRevision = -1;
            }
            _completedAfterStatus = context.NeighborDirection < 0 ? AppMessages.Start : AppMessages.End;
        }
        return null;

        PreviewRequest? ResolveNeighbor(Y4mFrame frame, double seconds, PreviewFrameReference? reference = null)
        {
            if (!TryRetainSelection(frame, "VmNeighborSelection", out var retained))
            {
                // この通知内では再帰せず、既存の正ID失効と同じcache miss処理へ一度だけ戻す。
                if (IsCurrent(context.Generation))
                    _ = QueuePreview(navigation: true, neighborDirection: context.NeighborDirection,
                        requirePhysicalRefetch: context.Segment?.Clock.TrustedAbsolute == true);
                return null;
            }
            using var selectedRaw = retained;
            // 取得成功後だけ、区間/anchor/保留解除と目標をcommitする。
            if (reference != null)
            {
                AdoptNavigationReference(reference);
                context.Segment = reference.Segment;
            }
            if (_pendingNeighbor?.Generation == context.Generation) _pendingNeighbor = null;
            context.NeighborResolved = true;
            context.ResolvedTargetSeconds = seconds;
            // 明示的なシークが後から来た場合も、最新の目標を過去へ戻さない。
            bool current = IsCurrent(context.Generation);
            if (current) { SetTimeOnly(seconds); _lastIntentTarget = seconds; }
            DisplayFrame(frame, context);
            if (current) NotifyAcceptedFrameStep(request, context.NeighborDirection, seconds);
            return RetargetInterval(request, seconds);
        }
    }

    private static int NeighborAnchorIndex(IReadOnlyList<Y4mFrame> frames, double target, double requestedFps)
    {
        // 既知フレームの隣は同じXtsを優先する。表示時間の許容幅で細かい間隔を飛ばさない。
        // seekとF9形式のXtsを足し戻した丸め誤差だけを吸収する。
        for (int i = 0; i < frames.Count; ++i)
            if (frames[i].Xts is double ts && Math.Abs(ts - target) <= 1e-8) return i;
        return FrameSelector.Select(frames, target, requestedFps);
    }

    private double FrameSeconds(PreviewRequest request, Y4mFrame frame, int index) =>
        IntervalClock(request, _intervalFrames).ToInputSeconds(frame, index, request.Fps);

    // 比較計測を明示した場合だけ既定マージンを上書きする。
    private static double BenchmarkPostMargin() => PerfTrace.Enabled &&
        double.TryParse(Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_POST_MARGIN"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds)
        && double.IsFinite(seconds) && seconds >= 0 ? seconds : 0.5;

    private sealed record NeighborIntent(long Generation, int Direction);

    private sealed class ImageWork(Y4mFrame Frame, long Generation, long EditEpoch,
        bool Before = false, long BeforeVersion = 0, bool FinishBeforeBusy = false,
        double? DisplaySeconds = null, long? SegmentId = null, bool TrustedAbsolute = false,
        RequestContext? RevisionContext = null, long DisplayRevision = 0, long TraceId = 0, bool DisplayOnly = false) : IDisposable
    {
        private IDisposable? _raw = RetainRawFrames(new[] { Frame }, "VmWork");
        public Y4mFrame Frame { get; } = Frame;
        public long Generation { get; } = Generation;
        public long EditEpoch { get; } = EditEpoch;
        public bool Before { get; } = Before;
        public long BeforeVersion { get; } = BeforeVersion;
        public bool FinishBeforeBusy { get; } = FinishBeforeBusy;
        public double? DisplaySeconds { get; } = DisplaySeconds;
        public long? SegmentId { get; } = SegmentId;
        public bool TrustedAbsolute { get; } = TrustedAbsolute;
        public RequestContext? RevisionContext { get; } = RevisionContext;
        public long DisplayRevision { get; } = DisplayRevision;
        public long TraceId { get; set; } = TraceId;
        public bool DisplayOnly { get; } = DisplayOnly;
        public ImageWork Retain() => new(Frame, Generation, EditEpoch, Before, BeforeVersion, FinishBeforeBusy,
            DisplaySeconds, SegmentId, TrustedAbsolute, RevisionContext, DisplayRevision, TraceId, DisplayOnly);
        public void Dispose() => Interlocked.Exchange(ref _raw, null)?.Dispose();
    }

    private static void DropImage(ref ImageWork? slot)
        => Interlocked.Exchange(ref slot, null)?.Dispose();
}

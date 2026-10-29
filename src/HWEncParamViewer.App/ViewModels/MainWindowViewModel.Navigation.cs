using System.Collections.Concurrent;
using Avalonia.Threading;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ConcurrentDictionary<long, RequestContext> _afterRequests = new();
    private long _editEpoch, _lastQueuedAfter, _lastDisplayedAfter, _lastCompletedAfter, _lastFailedAfter;
    private bool _latestIntentNavigation;
    private string _completedAfterStatus = "完了";
    private ImageWork? _pendingAfterImage, _pendingBeforeImage;
    private bool _conversionActive;

    private long BeginIntent(bool navigation)
    {
        long generation = Interlocked.Increment(ref _generation);
        _latestIntentNavigation = navigation;
        Volatile.Write(ref _context, null);
        CancelBefore();
        if (!navigation)
        {
            Interlocked.Increment(ref _editEpoch);
            _afterRequests.Clear();
            lock (_taskLock) _pendingAfterImage = null;
        }
        return generation;
    }

    private void RegisterContext(RequestContext context)
    {
        _afterRequests[context.Generation] = context;
        Volatile.Write(ref _context, context);
    }

    private Task QueuePreview(bool bypassCache = false, bool navigation = false)
    {
        if (!_initialized || _suspendChanges || _disposed) return Task.CompletedTask;
        long generation = BeginIntent(navigation);
        long editEpoch = Volatile.Read(ref _editEpoch);
        // 入力検査中の移動連打では検査そのものを止めず、取得後の最新位置だけを使う。
        if (navigation && _inputInfo == null && _preparationCancellation != null)
        { IsBusy = true; StatusText = "処理中…"; return _preparation; }
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
            Track(LoadMetadataAsync(encoder, _lifetime.Token));
        }
        if (string.IsNullOrWhiteSpace(encoder) || string.IsNullOrWhiteSpace(input))
        {
            IsBusy = false;
            ErrorText = null;
            StatusText = "エンコーダと入力ファイルを選択してください";
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
        StatusText = navigation ? "処理中…" : "入力情報を取得中…";
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
                InputInfo info = _inputInfo ?? await _probe.ProbeAsync(encoder, input, inputArgs, cancellation.Token).ConfigureAwait(false);
                await PostAsync(() =>
                {
                    if (_disposed || editEpoch != Volatile.Read(ref _editEpoch) || cancellation.IsCancellationRequested) return;
                    long actualGeneration = generation;
                    bool actualNavigation = navigation;
                    double target = seconds;
                    if (!IsCurrent(generation))
                    {
                        if (!_latestIntentNavigation) return;
                        actualGeneration = Volatile.Read(ref _generation);
                        actualNavigation = true;
                        target = CurrentSeconds;
                    }
                    ApplyInputInfo(info);
                    target = HasDuration ? Math.Min(target, DurationSeconds) : target;
                    // 古い移動の準備完了で目標時刻を過去へ戻さない。
                    if (actualGeneration == Volatile.Read(ref _generation)) SetTimeOnly(target);
                    var request = new PreviewRequest
                    {
                        EncoderPath = encoder, InputFile = input, InputArgs = inputArgs, VppArgs = vpp,
                        Fps = info.Fps, TargetSeconds = target, CorrelationId = actualGeneration,
                    };
                    bool squarePixel = info.SarNum <= 0 || info.SarDen <= 0 || info.SarNum == info.SarDen;
                    string sar = squarePixel ? "" : $" (SAR {info.SarNum}:{info.SarDen})";
                    InputInfoText = $"{info.Width}x{info.Height}{sar}  {info.Fps:0.###}fps";
                    var context = new RequestContext(actualGeneration, request, _clock.GetTimestamp(),
                        editEpoch: editEpoch);
                    RegisterContext(context);
                    StatusText = "処理中…";
                    _controller.Submit(request, bypassCache, navigation: actualNavigation);
                    Track(ReleaseContextAsync(context));
                    QueueDuration(encoder, input, inputArgs);
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
        try { await _controller.WaitForIdleAsync().ConfigureAwait(false); }
        finally
        {
            // Coreが実行を始める直前の要求も、タスク完了までは通知の対応表から外さない。
            _afterRequests.TryRemove(context.Generation, out _);
        }
    }

    private void UpdateNavigationStatus()
    {
        if (_disposed) return;
        long latest = Volatile.Read(ref _generation);
        if (_lastFailedAfter == latest) return;
        IsBusy = _lastDisplayedAfter < latest || _lastCompletedAfter < latest;
        StatusText = IsBusy ? "処理中…" : _completedAfterStatus;
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

    private void EnqueueImage(ImageWork image)
    {
        TaskCompletionSource? completion = null;
        lock (_taskLock)
        {
            if (_disposed) return;
            if (image.Before) _pendingBeforeImage = image;
            else if (_pendingAfterImage == null || image.Generation >= _pendingAfterImage.Generation) _pendingAfterImage = image;
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
                    image = _pendingAfterImage ?? _pendingBeforeImage;
                    if (image == null) { _conversionActive = false; break; }
                    if (image.Before) _pendingBeforeImage = null;
                    else _pendingAfterImage = null;
                }
                try
                {
                    byte[] data = await _imageConverter(image.Frame, _lifetime.Token).ConfigureAwait(false);
                    await PostAsync(() => ApplyImage(image, data)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    await PostAsync(() =>
                    {
                        if (_disposed || image.EditEpoch != Volatile.Read(ref _editEpoch)) return;
                        if (image.Before) { if (IsCurrent(image.Generation)) { IsBeforeBusy = false; BeforeImage = null; } }
                        else if (image.Generation == Volatile.Read(ref _generation))
                        { _lastFailedAfter = image.Generation; ShowError("画像を表示できませんでした", exception.Message); }
                    }).ConfigureAwait(false);
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
        if (image.Before)
        {
            if (!IsCurrent(image.Generation) || image.BeforeVersion != _beforeImageGeneration) return;
            var bitmap = CreateBitmap(image.Frame, data);
            _beforeDisplayedFrame = image.Frame;
            RecalculateDisplaySizes();
            BeforeImage = bitmap;
            if (image.FinishBeforeBusy) IsBeforeBusy = false;
            return;
        }
        // 最新の待ち画像があっても、実行中だった変換は必ず表示してから次へ進む。
        if (image.Generation <= _lastDisplayedAfter) return;
        var afterBitmap = CreateBitmap(image.Frame, data);
        _displayedFrame = image.Frame;
        _lastDisplayedAfter = image.Generation;
        RecalculateDisplaySizes();
        PreviewImage = afterBitmap;
        UpdateNavigationStatus();
    }

    private sealed record ImageWork(Y4mFrame Frame, long Generation, long EditEpoch,
        bool Before = false, long BeforeVersion = 0, bool FinishBeforeBusy = false);
}

using Avalonia.Threading;
using HWEncVppPreview.Core;
using static HWEncVppPreview.Core.EncoderPreviewSession;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private ImageWork? _pendingAfterImage, _pendingBeforeImage;
    private bool _conversionActive;

    private long _nextImageTraceId;

    private void EnqueueImage(ImageWork image)
    {
        // 計測時だけ識別子を付け、同じXtsのBefore/Afterや間引かれた仕事を区別する。
        if (PerfTrace.Enabled) image.TraceId = Interlocked.Increment(ref _nextImageTraceId);
        TaskCompletionSource? completion = null;
        lock (_session._taskLock)
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
                _session._tasks.Add(completion.Task);
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
                lock (_session._taskLock)
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
                    lock (_session._taskLock) { settings = _hdrDisplaySettings; displaySettingsRevision = _hdrDisplaySettingsRevision; }
                    await WithConvertedBgraAsync(image.Frame, settings, _session._lifetime.Token, async data =>
                    {
                        if (PerfTrace.Enabled) PerfTrace.Write("vm.convert.end", new { image.TraceId, image.Generation, image.Before, image.Frame.Xts, bytes = data.Length });
                        await _session.PostAsync(() =>
                        {
                            if (displaySettingsRevision != Volatile.Read(ref _hdrDisplaySettingsRevision) && YuvToBgra.GetColorInfo(image.Frame.Header).IsHdr)
                                RequeueForDisplaySettings(image);
                            else ApplyImage(image, data);
                        }).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_session._lifetime.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    await _session.PostAsync(() =>
                    {
                        if (_disposed || image.EditEpoch != Volatile.Read(ref _session._editEpoch)) return;
                        if (image.DisplayOnly)
                        {
                            if (PerfTrace.Enabled) PerfTrace.Write("vm.display_refresh.failed", new { exception.Message });
                            return;
                        }
                        if (image.RevisionContext is { } context && image.DisplayRevision != Volatile.Read(ref context.DisplayRevision))
                        {
                            if (image.Before && _session.IsCurrent(image.Generation) && image.BeforeVersion == _beforeImageGeneration) IsBeforeBusy = false;
                            return;
                        }
                        if (image.Before) { if (_session.IsCurrent(image.Generation) && image.BeforeVersion == _beforeImageGeneration) { IsBeforeBusy = false; BeforeImage = null; BeforeUnavailableForDuration(); } }
                        else if (image.Generation == Volatile.Read(ref _session._generation))
                        { _session._lastFailedAfter = image.Generation; ShowExceptionError(AppMessages.ImageDisplayFailed, "", exception); }
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
            lock (_session._taskLock) _conversionActive = false;
            completion.TrySetException(exception);
        }
        finally { lock (_session._taskLock) _session._tasks.Remove(completion.Task); }
    }

    private void ApplyImage(ImageWork image, byte[] data)
    {
        if (_disposed || image.EditEpoch != Volatile.Read(ref _session._editEpoch)) return;
        if (image.DisplayOnly)
        {
            if (!ReferenceEquals(image.Frame, image.Before ? _beforeDisplayedFrame : _displayedFrame)) return;
            if (image.Before && BeforeImage != null) BeforeImage = CreateBitmap(image.Frame, data, image.TraceId);
            else if (!image.Before && PreviewImage != null) PreviewImage = CreateBitmap(image.Frame, data, image.TraceId);
            return;
        }
        if (image.RevisionContext is { } context && image.DisplayRevision != Volatile.Read(ref context.DisplayRevision))
        {
            if (image.Before && _session.IsCurrent(image.Generation) && image.BeforeVersion == _beforeImageGeneration) IsBeforeBusy = false;
            return;
        }
        if (image.TrustedAbsolute && image.SegmentId is > 0 && !_session.CacheHasSegment(image.SegmentId.Value))
        {
            // 変換待ちの間に画素照合で破棄された区間を、UIから再び採用しない。
            _session.ValidateNavigationState();
            if (image.Generation == Volatile.Read(ref _session._generation))
            {
                if (image.Before)
                {
                    if (_session._context is { Kind: RequestKind.After } after) QueueBefore(after.Request, after.Generation);
                }
                else _ = _session.QueuePreview(navigation: true);
            }
            return;
        }
        if (image.Before)
        {
            if (!_session.IsCurrent(image.Generation) || image.BeforeVersion != _beforeImageGeneration) return;
            var bitmap = CreateBitmap(image.Frame, data, image.TraceId);
            SetRawFrame(ref _beforeDisplayedFrame, ref _session._beforeDisplayedRaw, image.Frame, "VmBeforeDisplayed");
            SelectBeforePresentationSegment(image.SegmentId, image.Frame.FrameId);
            RecalculateDisplaySizes();
            BeforeImage = bitmap;
            if (PerfTrace.Enabled) PerfTrace.Write("vm.apply", new { image.TraceId, image.Generation, image.DisplayRevision, image.Before, image.Frame.Xts, target = CurrentSeconds, displaySeconds = image.DisplaySeconds, segmentId = image.SegmentId, trustedAbsolute = image.TrustedAbsolute });
            QueueDurationAfterComparison(image.Before, image.Generation);
            if (image.FinishBeforeBusy) IsBeforeBusy = false;
            return;
        }
        // replay前の変換は除外し、通常移動では実行中だった途中画像を表示する。
        if (image.Generation < _session._lastDisplayedAfter || image.Generation == _session._lastDisplayedAfter && image.DisplayRevision <= _session._lastDisplayedRevision) return;
        var afterBitmap = CreateBitmap(image.Frame, data, image.TraceId);
        SetRawFrame(ref _displayedFrame, ref _session._displayedRaw, image.Frame, "VmDisplayed");
        _session._lastDisplayedAfter = image.Generation;
        _session._lastDisplayedRevision = image.DisplayRevision;
        RecalculateDisplaySizes();
        PreviewImage = afterBitmap;
        if (PerfTrace.Enabled) PerfTrace.Write("vm.apply", new { image.TraceId, image.Generation, image.DisplayRevision, image.Before, image.Frame.Xts, target = CurrentSeconds, displaySeconds = image.DisplaySeconds, segmentId = image.SegmentId, trustedAbsolute = image.TrustedAbsolute });
        if (_session._context is { Kind: RequestKind.After } current && _session.IsCurrent(current)
            && current.Request.AllowConcurrentBackground && (current.Request.VppArgs.Count > 0 || current.Request.Continuous))
            QueueBefore(RetargetInterval(current.Request, CurrentSeconds), current.Generation);
        QueueDurationAfterComparison(image.Before, image.Generation);
        _session.UpdateNavigationStatus();
    }

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

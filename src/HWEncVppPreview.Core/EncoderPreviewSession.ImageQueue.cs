namespace HWEncVppPreview.Core;

public sealed partial class EncoderPreviewSession
{
    private ImageWork? _pendingAfterDisplayRefresh, _pendingBeforeDisplayRefresh;
    private ImageWork? _pendingAfterImage, _pendingBeforeImage;
    private bool _conversionActive;

    private long _nextImageTraceId;

    private void EnqueueImage(ImageWork image)
    {
        // 計測時だけ識別子を付け、同じXtsのBefore/Afterや間引かれた仕事を区別する。
        if (PerfTrace.Enabled) image.TraceId = Interlocked.Increment(ref _nextImageTraceId);
        TaskCompletionSource? completion = null;
        lock (_taskLock)
        {
            if (IsDisposed)
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
                    await _host.ConvertAndPresentAsync(image).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    await PostAsync(() => PresentationFailed(image, exception)).ConfigureAwait(false);
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

    private static void DropImage(ref ImageWork? slot)
        => Interlocked.Exchange(ref slot, null)?.Dispose();

    internal void RequestDisplayRefresh(Y4mFrame frame, bool before)
        => EnqueueImage(new(frame, Volatile.Read(ref _generation), Volatile.Read(ref _editEpoch),
            Before: before, DisplayOnly: true));

    private void ReleaseImageWork()
    {
        lock (_taskLock)
        {
            DropImage(ref _pendingAfterDisplayRefresh);
            DropImage(ref _pendingBeforeDisplayRefresh);
            DropImage(ref _pendingAfterImage);
            DropImage(ref _pendingBeforeImage);
        }
    }

    // 旧設定で終わった変換は、新しい画像の待機枠を潰さず同じrawからやり直す。
    internal void RequeueForDisplaySettings(ImageWork image)
    {
        lock (_taskLock)
        {
            if (IsDisposed) return;
            if (image.DisplayOnly)
            { if ((image.Before ? _pendingBeforeDisplayRefresh : _pendingAfterDisplayRefresh) != null) return; }
            else if ((image.Before ? _pendingBeforeImage : _pendingAfterImage) != null) return;
            EnqueueImage(image.Retain());
        }
    }
}

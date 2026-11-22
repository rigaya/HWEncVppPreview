namespace HWEncVppPreview.Core;

public sealed partial class EncoderPreviewSession
{
    internal sealed class ImageWork(Y4mFrame Frame, long Generation, long EditEpoch,
        bool Before = false, long BeforeVersion = 0, bool FinishBeforeBusy = false,
        double? DisplaySeconds = null, long? SegmentId = null, bool TrustedAbsolute = false,
        RequestContext? RevisionContext = null, long DisplayRevision = 0, long TraceId = 0, bool DisplayOnly = false,
        EncoderPreviewFrameTiming Timing = default, bool IsProvisional = false) : IDisposable
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
        public EncoderPreviewFrameTiming Timing { get; } = Timing;
        public bool IsProvisional { get; } = IsProvisional;
        public ImageWork Retain() => new(Frame, Generation, EditEpoch, Before, BeforeVersion, FinishBeforeBusy,
            DisplaySeconds, SegmentId, TrustedAbsolute, RevisionContext, DisplayRevision, TraceId, DisplayOnly, Timing, IsProvisional);
        public void Dispose() => Interlocked.Exchange(ref _raw, null)?.Dispose();
    }


    private EncoderPreviewFrameTiming GetFrameTiming(Y4mFrame frame, PreviewRequest? request, PreviewSegmentSnapshot? segment)
    {
        double fps = request?.Fps ?? frame.Header.Fps;
        var clock = segment?.Clock ?? (request == null ? null : ClockFor(request, new[] { frame }));
        int index = 0;
        if (!frame.Xts.HasValue && segment != null)
            for (int i = 0; i < segment.Frames.Count; ++i)
                if (ReferenceEquals(frame, segment.Frames[i])) { index = i; break; }
        return new(clock?.ToInputSeconds(frame, index, fps) ?? request?.TargetSeconds ?? CurrentSeconds,
            frame.Xts, frame.Xdur, FrameSelector.Duration(frame, fps));
    }

    private Y4mFrame? _displayedFrame, _beforeDisplayedFrame;
    private bool _hasAfterImage, _hasBeforeImage, _beforeProvisional;
    private EncoderPreviewFrameTiming _afterPresentation, _beforePresentation;
    private long _viewRevision, _afterPresentedGeneration, _beforePresentedGeneration;

    internal Y4mFrame? DisplayedAfter => _displayedFrame;
    internal Y4mFrame? DisplayedBefore => _beforeDisplayedFrame;

    internal void ClearAfterPresentation()
    {
        if (_hasAfterImage) ++_viewRevision;
        _hasAfterImage = false;
        if (_usesOwnedRaw) SetRawFrame(ref _displayedFrame, ref _displayedRaw, null, "VmDisplayed");
    }

    private void ClearBeforePresentation()
    {
        InvalidateBeforePresentation();
        ReleaseBeforePresentationPixels();
    }

    internal void InvalidateBeforePresentation()
    {
        SelectBeforePresentationSegment(null);
        if (_hasBeforeImage) ++_viewRevision;
        _hasBeforeImage = false;
    }

    internal void ReleaseBeforePresentationPixels()
    {
        if (_usesOwnedRaw) ClearBeforeDisplayedRaw();
    }

    private void ClearBeforeDisplayedRaw()
        => SetRawFrame(ref _beforeDisplayedFrame, ref _beforeDisplayedRaw, null, "VmBeforeDisplayed");

    private void ClearBeforeImage() => _host.ClearBeforeImage();

    internal void ApplyImage(ImageWork image, Func<Action>? preparePresentation = null)
    {
        if (IsDisposed || image.EditEpoch != Volatile.Read(ref _editEpoch)) return;
        if (image.DisplayOnly)
        {
            if (!ReferenceEquals(image.Frame, image.Before ? _beforeDisplayedFrame : _displayedFrame)) return;
            if (image.Before ? _hasBeforeImage : _hasAfterImage) preparePresentation?.Invoke().Invoke();
            return;
        }
        if (image.RevisionContext is { } context && image.DisplayRevision != Volatile.Read(ref context.DisplayRevision))
        {
            if (image.Before && IsCurrent(image.Generation) && image.BeforeVersion == _beforeImageGeneration) _host.IsBeforeBusy = false;
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
            var present = preparePresentation?.Invoke();
            SetRawFrame(ref _beforeDisplayedFrame, ref _beforeDisplayedRaw, image.Frame, "VmBeforeDisplayed");
            SelectBeforePresentationSegment(image.SegmentId, image.Frame.FrameId);
            _hasBeforeImage = true;
            _beforePresentation = image.Timing;
            _beforeProvisional = image.IsProvisional;
            _beforePresentedGeneration = image.Generation;
            ++_viewRevision;
            present?.Invoke();
            if (PerfTrace.Enabled) PerfTrace.Write("vm.apply", new { image.TraceId, image.Generation, image.DisplayRevision, image.Before, image.Frame.Xts, target = CurrentSeconds, displaySeconds = image.DisplaySeconds, segmentId = image.SegmentId, trustedAbsolute = image.TrustedAbsolute });
            QueueDurationAfterComparison(image.Before, image.Generation);
            if (image.FinishBeforeBusy) _host.IsBeforeBusy = false;
            return;
        }
        // replay前の変換は除外し、通常移動では実行中だった途中画像を表示する。
        if (image.Generation < _lastDisplayedAfter || image.Generation == _lastDisplayedAfter && image.DisplayRevision <= _lastDisplayedRevision) return;
        var presentAfter = preparePresentation?.Invoke();
        SetRawFrame(ref _displayedFrame, ref _displayedRaw, image.Frame, "VmDisplayed");
        _lastDisplayedAfter = image.Generation;
        _lastDisplayedRevision = image.DisplayRevision;
        _hasAfterImage = true;
        _afterPresentation = image.Timing;
        _afterPresentedGeneration = image.Generation;
        ++_viewRevision;
        presentAfter?.Invoke();
        if (PerfTrace.Enabled) PerfTrace.Write("vm.apply", new { image.TraceId, image.Generation, image.DisplayRevision, image.Before, image.Frame.Xts, target = CurrentSeconds, displaySeconds = image.DisplaySeconds, segmentId = image.SegmentId, trustedAbsolute = image.TrustedAbsolute });
        if (_context is { Kind: RequestKind.After } current && IsCurrent(current)
            && current.Request.AllowConcurrentBackground && (current.Request.VppArgs.Count > 0 || current.Request.Continuous))
            QueueBefore(RetargetInterval(current.Request, CurrentSeconds), current.Generation);
        QueueDurationAfterComparison(image.Before, image.Generation);
        UpdateNavigationStatus();
    }

    internal void PresentationFailed(ImageWork image, Exception exception)
    {
        if (IsDisposed || image.EditEpoch != Volatile.Read(ref _editEpoch)) return;
        if (image.DisplayOnly)
        {
            if (PerfTrace.Enabled) PerfTrace.Write("vm.display_refresh.failed", new { exception.Message });
            return;
        }
        if (image.RevisionContext is { } context && image.DisplayRevision != Volatile.Read(ref context.DisplayRevision))
        {
            if (image.Before && IsCurrent(image.Generation) && image.BeforeVersion == _beforeImageGeneration) _host.IsBeforeBusy = false;
            return;
        }
        if (image.Before) { if (IsCurrent(image.Generation) && image.BeforeVersion == _beforeImageGeneration) { _host.IsBeforeBusy = false; ClearBeforeImage(); BeforeUnavailableForDuration(); } }
        else if (image.Generation == Volatile.Read(ref _generation))
        { _lastFailedAfter = image.Generation; _host.ShowPreviewFailure(PreviewFailureKind.ImageDisplayFailed, "", exception); }
    }
}

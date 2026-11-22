namespace HWEncVppPreview.Core;

public sealed partial class EncoderPreviewSession
{
    internal sealed class ImageWork(Y4mFrame Frame, long Generation, long EditEpoch,
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

}

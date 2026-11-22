namespace HWEncVppPreview.Core;

public sealed partial class EncoderPreviewSession
{
    // intervalは正IDのdescriptorだけを持つ。ID0/nullの有限窓だけ一度所有し、slot間はhandleを渡す。
    internal void SetIntervalFrames(IReadOnlyList<Y4mFrame> frames, RequestContext? source = null)
    {
        var retained = source == null
            ? AcquireRawSegment(_intervalSegment, frames, _intervalRaw, windowKind: "VmId0IntervalWindow")
            : source.AcquireIntervalHold(_intervalSegment, frames, _intervalRaw, "VmId0IntervalWindow");
        var previous = _intervalRaw;
        _intervalRaw = retained;
        _intervalFrames = retained.Frames;
        previous?.Dispose();
    }

    internal void SetBeforeIntervalFrames(IReadOnlyList<Y4mFrame> frames, RequestContext? source = null,
        RawSegmentHold? shared = null)
    {
        var retained = source == null
            ? AcquireRawSegment(_beforeIntervalSegment, frames, _beforeIntervalRaw, shared, "VmId0BeforeWindow")
            : source.AcquireIntervalHold(_beforeIntervalSegment, frames, _beforeIntervalRaw, "VmId0BeforeWindow");
        SetBeforeIntervalWindow(retained);
    }

    internal void SetBeforeIntervalWindow(RawSegmentHold retained)
    {
        var previous = _beforeIntervalRaw;
        _beforeIntervalRaw = retained;
        _beforeIntervalFrames = retained.Frames;
        previous?.Dispose();
    }

    internal void SetIntervalFramesFromWindow(IReadOnlyList<Y4mFrame> frames, RawSegmentHold source)
    {
        var retained = AcquireRawSegment(_intervalSegment, frames, _intervalRaw, source, "VmId0IntervalWindow");
        var previous = _intervalRaw;
        _intervalRaw = retained;
        _intervalFrames = retained.Frames;
        previous?.Dispose();
    }

    internal void SetBeforeFramesFromWindow(IReadOnlyList<Y4mFrame> frames, RawSegmentHold source)
        => SetBeforeIntervalWindow(AcquireRawSegment(_beforeIntervalSegment, frames, _beforeIntervalRaw,
            source, "VmId0BeforeWindow"));

    internal abstract class RawSegmentHold : IDisposable
    {
        // descriptorはrawの解放後も参照できる。画素の生存保証とは分ける。
        public abstract IReadOnlyList<Y4mFrame> Frames { get; }
        public abstract void Dispose();
    }

    internal sealed class CachedSegmentMetadata(IReadOnlyList<Y4mFrame> frames) : RawSegmentHold
    {
        public override IReadOnlyList<Y4mFrame> Frames { get; } = Array.AsReadOnly(frames.ToArray());
        public override void Dispose() { }
    }

    private static RawSegmentHold AcquireRawSegment(PreviewSegmentSnapshot? segment,
        IReadOnlyList<Y4mFrame> frames, RawSegmentHold? current = null, RawSegmentHold? source = null,
        string windowKind = "VmId0Window")
    {
        // 正IDはcache所属を借りるmetadataだけ。空列にもraw所有を作らない。
        if (segment?.SegmentId > 0 || frames.Count == 0)
            return new CachedSegmentMetadata(frames);
        if (current is OwnedUncachedWindow previous && previous.ShareIfSame(frames) is { } reused)
            return reused;
        if (!ReferenceEquals(current, source) && source is OwnedUncachedWindow origin
            && origin.ShareIfSame(frames) is { } shared)
            return shared;
        // ID0/nullの完了窓は元所属が生存する場所で一度だけ確保する。legacyも同じ窓寿命を持つ。
        // subsetは親全窓と共有せず、新しい少数窓として一度だけ確保する。
        return OwnedUncachedWindow.Capture(frames, windowKind);
    }

    internal sealed class OwnedUncachedWindow : RawSegmentHold
    {
        private readonly RawFrameWindow _window;
        private OwnedUncachedWindow(RawFrameWindow window) => _window = window;
        public override IReadOnlyList<Y4mFrame> Frames => _window.Frames;
        internal static OwnedUncachedWindow Capture(IReadOnlyList<Y4mFrame> frames, string kind)
            => AdoptShared(RawFrameWindow.Capture(frames, kind));
        internal static bool TryCapture(IReadOnlyList<Y4mFrame> frames, string kind,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out OwnedUncachedWindow? window)
        {
            window = null;
            if (!RawFrameWindow.TryCapture(frames, kind, out var retained)) return false;
            window = AdoptShared(retained);
            return true;
        }
        internal static OwnedUncachedWindow Adopt(IReadOnlyList<Y4mFrame> frames, IDisposable? ownership)
            => AdoptShared(RawFrameWindow.Adopt(frames, ownership));
        internal static OwnedUncachedWindow AdoptShared(RawFrameWindow window)
        {
            try { return new(window); }
            catch { window.Dispose(); throw; }
        }
        internal OwnedUncachedWindow? ShareIfSame(IReadOnlyList<Y4mFrame> frames)
            => _window.ShareIfSame(frames) is { } shared ? AdoptShared(shared) : null;
        public override void Dispose() => _window.Dispose();
    }

    // 完了通知と別のキャッシュ読取は異なる列を持ち得るため、両方の取得済み所有をUI処理まで保つ。
    internal sealed class CompletedWindowSources(OwnedUncachedWindow? notification, OwnedUncachedWindow? cached,
        PreviewSegmentSnapshot? rescue = null, bool rescueUnavailable = false) : IDisposable
    {
        public PreviewSegmentSnapshot? RescueSegment { get; } = rescue;
        public bool RescueUnavailable { get; } = rescueUnavailable;
        public OwnedUncachedWindow? ShareMatch(IReadOnlyList<Y4mFrame> frames)
            => notification?.ShareIfSame(frames) ?? cached?.ShareIfSame(frames);
        public void Dispose()
        {
            notification?.Dispose();
            cached?.Dispose();
        }
    }
    internal enum RequestKind { After, Before }
    internal sealed class RequestContext(long generation, PreviewRequest request, long started, RequestKind kind = RequestKind.After,
        CancellationTokenSource? beforeCancellation = null, long editEpoch = 0, int neighborDirection = 0)
    {
        public long Generation { get; } = generation;
        public PreviewRequest Request { get; } = request;
        public long Started { get; } = started;
        public RequestKind Kind { get; } = kind;
        public long EditEpoch { get; } = editEpoch;
        public int NeighborDirection { get; } = neighborDirection;
        public CancellationTokenSource? BeforeCancellation { get; } = beforeCancellation;
        public long DisplayRevision;
        public int ReplayStarted;
        public int BeforeCompletionReceived;
        public Y4mFrame? BeforeEarlyFrame;
        public double BeforeEarlyTarget;
        public string? BeforeEarlyFallbackReason;
        public bool ReplayUiStarted;
        public bool NeighborResolved { get; set; }
        public double? ResolvedTargetSeconds { get; set; }
        private readonly object _segmentSync = new();
        private PreviewSegmentSnapshot? _segment;
        private RawSegmentHold? _segmentRaw;
        public PreviewSegmentSnapshot? Segment
        {
            get
            {
                lock (_segmentSync) return _segment;
            }
            set => SetSegmentSharing(value);
        }
        public void SetSegmentSharing(PreviewSegmentSnapshot? value, RawSegmentHold? source = null)
        {
            RawSegmentHold? previous;
            lock (_segmentSync)
            {
                // 正IDはmetadataだけ。ID0は同じ窓のhandleを共有し、交換後の旧所有はlock外で返す。
                var retained = value == null ? null : AcquireRawSegment(value, value.Frames, _segmentRaw, source);
                previous = _segmentRaw;
                _segment = value == null ? null : value with { Frames = retained!.Frames };
                _segmentRaw = retained;
            }
            previous?.Dispose();
        }
        public RawSegmentHold AcquireIntervalHold(PreviewSegmentSnapshot? segment, IReadOnlyList<Y4mFrame> frames,
            RawSegmentHold? current, string kind)
        {
            lock (_segmentSync) return AcquireRawSegment(segment, frames, current, _segmentRaw, kind);
        }
        public string Diagnostics { get; set; } = "";
    }
}

namespace HWEncVppPreview.Core;

public sealed partial class EncoderPreviewSession
{
    private readonly object _rawPostSync = new();
    private readonly HashSet<OwnedUiPost> _rawPosts = new();
    internal IDisposable? _displayedRaw, _beforeDisplayedRaw, _anchorRaw;
    internal RawSegmentHold? _intervalRaw, _beforeIntervalRaw;

    // legacyの列は何も取得しない。ownedは同じviewのまま明示所有だけを追加する。
    internal static IDisposable? RetainRawFrames(IReadOnlyList<Y4mFrame>? frames, string kind)
        => frames == null || !frames.Any(frame => frame.RawOwner != null)
            ? null : RawFrameSetLease.Capture(frames, kind);

    // gate外で選んだdescriptorは、この取得が成功するまで選択状態へ反映しない。
    internal static bool TryRetainSelection(Y4mFrame frame, string caller, out IDisposable? ownership)
    {
        if (frame.RawOwner == null) { ownership = null; return true; }
        bool success = RawFrameLease.TryCapture(frame, caller, out var retained);
        ownership = retained;
        return success;
    }

    internal static void SetRawFrame(ref Y4mFrame? slot, ref IDisposable? ownership, Y4mFrame? frame, string kind)
    {
        var retained = frame == null ? null : RetainRawFrames(new[] { frame }, kind);
        var previous = ownership;
        slot = frame;
        ownership = retained;
        previous?.Dispose();
    }

    private static void SetRawAnchor(ref PreviewFrameReference? slot, ref IDisposable? ownership,
        PreviewFrameReference? anchor)
    {
        var retained = anchor == null ? null : RetainRawFrames(new[] { anchor.Frame }, "VmAnchor");
        var previous = ownership;
        slot = anchor;
        ownership = retained;
        previous?.Dispose();
    }

    private OwnedUiPost CaptureRawPost(RequestContext context, IReadOnlyList<Y4mFrame> frames, Action callback)
        => CaptureOwnershipPost(context, RetainRawFrames(frames, "VmUi"), callback);

    internal OwnedUiPost CaptureOwnershipPost(RequestContext context, IDisposable? ownership, Action callback)
    {
        try
        {
            var post = new OwnedUiPost(context, ownership, callback, this);
            // Beforeの反映と破棄を静止待ちに含める。
            if (post.Completion is { } completion) Track(completion);
            lock (_rawPostSync)
            {
                if (_host.IsDisposed) post.Dispose();
                else if (_usesOwnedRaw) _rawPosts.Add(post);
            }
            return post;
        }
        catch { ownership?.Dispose(); throw; }
    }

    internal void PostOwnership(RequestContext context, IDisposable ownership, Action callback)
    {
        var post = CaptureOwnershipPost(context, ownership, callback);
        try { _post(post.Execute); }
        catch { post.Dispose(); throw; }
    }

    internal void PostRaw(RequestContext context, IReadOnlyList<Y4mFrame> frames, Action callback)
    {
        if (!_usesOwnedRaw)
        {
            if (context.Kind == RequestKind.Before) Track(PostAsync(callback));
            else _post(callback);
            return;
        }
        var post = CaptureRawPost(context, frames, callback);
        try
        {
            _post(post.Execute);
        }
        catch
        {
            post.Dispose();
            throw;
        }
    }

    internal void DropRawPosts(Func<RequestContext, bool> predicate)
    {
        OwnedUiPost[] removed;
        lock (_rawPostSync) removed = _rawPosts.Where(post => predicate(post.Context)).ToArray();
        foreach (var post in removed)
        {
            if (post.Drop()) ReleaseDetachedContext(post.Context);
        }
    }

    internal void ReleaseDetachedContext(RequestContext context)
    {
        if (ReferenceEquals(Volatile.Read(ref _context), context)
            || ReferenceEquals(Volatile.Read(ref _beforeExecutionContext), context)
            || _afterRequests.TryGetValue(context.Generation, out var tracked) && ReferenceEquals(tracked, context)) return;
        context.Segment = null;
    }

    // 待機中の取消とUI側の引き取りは排他的。Taken後は実処理のfinallyだけが返す。
    internal sealed class OwnedUiPost(RequestContext context, IDisposable? ownership, Action callback,
        EncoderPreviewSession owner) : IDisposable
    {
        private readonly object _sync = new();
        private IDisposable? _ownership = ownership;
        private Action? _callback = callback;
        private readonly TaskCompletionSource? _completion = context.Kind == RequestKind.Before
            ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        public Task? Completion => _completion?.Task;
        public RequestContext Context { get; } = context;
        public void Execute()
        {
            Action? action;
            IDisposable? retained;
            lock (_sync)
            {
                action = _callback;
                _callback = null;
                retained = _ownership;
                _ownership = null;
            }
            lock (owner._rawPostSync) owner._rawPosts.Remove(this);
            try
            {
                action?.Invoke();
            }
            finally
            {
                retained?.Dispose();
                owner.ReleaseDetachedContext(Context);
                _completion?.TrySetResult();
            }
        }
        public void Dispose() => Drop();
        internal bool Drop()
        {
            IDisposable? retained;
            bool dropped;
            lock (_sync)
            {
                dropped = _callback != null;
                _callback = null;
                retained = _ownership;
                _ownership = null;
            }
            lock (owner._rawPostSync) owner._rawPosts.Remove(this);
            retained?.Dispose();
            if (dropped) _completion?.TrySetResult();
            return dropped;
        }
    }

    internal void OnOwnedFrameReady(OwnedPreviewEnvelope value)
    {
        if (value.FrameReady is { } frame) _host.OnFrameReady(frame);
    }
    internal void OnOwnedCompleted(OwnedPreviewEnvelope value)
    {
        if (value.Completed is not { } completed) return;
        using var source = value.ShareFiniteWindow() is { } window ? OwnedUncachedWindow.AdoptShared(window) : null;
        _host.OnCompletedWithWindow(completed, source);
    }
    internal void OnOwnedSegmentUpdated(OwnedPreviewEnvelope value)
    {
        if (value.SegmentUpdated is not { } segment) return;
        using var source = value.ShareFiniteWindow() is { } window ? OwnedUncachedWindow.AdoptShared(window) : null;
        OnSegmentUpdatedWithWindow(segment, source);
    }

    internal RawCacheRead? ReadCacheSegment(long id, string kind)
    {
        if (!_usesOwnedRaw)
            return _cache.GetSegment(id) is { } legacy ? new(legacy, null) : null;
        var owned = _cache.GetSegmentOwned(id, kind);
        return owned == null ? null : new(owned.Segment, owned);
    }

    internal RawCacheRead? ReadCacheAt(PreviewRequest request, double target, string kind)
    {
        if (!_usesOwnedRaw)
            return _cache.TryGetAt(request, target, out var legacy) ? new(legacy, null) : null;
        var owned = _cache.TryGetAtOwned(request, target, kind);
        return owned == null ? null : new(owned.Segment, owned);
    }

    internal RawCacheRead? ReadCacheExact(PreviewRequest request, string kind, out PreviewRequest physical)
    {
        if (!_usesOwnedRaw)
        {
            if (!_cache.TryGet(request, out var frames, out physical)) return null;
            return new(null, null, frames);
        }
        var owned = _cache.TryGetOwned(request, kind, out physical);
        return owned == null ? null : new(owned.Segment, owned, owned.Frames);
    }

    internal sealed class RawCacheRead(PreviewSegmentSnapshot? segment, IDisposable? ownership,
        IReadOnlyList<Y4mFrame>? frames = null) : IDisposable
    {
        private IDisposable? _ownership = ownership;
        public PreviewSegmentSnapshot? Segment { get; } = segment;
        public IReadOnlyList<Y4mFrame> Frames { get; } = frames ?? segment?.Frames ?? Array.Empty<Y4mFrame>();
        public OwnedUncachedWindow TakeWindow()
        {
            // 引き渡した所有は受取側が例外時も返すため、ここでは二重に破棄しない。
            return OwnedUncachedWindow.Adopt(Frames, Interlocked.Exchange(ref _ownership, null));
        }
        public void Dispose() => Interlocked.Exchange(ref _ownership, null)?.Dispose();
    }
}

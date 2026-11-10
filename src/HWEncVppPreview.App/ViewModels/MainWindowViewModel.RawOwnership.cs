using Avalonia.Threading;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly object _rawPostSync = new();
    private readonly HashSet<OwnedUiPost> _rawPosts = new();
    private IDisposable? _displayedRaw, _beforeDisplayedRaw, _anchorRaw;
    private RawSegmentHold? _intervalRaw, _beforeIntervalRaw;

    // legacyの列は何も取得しない。ownedは同じviewのまま明示所有だけを追加する。
    private static IDisposable? RetainRawFrames(IReadOnlyList<Y4mFrame>? frames, string kind)
        => frames == null || !frames.Any(frame => frame.RawOwner != null)
            ? null : RawFrameSetLease.Capture(frames, kind);

    // gate外で選んだdescriptorは、この取得が成功するまで選択状態へ反映しない。
    private static bool TryRetainSelection(Y4mFrame frame, string caller, out IDisposable? ownership)
    {
        if (frame.RawOwner == null) { ownership = null; return true; }
        bool success = RawFrameLease.TryCapture(frame, caller, out var retained);
        ownership = retained;
        return success;
    }

    private static void SetRawFrame(ref Y4mFrame? slot, ref IDisposable? ownership, Y4mFrame? frame, string kind)
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

    private OwnedUiPost CaptureOwnershipPost(RequestContext context, IDisposable? ownership, Action callback)
    {
        try
        {
            var post = new OwnedUiPost(context, ownership, callback, this);
            lock (_rawPostSync)
            {
                if (_disposed) post.Dispose();
                else if (_usesOwnedRaw) _rawPosts.Add(post);
            }
            return post;
        }
        catch { ownership?.Dispose(); throw; }
    }

    private void PostOwnership(RequestContext context, IDisposable ownership, Action callback)
    {
        var post = CaptureOwnershipPost(context, ownership, callback);
        try { Dispatcher.UIThread.Post(post.Execute); }
        catch { post.Dispose(); throw; }
    }

    private void PostRaw(RequestContext context, IReadOnlyList<Y4mFrame> frames, Action callback)
    {
        if (!_usesOwnedRaw)
        {
            Dispatcher.UIThread.Post(callback);
            return;
        }
        var post = CaptureRawPost(context, frames, callback);
        try
        {
            Dispatcher.UIThread.Post(post.Execute);
        }
        catch
        {
            post.Dispose();
            throw;
        }
    }

    private void DropRawPosts(Func<RequestContext, bool> predicate)
    {
        OwnedUiPost[] removed;
        lock (_rawPostSync) removed = _rawPosts.Where(post => predicate(post.Context)).ToArray();
        foreach (var post in removed)
        {
            if (post.Drop()) ReleaseDetachedContext(post.Context);
        }
    }

    private void ReleaseDetachedContext(RequestContext context)
    {
        if (ReferenceEquals(Volatile.Read(ref _context), context)
            || ReferenceEquals(Volatile.Read(ref _beforeExecutionContext), context)
            || _afterRequests.TryGetValue(context.Generation, out var tracked) && ReferenceEquals(tracked, context)) return;
        context.Segment = null;
    }

    // 待機中の取消とUI側の引き取りは排他的。Taken後は実処理のfinallyだけが返す。
    private sealed class OwnedUiPost(RequestContext context, IDisposable? ownership, Action callback,
        MainWindowViewModel owner) : IDisposable
    {
        private readonly object _sync = new();
        private IDisposable? _ownership = ownership;
        private Action? _callback = callback;
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
            return dropped;
        }
    }

    private void OnOwnedFrameReady(OwnedPreviewEnvelope value)
    {
        if (value.FrameReady is { } frame) OnFrameReady(frame);
    }
    private void OnOwnedCompleted(OwnedPreviewEnvelope value)
    {
        if (value.Completed is not { } completed) return;
        using var source = value.ShareFiniteWindow() is { } window ? OwnedUncachedWindow.AdoptShared(window) : null;
        OnCompletedWithWindow(completed, source);
    }
    private void OnOwnedSegmentUpdated(OwnedPreviewEnvelope value)
    {
        if (value.SegmentUpdated is not { } segment) return;
        using var source = value.ShareFiniteWindow() is { } window ? OwnedUncachedWindow.AdoptShared(window) : null;
        OnSegmentUpdatedWithWindow(segment, source);
    }

    private RawCacheRead? ReadCacheSegment(long id, string kind)
    {
        if (!_usesOwnedRaw)
            return _cache.GetSegment(id) is { } legacy ? new(legacy, null) : null;
        var owned = _cache.GetSegmentOwned(id, kind);
        return owned == null ? null : new(owned.Segment, owned);
    }

    private RawCacheRead? ReadCacheAt(PreviewRequest request, double target, string kind)
    {
        if (!_usesOwnedRaw)
            return _cache.TryGetAt(request, target, out var legacy) ? new(legacy, null) : null;
        var owned = _cache.TryGetAtOwned(request, target, kind);
        return owned == null ? null : new(owned.Segment, owned);
    }

    private RawCacheRead? ReadCacheExact(PreviewRequest request, string kind, out PreviewRequest physical)
    {
        if (!_usesOwnedRaw)
        {
            if (!_cache.TryGet(request, out var frames, out physical)) return null;
            return new(null, null, frames);
        }
        var owned = _cache.TryGetOwned(request, kind, out physical);
        return owned == null ? null : new(owned.Segment, owned, owned.Frames);
    }

    private sealed class RawCacheRead(PreviewSegmentSnapshot? segment, IDisposable? ownership,
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
    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertOwnedRawReleased()
    {
        if (_rawOwnershipScope == null) return;
        var state = _rawOwnershipScope.Capture();
        string remaining = System.Text.Json.JsonSerializer.Serialize(state.OwnerKinds);
        string message = $"raw所有権: owners={state.LiveOwners}, tokens={state.LiveTokens}, bytes={state.LiveBytes}, kinds={remaining}";
        if (Volatile.Read(ref _shutdownIncomplete) != 0)
        {
            System.Diagnostics.Trace.TraceWarning("終了未完了のため台帳0判定を保留: {0}", message);
            return;
        }
        System.Diagnostics.Debug.Assert(state.IsEmpty, message);
    }

}

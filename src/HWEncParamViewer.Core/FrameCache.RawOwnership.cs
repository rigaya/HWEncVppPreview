namespace HWEncParamViewer.Core;

public sealed partial class FrameCache
{
    internal RawOwnershipScope? OwnershipScope
    { get; }
    internal int PendingRawReleaseCount
    {
        get
        {
            lock (_gate)
                return _rawReleases.Count;
        }
    }
    private List<IDisposable> _rawReleases = new();
    private void ValidateRawScope(Y4mFrame frame)
    {
        if (frame.RawOwner is { } owner && !ReferenceEquals(owner.Scope, OwnershipScope))
            throw new InvalidOperationException("別の所有scopeのframeをcacheへ保存できません。");
    }
    internal FrameCache(RawOwnershipScope scope, long capacityBytes = 1L << 30) : this(capacityBytes)
    {
        OwnershipScope = scope;
    }
    private void DeferRawRelease(IDisposable item)
    {
        // legacyにはraw所有がなく、遅延解放queue自体を保持rootにしない。
        if (OwnershipScope == null)
        {
            item.Dispose();
            return;
        }
        if (Monitor.IsEntered(_gate))
            _rawReleases.Add(item);
        else
            item.Dispose();
    }
    private readonly struct RawReleaseGuard(FrameCache cache) : IDisposable
    {
        public void Dispose()
        {
            if (cache.OwnershipScope == null || Monitor.IsEntered(cache._gate))
                return;
            List<IDisposable> items;
            lock (cache._gate)
            {
                if (cache._rawReleases.Count == 0)
                    return;
                items = cache._rawReleases;
                cache._rawReleases = new();
            }
            foreach (var item in items)
                item.Dispose();
        }
    }
    private RawReleaseGuard ReleaseRawOutsideGate() => new(this);
    private readonly struct DeferredRaw(FrameCache cache, IDisposable item) : IDisposable
    {
        public void Dispose() => cache.DeferRawRelease(item);
    }
    internal RawFrameSetLease? GetSegmentOwned(long id, string ownerKind)
    {
        lock (_gate)
        {
            var segment = GetSegment(id);
            return segment == null ? null : RawFrameSetLease.Capture(segment, ownerKind);
        }
    }
    internal RawFrameSetLease? TryGetAtOwned(PreviewRequest request, double time, string ownerKind)
    {
        lock (_gate)
            return TryGetAt(request, time, out var segment) ? RawFrameSetLease.Capture(segment, ownerKind) : null;
    }
    internal RawFrameSetLease? TryGetOwned(PreviewRequest request, string ownerKind, out PreviewRequest physicalRequest)
    {
        lock (_gate)
            return TryGet(request, out var frames, out physicalRequest) ? RawFrameSetLease.Capture(frames, ownerKind) : null;
    }
    internal OwnedNavigationLease GetNavigationFramesOwned(PreviewRequest request, string ownerKind)
    {
        using var release = ReleaseRawOutsideGate();
        lock (_gate)
            return new(GetNavigationFrames(request), ownerKind);
    }
}

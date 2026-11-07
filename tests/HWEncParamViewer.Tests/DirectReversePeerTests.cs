using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class DirectReversePeerTests
{
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 60, FpsDen = 1 };
    private static PreviewRequest Request(double target) => new() { EncoderPath = "encoder", InputFile = "input", Fps = 60,
        TargetSeconds = target, Capabilities = new(true), TimestampOriginSeconds = 1000 };
    private static Y4mFrame Frame(double time, double duration = 1d / 60) => new(Header, 1000 + time, duration, new byte[6]);

    [Theory]
    [InlineData(-.25, false)]
    [InlineData(.25, false)]
    [InlineData(1.25, false)]
    [InlineData(2.25, false)]
    [InlineData(-.25, true)]
    [InlineData(.25, true)]
    [InlineData(1.25, true)]
    [InlineData(2.25, true)]
    public void DirectFuturePeerSurvivesAcrossDifferentNextFrameBoundaries(double offsetFrames, bool newestPeer)
    {
        const double target = 302.352055;
        var request = Request(target);
        var cache = new FrameCache(88 * 6);
        PreviewSegmentSnapshot StorePeer() => cache.StoreSegment(request,
            Enumerable.Range(0, 42).Select(i => Frame(target + i / 60d)).ToArray(),
            new(1000, 0, true), target + offsetFrames / 60, target + 41 / 60d)!;
        var peer = newestPeer ? null : StorePeer();
        var current = cache.StoreSegment(request, Enumerable.Range(0, 44).Select(i => Frame(target + (i - 36) / 60d)).ToArray(),
            new(1000, 0, true), 301.735, target + 7 / 60d, firstOutputInputSeconds: 301.7)!;
        peer ??= StorePeer();
        cache.PinCurrent(current.SegmentId, target);
        Assert.True(cache.CanStitch(request));
        long room = (8 - (int)Math.Ceiling(offsetFrames)) * 6;
        Assert.Equal(room, cache.ReverseWindowRoom(request, 301.735, target, 4d / 60));
        Assert.Equal(room >= 6 * 6, cache.TryTrimReverseWindow(request, 301.735, target, 4d / 60, 6 * 6));
        Assert.NotNull(cache.GetSegment(peer.SegmentId));
        Assert.True(cache.TryGetAt(request, target + .3, out _));
        var range = cache.GetConnectedSafeRange(request)!.Value;
        Assert.True(range.End >= peer.SafeEndSeconds);
        Assert.True(cache.SizeBytes <= cache.CapacityBytes);
    }

    [Fact]
    public void TrimCannotEraseTheValidatedPreviousPeerOverlap()
    {
        var request = Request(10.6);
        var cache = new FrameCache(12 * 6);
        var previous = cache.StoreSegment(request, new[] { 10, 10.1, 10.2, 10.3 }.Select(t => Frame(t, .1)).ToArray(),
            new(1000, 0, true), 10, 10.35)!;
        var current = cache.StoreSegment(request, new[] { 10.2, 10.3, 10.4, 10.5, 10.6, 10.7 }.Select(t => Frame(t, .1)).ToArray(),
            new(1000, 0, true), 10.2, 10.8)!;
        cache.PinCurrent(current.SegmentId, 10.6);
        Assert.Equal(2 * 6, cache.ReverseWindowRoom(request, 10.4, 10.6, .2));
        Assert.False(cache.TryTrimReverseWindow(request, 10.4, 10.6, .2, 3 * 6));
        Assert.Equal(current.Frames, cache.GetSegment(current.SegmentId)!.Frames);
        Assert.NotNull(cache.GetSegment(previous.SegmentId));
    }

    [Fact]
    public void OnlyTheNearestValidatedPeerOnEachSideIsProtected()
    {
        var request = Request(10.5);
        var cache = new FrameCache(45 * 6);
        PreviewSegmentSnapshot Store(double start, int count, double end) => cache.StoreSegment(request,
            Enumerable.Range(0, count).Select(i => Frame(start + i * .1, .1)).ToArray(), new(1000, 0, true), start, end)!;
        var previousFar = Store(9.5, 8, 10.25);
        var previousNear = Store(9.8, 6, 10.35);
        var nextNear = Store(10.8, 6, 11.35);
        var nextFar = Store(10.9, 7, 11.55);
        var twoHop = Store(11.4, 5, 11.85);
        var current = Store(10, 11, 11.05);
        cache.PinCurrent(current.SegmentId, 10.5);
        Assert.Equal(23 * 6, cache.ReverseWindowRoom(request, 10, 10.5, .2));
        Assert.True(cache.TryTrimReverseWindow(request, 10, 10.5, .2, 22 * 6));
        Assert.NotNull(cache.GetSegment(previousNear.SegmentId));
        Assert.NotNull(cache.GetSegment(nextNear.SegmentId));
        Assert.Null(cache.GetSegment(previousFar.SegmentId));
        Assert.Null(cache.GetSegment(nextFar.SegmentId));
        Assert.Null(cache.GetSegment(twoHop.SegmentId));
    }
    [Fact]
    public void NormalEvictionUsesTheSameDirectPeerProtection()
    {
        var request = Request(10.5);
        var cache = new FrameCache(35 * 6);
        var oldRequest = request with { VppArgs = new[] { "other" } };
        var old = cache.StoreSegment(oldRequest, Enumerable.Range(0, 10).Select(i => Frame(40 + i * .1, .1)).ToArray(),
            new(1000, 0, true), 40, 41)!;
        var previous = cache.StoreSegment(request, Enumerable.Range(0, 6).Select(i => Frame(9.8 + i * .1, .1)).ToArray(),
            new(1000, 0, true), 9.8, 10.35)!;
        var next = cache.StoreSegment(request, Enumerable.Range(0, 6).Select(i => Frame(10.8 + i * .1, .1)).ToArray(),
            new(1000, 0, true), 10.8, 11.35)!;
        var current = cache.StoreSegment(request, Enumerable.Range(0, 11).Select(i => Frame(10 + i * .1, .1)).ToArray(),
            new(1000, 0, true), 10, 11.05)!;
        cache.PinCurrent(current.SegmentId, 10.5);
        Assert.True(cache.TryGetAt(oldRequest, 40.3, out _));
        Assert.NotNull(cache.StoreSegment(oldRequest, Enumerable.Range(0, 8).Select(i => Frame(60 + i * .1, .1)).ToArray(),
            new(1000, 0, true), 60, 61));
        Assert.Null(cache.GetSegment(old.SegmentId));
        Assert.NotNull(cache.GetSegment(previous.SegmentId));
        Assert.NotNull(cache.GetSegment(next.SegmentId));
    }

    [Fact]
    public void NumericOverlapWithoutValidationIsNotAProtectedBridge()
    {
        var request = Request(10.6);
        var cache = new FrameCache(13 * 6);
        var unknown = cache.StoreSegment(request, new[] { 10, 10.1, 10.2, 10.3, 10.4 }.Select(t => Frame(t, .1)).ToArray(),
            new(1000, 0, true), 10, 10.45)!;
        var current = cache.StoreSegment(request, new[] { 10.3, 10.4, 10.5, 10.6, 10.7, 10.8 }.Select(t => Frame(t, .1)).ToArray(),
            new(1000, 0, true), 10.3, 10.9)!;
        // 数値範囲だけは重なるが、直接の照合完了記録がない状態を作る。
        var entries = (System.Collections.IDictionary)typeof(FrameCache).GetField("_segments", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cache)!;
        object entry = entries[current.SegmentId]!;
        ((HashSet<long>)entry.GetType().GetProperty("ValidatedPeers")!.GetValue(entry)!).Clear();
        cache.PinCurrent(current.SegmentId, 10.6);
        Assert.Equal(0, cache.ReverseWindowRoom(request, 10, 10.6, .2));
        Assert.False(cache.TryTrimReverseWindow(request, 10, 10.6, .2, 6));
        Assert.NotNull(cache.GetSegment(unknown.SegmentId));
        Assert.True(cache.TryTrimReverseWindow(request, 10.3, 10.6, .2, 6 * 6));
        Assert.Null(cache.GetSegment(unknown.SegmentId));
    }
    [Fact]
    public void NormalPrefixRollingLetsPastConnectionExpireAndKeepsDisplayNeighbors()
    {
        var request = Request(10.6);
        var cache = new FrameCache(12 * 6);
        var previous = cache.StoreSegment(request, new[] { 10, 10.1, 10.2, 10.3 }.Select(t => Frame(t, .1)).ToArray(),
            new(1000, 0, true), 10, 10.35)!;
        var current = cache.StoreSegment(request, new[] { 10.2, 10.3, 10.4, 10.5, 10.6, 10.7 }.Select(t => Frame(t, .1)).ToArray(),
            new(1000, 0, true), 10.2, 10.8)!;
        cache.PinCurrent(current.SegmentId, 10.6);
        long active = cache.BeginSegment(request with { VppArgs = new[] { "other" } }, new(1000, 0, true), 20);
        int Common() => cache.GetSegment(current.SegmentId)!.Frames.Select(f => f.Xts)
            .Intersect(previous.Frames.Select(f => f.Xts)).Count();
        for (int step = 1; step <= 6; step++)
        {
            Assert.True(cache.Append(active, Frame(20 + step * .1, .1)));
            Assert.True(cache.CanStitch(request));
            Assert.True(cache.TryGetAt(request, 10.6, out _));
            Assert.Contains(cache.GetSegment(current.SegmentId)!.Frames, f => Math.Abs(f.Xts!.Value - 1010.7) < 1e-8);
            Assert.True(cache.SizeBytes <= cache.CapacityBytes);
            if (step == 2) Assert.Equal(2, Common());
            if (step == 3) { Assert.Equal(1, Common()); Assert.NotNull(cache.GetSegment(previous.SegmentId)); }
            if (step == 4)
            {
                Assert.Equal(0, Common());
                Assert.NotNull(cache.GetSegment(previous.SegmentId));
                Assert.True(cache.GetConnectedSafeRange(request)!.Value.Start > previous.SafeEndSeconds);
            }
            if (step >= 5) Assert.Null(cache.GetSegment(previous.SegmentId));
        }
        Assert.Equal(6, cache.GetSegment(active)!.Frames.Count);
    }

    [Fact]
    public void DenseVfrCannotCountOnePeerFrameTwiceAsTwoCommonPoints()
    {
        var request = Request(10.6);
        var cache = new FrameCache(12 * 6);
        var previous = cache.StoreSegment(request, new[] { 10, 10.1, 10.2, 10.3 }.Select(t => Frame(t)).ToArray(),
            new(1000, 0, true), 10, 10.35)!;
        var current = cache.StoreSegment(request, new[] { 10.2, 10.3, 10.301, 10.302, 10.6, 10.7 }.Select(t => Frame(t)).ToArray(),
            new(1000, 0, true), 10.2, 10.8)!;
        cache.PinCurrent(current.SegmentId, 10.6);
        long active = cache.BeginSegment(request with { VppArgs = new[] { "other" } }, new(1000, 0, true), 20);
        for (int n = 0; n < 3; n++) Assert.True(cache.Append(active, Frame(20 + n * .1)));
        cache.PinCurrent(current.SegmentId, 10.3);
        var before = cache.GetSegment(current.SegmentId)!;
        // currentのdenseな3点がpeerの同じ1点へ近くても、対応は両側とも一対一に保つ。
        Assert.Equal(0, cache.ReverseWindowRoom(request with { TargetSeconds = 10.3 }, 10.3, 10.3, .2));
        Assert.False(cache.TryTrimReverseWindow(request with { TargetSeconds = 10.3 }, 10.3, 10.3, .2, 6));
        Assert.Equal(before.Frames, cache.GetSegment(current.SegmentId)!.Frames);
        Assert.NotNull(cache.GetSegment(previous.SegmentId));
    }
}

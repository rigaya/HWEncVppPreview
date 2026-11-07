using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class ReverseNeighborProtectionTests
{
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 60, FpsDen = 1 };
    private static PreviewRequest Request(double target) => new()
    {
        EncoderPath = "encoder", InputFile = "input", Fps = Header.Fps, TargetSeconds = target,
        Capabilities = new(true), TimestampOriginSeconds = 1000,
    };
    private static Y4mFrame Frame(double time) => new(Header, 1000 + time, 1 / Header.Fps, new byte[6]);

    [Fact]
    public void DirectFuturePeerSurvivesWhenTwoPointOverlapRequiresSkipping()
    {
        const double target = 302.368722;
        var request = Request(target);
        var cache = new FrameCache(88 * 6);
        var peer = cache.StoreSegment(request, Enumerable.Range(0, 42).Select(i => Frame(target + i / Header.Fps)).ToArray(),
            new(1000, 0, true), 302.369, 303.086105333333)!;
        var current = cache.StoreSegment(request, Enumerable.Range(0, 44).Select(i => Frame(target + (i - 38) / Header.Fps)).ToArray(),
            new(1000, 0, true), 301.735, target + 5 / Header.Fps, firstOutputInputSeconds: 301.7)!;
        cache.PinCurrent(current.SegmentId, target);
        Assert.True(cache.CanStitch(request));
        // 未来peer全体を空きへ加えると大窓になるが、次コマの先を失い順反転で再取得になる。
        Assert.Equal(5 * 6, cache.ReverseWindowRoom(request, 301.735, target, 4 / Header.Fps));
        Assert.False(cache.TryTrimReverseWindow(request, 301.735, target, 4 / Header.Fps, 6 * 6));
        Assert.NotNull(cache.GetSegment(peer.SegmentId));
        Assert.Equal(44, cache.GetSegment(current.SegmentId)!.Frames.Count);
        Assert.True(cache.TryGetAt(request, target + .2, out var forward));
        Assert.Equal(peer.SegmentId, forward.SegmentId);
        Assert.True(cache.SizeBytes <= cache.CapacityBytes);
    }

    [Fact]
    public void VfrDirectPeerKeepsTwoActualCommonPointsWhenTrimCannotFit()
    {
        var request = Request(10.4);
        var cache = new FrameCache(12 * 6);
        var peer = cache.StoreSegment(request, new[] { 11.3, 11.4, 11.5, 11.6, 11.7 }.Select(Frame).ToArray(),
            new(1000, 0, true), 11.3, 11.8)!;
        var current = cache.StoreSegment(request, new[] { 10, 10.2, 10.4, 11.3, 11.4 }.Select(Frame).ToArray(),
            new(1000, 0, true), 10, 11.5)!;
        cache.PinCurrent(current.SegmentId, 10.4);
        Assert.True(cache.CanStitch(request));
        Assert.Equal(2 * 6, cache.ReverseWindowRoom(request, 10, 10.4, .3));
        Assert.False(cache.TryTrimReverseWindow(request, 10, 10.4, .3, 3 * 6));
        Assert.NotNull(cache.GetSegment(peer.SegmentId));
        Assert.Contains(cache.GetSegment(current.SegmentId)!.Frames, f => Math.Abs(f.Xts!.Value - 1011.3) < 1e-8);
        Assert.True(cache.TryGetAt(request, 11.6, out _));
    }
    [Fact]
    public void SecondHopFutureCanBeReclaimedWhileTheDirectBridgeRemains()
    {
        var request = Request(10.4);
        var cache = new FrameCache(20 * 6);
        var future = cache.StoreSegment(request, new[] { 10.5, 10.6, 10.7, 10.8, 10.9 }.Select(Frame).ToArray(),
            new(1000, 0, true), 10.5, 11)!;
        var bridge = cache.StoreSegment(request, new[] { 10.3, 10.4, 10.5, 10.6 }.Select(Frame).ToArray(),
            new(1000, 0, true), 10.3, 10.7)!;
        var current = cache.StoreSegment(request, new[] { 10, 10.1, 10.2, 10.3, 10.4 }.Select(Frame).ToArray(),
            new(1000, 0, true), 10, 10.45)!;
        var distant = cache.StoreSegment(request, new[] { 40, 40.1, 40.2, 40.3 }.Select(Frame).ToArray(),
            new(1000, 0, true), 40, 40.4)!;
        cache.PinCurrent(current.SegmentId, 10.4);
        Assert.True(cache.CanStitch(request));
        Assert.Equal(11 * 6, cache.ReverseWindowRoom(request, 10, 10.4, .3));
        Assert.True(cache.TryTrimReverseWindow(request, 10, 10.4, .3, 6 * 6));
        Assert.Null(cache.GetSegment(future.SegmentId));
        Assert.NotNull(cache.GetSegment(bridge.SegmentId));
        Assert.NotNull(cache.GetSegment(distant.SegmentId));
        Assert.False(cache.TryGetAt(request, 10.8, out _));
    }
}

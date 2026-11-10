using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class OverlapValidationTests
{
    private const double Step = 1.0 / 24;
    private const double Offset = 0.00834;
    private static PreviewRequest Request(string input = "input") => new()
    { EncoderPath = "encoder", InputFile = input, Fps = 24, TimestampOriginSeconds = 1000, Capabilities = new(true), TargetSeconds = 10 };
    private static Y4mFrame Frame(double time, byte value = 0) => new(
        Y4mHeader.Parse("YUV4MPEG2 W2 H2 F24:1 C420"), 1000 + time, Step, Enumerable.Repeat(value, 6).ToArray());
    private static Y4mFrame[] Frames(double start, int count = 6, byte pixelOffset = 0)
        => Enumerable.Range(0, count).Select(i => Frame(start + i * Step, (byte)(i + pixelOffset))).ToArray();

    [Fact]
    public void PendingNewStreamIsNotMixedUntilThreeSafeFramesMatch()
    {
        var cache = new FrameCache();
        var old = cache.StoreSegment(Request(), Frames(10), new(1000, 7, true), 10, 11)!;
        long current = cache.BeginSegment(Request(), new(1000, 7, true), 10);
        cache.PinCurrent(current, 10);
        cache.Append(current, Frame(10 + Offset));
        Assert.False(cache.GetSegment(current)!.CanStitch);
        Assert.False(cache.TryGetAt(Request(), 10.1, out _));
        Assert.All(cache.GetNavigationFrames(Request()), frame => Assert.Equal(current, frame.SegmentId));
        cache.Append(current, Frame(10 + Offset + Step, 1));
        Assert.False(cache.GetSegment(current)!.CanStitch);
        cache.Append(current, Frame(10 + Offset + 2 * Step, 2));
        Assert.True(cache.GetSegment(current)!.CanStitch);
        Assert.True(cache.CanStitch(Request()));
        Assert.All(cache.GetNavigationFrames(Request()), frame => Assert.Equal(old.SegmentId, frame.SegmentId));
    }

    [Fact]
    public void PixelMismatchDropsOnlySameSourceAndKeepsAbsoluteClock()
    {
        var cache = new FrameCache();
        var old = cache.StoreSegment(Request(), Frames(10), new(1000, 7, true), 10, 11)!;
        var other = cache.StoreSegment(Request("other"), Frames(10), new(1000, 7, true), 10, 11)!;
        long current = cache.BeginSegment(Request(), new(1000, 7, true), 10);
        cache.PinCurrent(current, 10);
        foreach (var frame in Frames(10 + Offset, 3, 30)) cache.Append(current, frame);
        var segment = cache.GetSegment(current)!;
        Assert.False(segment.CanStitch);
        Assert.True(segment.Clock.TrustedAbsolute);
        Assert.Equal(10 + Offset, segment.Clock.ToInputSeconds(segment.Frames[0], 0, 24), 6);
        Assert.False(cache.CanStitch(Request()));
        Assert.Null(cache.GetSegment(old.SegmentId));
        Assert.NotNull(cache.GetSegment(other.SegmentId));
        Assert.False(cache.TryGetAt(Request(), 10.1, out _));
        Assert.All(cache.GetNavigationFrames(Request()), frame => Assert.Equal(current, frame.SegmentId));
    }

    [Fact]
    public void NoOverlapDoesNotComparePixels()
    {
        var cache = new FrameCache();
        var old = cache.StoreSegment(Request(), Frames(10), new(1000, 7, true), 10, 11)!;
        var newer = cache.StoreSegment(Request(), Frames(20, pixelOffset: 90), new(1000, 17, true), 20, 21)!;
        Assert.True(newer.CanStitch);
        Assert.True(cache.CanStitch(Request()));
        Assert.NotNull(cache.GetSegment(old.SegmentId));
    }

    [Fact]
    public void LaterOverlapReturnsToPendingBeforeItCanJoin()
    {
        var cache = new FrameCache();
        var old = cache.StoreSegment(Request(), Frames(20), new(1000, 17, true), 20, 21)!;
        long current = cache.BeginSegment(Request(), new(1000, 7, true), 10);
        cache.PinCurrent(current, 10);
        cache.Append(current, Frame(10, 60));
        Assert.True(cache.GetSegment(current)!.CanStitch);
        cache.Append(current, Frame(20 + Offset));
        Assert.False(cache.GetSegment(current)!.CanStitch);
        Assert.All(cache.GetNavigationFrames(Request()), frame => Assert.Equal(current, frame.SegmentId));
        cache.Append(current, Frame(20 + Offset + Step, 1));
        cache.Append(current, Frame(20 + Offset + 2 * Step, 2));
        Assert.True(cache.GetSegment(current)!.CanStitch);
        Assert.NotNull(cache.GetSegment(old.SegmentId));
    }

    [Fact]
    public void NaturalEndCanValidateTwoFramesButSingleOverlapFallsBack()
    {
        var cache = new FrameCache();
        cache.StoreSegment(Request(), Frames(10), new(1000, 7, true), 10, 11);
        long current = cache.BeginSegment(Request(), new(1000, 7, true), 10);
        cache.PinCurrent(current, 10);
        cache.Append(current, Frame(10 + Offset));
        cache.Append(current, Frame(10 + Offset + Step, 1));
        Assert.False(cache.GetSegment(current)!.CanStitch);
        cache.CompleteSegment(current, true);
        Assert.True(cache.GetSegment(current)!.CanStitch);
        var one = cache.StoreSegment(Request(), new[] { Frame(10 + Offset) }, new(1000, 7, true), 10, 10.04, true)!;
        Assert.False(one.CanStitch);
        Assert.True(one.Clock.TrustedAbsolute);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void EveryOverlappingPeerIsChecked()
    {
        var cache = new FrameCache();
        var old = cache.StoreSegment(Request(), Frames(10), new(1000, 7, true), 10, 11)!;
        var peerFrames = Frames(10);
        // 最初の0/3/5点では見えなかった別peerの差を、新しい2点の照合で検出する。
        peerFrames[1].Data[0] = 99;
        var peer = cache.StoreSegment(Request(), peerFrames, new(1000, 7, true), 10, 11)!;
        var newest = cache.StoreSegment(Request(), Frames(10, 2), new(1000, 7, true), 10, 10 + 2 * Step)!;
        Assert.False(newest.CanStitch);
        Assert.Null(cache.GetSegment(old.SegmentId));
        Assert.Null(cache.GetSegment(peer.SegmentId));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void OlderActiveGrowthRevalidatesNewerActiveOverlap()
    {
        var cache = new FrameCache();
        long older = cache.BeginSegment(Request(), new(1000, 7, true), 10);
        cache.Append(older, Frame(10, 60));
        long newer = cache.BeginSegment(Request(), new(1000, 17, true), 20);
        cache.PinCurrent(newer, 20);
        foreach (var frame in Frames(20, 3)) cache.Append(newer, frame);
        Assert.True(cache.GetSegment(newer)!.CanStitch);
        cache.Append(older, Frame(20 + Offset));
        Assert.False(cache.GetSegment(newer)!.CanStitch);
        Assert.All(cache.GetNavigationFrames(Request()), frame => Assert.Equal(newer, frame.SegmentId));
        cache.Append(older, Frame(20 + Offset + Step, 1));
        cache.Append(older, Frame(20 + Offset + 2 * Step, 2));
        Assert.True(cache.GetSegment(newer)!.CanStitch);
    }

    [Fact]
    public void OlderLateMismatchKeepsLatestRequestInsteadOfInspectedMiddleRequest()
    {
        var cache = new FrameCache();
        long older = cache.BeginSegment(Request(), new(1000, 7, true), 10);
        cache.Append(older, Frame(10));
        long middle = cache.BeginSegment(Request(), new(1000, 17, true), 20);
        foreach (var frame in Frames(20)) cache.Append(middle, frame);
        var latest = cache.StoreSegment(Request(), Frames(30), new(1000, 27, true), 30, 31)!;
        cache.PinCurrent(latest.SegmentId, 30);
        // olderが伸びて中間区間との3点の画素不一致が判明しても、disjointな最新要求を残す。
        Assert.True(cache.Append(older, Frame(20 + Offset, 99)));
        Assert.True(cache.Append(older, Frame(20 + Offset + Step, 99)));
        Assert.False(cache.Append(older, Frame(20 + Offset + 2 * Step, 99)));
        Assert.Null(cache.GetSegment(older));
        Assert.Null(cache.GetSegment(middle));
        var kept = cache.GetSegment(latest.SegmentId)!;
        Assert.False(kept.CanStitch);
        Assert.True(kept.Clock.TrustedAbsolute);
        Assert.Equal(30, kept.Clock.ToInputSeconds(kept.Frames[0], 0, 24));
        Assert.Equal(1, cache.Count);
        Assert.All(cache.GetNavigationFrames(Request()), frame => Assert.Equal(latest.SegmentId, frame.SegmentId));
    }

    [Fact]
    public void PublicEquivalenceAndMergeCannotMixPendingSegments()
    {
        var request = Request();
        var first = new PreviewSegmentSnapshot(1, request, new(1000, 7, true), Frames(10), 10, 11, false, false);
        var pending = new PreviewSegmentSnapshot(2, request, new(1000, 7, true), Frames(10 + Offset), 10, 11, true, false)
            { CanStitch = false };
        Assert.False(FrameSelector.AreEquivalent(new(first, 0), new(pending, 0)));
        var view = FrameSelector.MergeNavigationFrames(new[] { new PreviewFrameReference(first, 0), new(pending, 0) });
        Assert.Equal(1, Assert.Single(view).SegmentId);
    }
}

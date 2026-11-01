using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class NavigationFrameViewTests
{
    private const double Duration = 1001.0 / 24000;
    private const double Offset = 0.00834;
    private static PreviewRequest Request() => new()
    {
        EncoderPath = "encoder", InputFile = "input", Fps = 24000.0 / 1001,
        Capabilities = new(true), TimestampOriginSeconds = 1000,
    };
    private static Y4mFrame Frame(double time, byte pixel = 0, double? duration = Duration, bool headerFps = true)
        => new(headerFps ? Y4mHeader.Parse("YUV4MPEG2 W2 H2 F24000:1001 C420") : new() { Width = 2, Height = 2 },
            1000 + time, duration, Enumerable.Repeat(pixel, 6).ToArray());
    private static PreviewSegmentSnapshot Segment(long id, IReadOnlyList<Y4mFrame> frames, double start = -1, double end = 100)
        => new(id, Request(), new(1000, 0, true), frames, start, end, false, false);

    [Fact]
    public void GridOffsetMergesForwardAndReverseWithoutChangingRawTimestamps()
    {
        var cache = new FrameCache();
        var first = Enumerable.Range(0, 5).Select(i => Frame(10 + i * Duration, (byte)i)).ToArray();
        var second = Enumerable.Range(0, 5).Select(i => Frame(10 + Offset + i * Duration, (byte)i)).ToArray();
        var a = cache.StoreSegment(Request(), first, new(1000, 0, true), 10, 11)!;
        cache.StoreSegment(Request(), second, new(1000, 0, true), 10, 11);
        var view = cache.GetNavigationFrames(Request());
        Assert.Equal(5, view.Count);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, view.Select(frame => frame.Frame.Data[0]));
        Assert.Equal(new byte[] { 4, 3, 2, 1, 0 }, view.Reverse().Select(frame => frame.Frame.Data[0]));
        Assert.All(view, frame => Assert.Equal(a.SegmentId, frame.SegmentId));
        Assert.Same(first[0], view[0].Frame);
        Assert.Equal(1010 + Offset, second[0].Xts);
        Assert.Equal(0, view[0].FrameIndex);
    }

    [Fact]
    public void SafeSideWinsOverEarlierWarmupWithOneDifferentFrame()
    {
        var cache = new FrameCache();
        var warm = new[] { Frame(10, 255), Frame(10 + Duration, 1), Frame(10 + 2 * Duration, 2) };
        cache.StoreSegment(Request(), warm, new(1000, 0, true), 10 + Duration, 11);
        var safe = new[] { Frame(10 + Offset, 0), Frame(10 + Offset + Duration, 1), Frame(10 + Offset + 2 * Duration, 2) };
        var safeSegment = cache.StoreSegment(Request(), safe, new(1000, 0, true), 10, 11)!;
        var view = cache.GetNavigationFrames(Request());
        Assert.Equal(3, view.Count);
        Assert.Equal(safeSegment.SegmentId, view[0].SegmentId);
        Assert.Same(safe[0], view[0].Frame);
        Assert.DoesNotContain(view, frame => frame.Frame.Data[0] == 255);
        Assert.All(view, frame => Assert.True(frame.IsSafe));
    }

    [Fact]
    public void SameSegmentCloseVfrFramesRemainDistinctAcrossOverlaps()
    {
        var a = Segment(1, new[] { Frame(0, 0), Frame(0.01, 1) });
        var b = Segment(2, new[] { Frame(Offset, 0), Frame(0.01 + Offset, 1) });
        Assert.False(FrameSelector.AreEquivalent(new(a, 0), new(a, 1)));
        var view = FrameSelector.MergeNavigationFrames(new[]
        {
            new PreviewFrameReference(b, 1), new(a, 1), new(b, 0), new(a, 0),
        });
        Assert.Equal(2, view.Count);
        Assert.Equal(new byte[] { 0, 1 }, view.Select(frame => frame.Frame.Data[0]));
    }

    [Fact]
    public void ProximityChainDoesNotMergeEndpointsTransitively()
    {
        var a = new PreviewFrameReference(Segment(1, new[] { Frame(0) }), 0);
        var b = new PreviewFrameReference(Segment(2, new[] { Frame(0.015) }), 0);
        var c = new PreviewFrameReference(Segment(3, new[] { Frame(0.030) }), 0);
        Assert.True(FrameSelector.AreEquivalent(a, b));
        Assert.True(FrameSelector.AreEquivalent(b, c));
        Assert.False(FrameSelector.AreEquivalent(a, c));
        var view = FrameSelector.MergeNavigationFrames(new[] { c, b, a });
        Assert.Equal(2, view.Count);
        Assert.Same(a.Frame, view[0].Frame);
        Assert.Same(c.Frame, view[1].Frame);
    }

    [Fact]
    public void RepresentativeShiftCannotCrossDenseVfrNeighbors()
    {
        var old = Segment(1, new[] { Frame(1.008, 2) });
        var dense = Segment(2, new[] { Frame(1.000, 0), Frame(1.004, 1), Frame(1.008, 2) });
        var view = FrameSelector.MergeNavigationFrames(new[]
        {
            new PreviewFrameReference(old, 0), new(dense, 0), new(dense, 1), new(dense, 2),
        });
        Assert.Equal(3, view.Count);
        Assert.Equal(new byte[] { 0, 1, 2 }, view.Select(frame => frame.Frame.Data[0]));
        Assert.Equal(new byte[] { 2, 1, 0 }, view.Reverse().Select(frame => frame.Frame.Data[0]));
        Assert.All(view, frame => Assert.False(frame.Segment.CanStitch));
        Assert.All(view, frame => Assert.Equal(dense.SegmentId, frame.SegmentId));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void EquivalenceUsesDurationThenHeaderThenRequestRate(bool explicitDuration, bool headerFps)
    {
        double? duration = explicitDuration ? Duration : null;
        var a = new PreviewFrameReference(Segment(1, new[] { Frame(0, duration: duration, headerFps: headerFps) }), 0);
        var b = new PreviewFrameReference(Segment(2, new[] { Frame(Offset, duration: duration, headerFps: headerFps) }), 0);
        Assert.True(FrameSelector.AreEquivalent(a, b));
    }

    [Fact]
    public void DifferentRatesUseNarrowerHalfDuration()
    {
        var slow = new PreviewFrameReference(Segment(1, new[] { Frame(0) }), 0);
        var fast = new PreviewFrameReference(Segment(2, new[] { Frame(0.012, duration: 1.0 / 60) }), 0);
        Assert.False(FrameSelector.AreEquivalent(slow, fast));
    }

    [Fact]
    public void DifferentImageSourcesAndUntrustedClocksAreNeverCombined()
    {
        var cache = new FrameCache();
        cache.StoreSegment(Request(), new[] { Frame(0) }, new(1000, 0, true), 0, 1);
        cache.StoreSegment(Request() with { VppArgs = new[] { "different" } }, new[] { Frame(Offset) }, new(1000, 0, true), 0, 1);
        cache.StoreSegment(Request(), new[] { Frame(Offset) }, new(1000, 0, false), 0, 1);
        Assert.Single(cache.GetNavigationFrames(Request()));
        var a = new PreviewFrameReference(Segment(10, new[] { Frame(0) }), 0);
        var b = new PreviewFrameReference(Segment(11, new[] { Frame(Offset) }) with
            { PhysicalRequest = Request() with { InputFile = "other" } }, 0);
        Assert.False(FrameSelector.AreEquivalent(a, b));
    }

    [Fact]
    public void RollingIndexChangesDoNotChangeFrameIdentity()
    {
        var frame = Frame(1);
        var a = Segment(1, new[] { Frame(0), frame });
        var b = a with { Frames = new[] { frame } };
        Assert.True(FrameSelector.AreEquivalent(new(a, 1), new(b, 0)));
        Assert.False(FrameSelector.AreEquivalent(new(a, 0), new(b, 0)));
    }
}

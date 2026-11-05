using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class ActiveSegmentPrefixRetentionTests
{
    private static PreviewRequest Request() => new()
    {
        EncoderPath = "encoder", InputFile = "input", Fps = 2, TargetSeconds = 0,
        Capabilities = new(true, true), TimestampOriginSeconds = 1000,
    };

    private static Y4mFrame Frame(double seconds, bool timestamp = true) => new(
        Y4mHeader.Parse("YUV4MPEG2 W2 H2 F2:1 C420"), timestamp ? 1000 + seconds : null, 0.5, new byte[6]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinAndAppendKeepOlderThanTwoSecondsWhenBudgetAllows(bool appendWhilePinned)
    {
        var cache = new FrameCache(1024);
        long id = cache.BeginSegment(Request(), new(1000, 0, true), 0);
        if (appendWhilePinned) cache.PinCurrent(id, 5);
        for (int i = 0; i <= 12; ++i) Assert.True(cache.Append(id, Frame(i * 0.5)));
        if (!appendWhilePinned) cache.PinCurrent(id, 5);
        var segment = cache.GetSegment(id)!;
        Assert.Equal(13, segment.Frames.Count);
        Assert.Equal(1000d, segment.Frames[0].Xts);
        Assert.Equal(0, segment.SafeStartSeconds);
        Assert.Equal(78, cache.SizeBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapacityReclaimsOwnPrefixAndPreservesCurrentAndFuture(bool timestamps)
    {
        var cache = new FrameCache(30);
        long id = cache.BeginSegment(Request(), timestamps ? new(1000, 0, true) : new(null, 0, false), 0);
        for (int i = 0; i < 5; ++i) Assert.True(cache.Append(id, Frame(i * 0.5, timestamps)));
        cache.PinCurrent(id, 1.5);
        var current = cache.GetSegment(id)!.Frames[3];
        var future = cache.GetSegment(id)!.Frames[4];
        Assert.True(cache.CanAppend(id, 6));
        Assert.True(cache.Append(id, Frame(2.5, timestamps)));
        var segment = cache.GetSegment(id)!;
        Assert.Equal(5, segment.Frames.Count);
        int selected = FrameSelector.Select(segment, 1.5, 2);
        Assert.Same(current, segment.Frames[selected]);
        Assert.Contains(future, segment.Frames);
        Assert.Equal(1.5, segment.Clock.ToInputSeconds(segment.Frames[selected], selected, 2));
        Assert.Equal(0.5, segment.Clock.ToInputSeconds(segment.Frames[0], 0, 2));
        Assert.Equal(30, cache.SizeBytes);
    }

    [Fact]
    public void CapacityDoesNotDiscardCurrentOrUnreadFuture()
    {
        var cache = new FrameCache(18);
        long id = cache.BeginSegment(Request(), new(1000, 0, true), 0);
        cache.PinCurrent(id, 0);
        for (int i = 0; i < 3; ++i) Assert.True(cache.Append(id, Frame(i * 0.5)));
        Assert.False(cache.CanAppend(id, 6));
        Assert.False(cache.Append(id, Frame(1.5)));
        Assert.Equal(new[] { 1000d, 1000.5, 1001 }, cache.GetSegment(id)!.Frames.Select(frame => frame.Xts!.Value));
    }

    [Fact]
    public void OtherPinnedSegmentDoesNotAuthorizeTrimmingActiveFrames()
    {
        var cache = new FrameCache(12);
        long pinned = cache.BeginSegment(Request(), new(1000, 0, true), 0);
        Assert.True(cache.Append(pinned, Frame(0)));
        cache.PinCurrent(pinned, 0);
        long active = cache.BeginSegment(Request(), new(1000, 0, true), 0);
        Assert.True(cache.Append(active, Frame(1)));
        Assert.False(cache.CanAppend(active, 6));
        Assert.Single(cache.GetSegment(pinned)!.Frames);
        Assert.Single(cache.GetSegment(active)!.Frames);
    }
}

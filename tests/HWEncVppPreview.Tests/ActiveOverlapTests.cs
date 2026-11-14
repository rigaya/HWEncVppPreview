using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class ActiveOverlapTests
{
    private const double Step = 1.0 / 24;
    private static PreviewRequest Request(long id) => new()
    { EncoderPath = "encoder", InputFile = "input.ts", Fps = 24, TimestampOriginSeconds = 1000,
        Capabilities = new(true), TargetSeconds = 10, CorrelationId = id };
    private static Y4mFrame Frame(int index, byte offset = 0) => new(
        Y4mHeader.Parse("YUV4MPEG2 W2 H2 F24:1 C420"), 1010 + index * Step, Step,
        Enumerable.Repeat((byte)(index + offset), 6).ToArray());
    private static Y4mFrame[] Frames(byte offset = 0) => Enumerable.Range(0, 6).Select(i => Frame(i, offset)).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 先に始めたstreamより大きいIdの完了区間が入っても受信を継続して接続する(bool appendFirst)
    {
        var cache = new FrameCache();
        long stream = cache.BeginSegment(Request(15), new(1000, 9, true), 10);
        cache.PinCurrent(stream, 10);
        if (appendFirst) Assert.True(cache.Append(stream, Frame(0)));
        var closed = cache.StoreSegment(Request(-15), Frames(), new(1000, 9, true), 10, 10 + 6 * Step);
        Assert.NotNull(closed);
        Assert.True(closed.SegmentId > stream);
        if (!appendFirst) Assert.True(cache.Append(stream, Frame(0)));
        Assert.True(cache.GetSegment(stream)!.IsActive);
        Assert.False(cache.GetSegment(closed.SegmentId)!.CanStitch);
        Assert.True(cache.CanStitch(Request(15)));
        Assert.True(cache.Append(stream, Frame(1)));
        Assert.True(cache.Append(stream, Frame(2)));
        Assert.True(cache.GetSegment(stream)!.CanStitch);
        Assert.True(cache.GetSegment(closed.SegmentId)!.CanStitch);
        Assert.True(cache.Append(stream, Frame(3)));
        Assert.Equal(4, cache.GetSegment(stream)!.Frames.Count);
    }

    [Fact]
    public void 受信中のpeerが一点で完了したときは保留を最終判定する()
    {
        var cache = new FrameCache();
        long stream = cache.BeginSegment(Request(15), new(1000, 9, true), 10);
        var closed = cache.StoreSegment(Request(-15), Frames(), new(1000, 9, true), 10, 10 + 6 * Step)!;
        Assert.True(cache.Append(stream, Frame(0)));
        Assert.True(cache.CanStitch(Request(15)));
        cache.CompleteSegment(stream, true);
        Assert.False(cache.CanStitch(Request(15)));
        Assert.NotNull(cache.GetSegment(closed.SegmentId));
    }

    [Fact]
    public void 実画素不一致でも受信中の区間は破棄せず接続だけを止める()
    {
        var cache = new FrameCache();
        long stream = cache.BeginSegment(Request(15), new(1000, 9, true), 10);
        cache.PinCurrent(stream, 10);
        var closed = cache.StoreSegment(Request(-15), Frames(90), new(1000, 9, true), 10, 10 + 6 * Step)!;
        Assert.True(cache.Append(stream, Frame(0)));
        Assert.True(cache.Append(stream, Frame(1)));
        Assert.False(cache.CanStitch(Request(15)));
        Assert.True(cache.GetSegment(stream)!.IsActive);
        Assert.False(cache.GetSegment(stream)!.CanStitch);
        Assert.False(cache.GetSegment(closed.SegmentId)!.CanStitch);
        Assert.True(cache.Append(stream, Frame(2)));
        Assert.All(cache.GetNavigationFrames(Request(15)), frame => Assert.Equal(stream, frame.SegmentId));
        cache.CompleteSegment(stream, false);
        Assert.Null(cache.GetSegment(stream));
        Assert.NotNull(cache.GetSegment(closed.SegmentId));
    }
}

using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class AtomicSegmentPinTests
{
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
    private static PreviewRequest Request(double time = 0, bool before = false) => new()
    {
        EncoderPath = "fake", InputFile = "input", Fps = 30, TargetSeconds = time,
        Capabilities = new(true), TimestampOriginSeconds = 1000,
        VppArgs = before ? Array.Empty<string>() : new[] { "--vpp-tweak" },
    };
    private static Y4mFrame Frame(double time) => new(Header, 1000 + time, 1d / 30, new byte[Header.FrameBytes]);

    [Fact]
    public void BackgroundStoreCannotEvictNewForegroundBetweenStoreAndPin()
    {
        var cache = new FrameCache(Header.FrameBytes * 2L);
        var old = cache.StoreSegment(Request(), new[] { Frame(0) }, new(1000, 0, true), 0, 1d / 30)!;
        cache.PinCurrent(old.SegmentId, 0);
        var request = Request(1);
        // 保存APIから戻った直後にBeforeが割り込んでも、新Afterのpinは既に確定している。
        var after = cache.StoreSegment(request, new[] { Frame(1), Frame(1 + 1d / 30) }, new(1000, 0, true), 1, 1.1,
            pinCurrent: true)!;
        var before = cache.StoreSegment(Request(1, before: true), new[] { Frame(1) }, new(1000, 0, true), 1, 1.1);
        Assert.NotNull(cache.GetSegment(after.SegmentId));
        Assert.Null(before);
        Assert.Equal(Header.FrameBytes * 2L, cache.SizeBytes);
    }
    [Fact]
    public void FailedPinnedStoreLeavesOldPinClearedAsBefore()
    {
        var cache = new FrameCache(Header.FrameBytes);
        var old = cache.StoreSegment(Request(), new[] { Frame(0) }, new(1000, 0, true), 0, 0.1)!;
        cache.PinCurrent(old.SegmentId, 0);
        Assert.Null(cache.StoreSegment(Request(1), new[] { Frame(1), Frame(1 + 1d / 30) },
            new(1000, 0, true), 1, 1.1, pinCurrent: true));
        var before = cache.StoreSegment(Request(1, before: true), new[] { Frame(1) }, new(1000, 0, true), 1, 1.1);
        Assert.NotNull(before);
        Assert.Null(cache.GetSegment(old.SegmentId));
    }

    [Fact]
    public void UnpinnedBackgroundDoesNotTakeTheForegroundPin()
    {
        var cache = new FrameCache(Header.FrameBytes * 3L);
        var after = cache.StoreSegment(Request(), new[] { Frame(0) }, new(1000, 0, true), 0, 0.1, pinCurrent: true)!;
        var before = cache.StoreSegment(Request(0, before: true), new[] { Frame(0) }, new(1000, 0, true), 0, 0.1)!;
        cache.Store(Request() with { VppArgs = new[] { "unrelated" } }, new[] { Frame(2), Frame(3) });
        Assert.NotNull(cache.GetSegment(after.SegmentId));
        Assert.Null(cache.GetSegment(before.SegmentId));
    }

    [Fact]
    public void PinTransitionDoesNotRemoveIndependentActiveProtection()
    {
        var cache = new FrameCache(Header.FrameBytes * 2L);
        long active = cache.BeginSegment(Request(), new(1000, 0, true), 0);
        Assert.True(cache.Append(active, Frame(0)));
        cache.PinCurrent(active, 0);
        Assert.Null(cache.StoreSegment(Request(1), new[] { Frame(1), Frame(1 + 1d / 30) },
            new(1000, 0, true), 1, 1.1, pinCurrent: true));
        Assert.NotNull(cache.GetSegment(active));
        Assert.Equal(Header.FrameBytes, cache.SizeBytes);
    }


}

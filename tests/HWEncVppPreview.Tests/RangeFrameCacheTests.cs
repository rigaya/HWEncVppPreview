using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class RangeFrameCacheTests
{
    private static PreviewRequest Request(double target = 5) => new()
    {
        EncoderPath = "encoder", InputFile = "input", Fps = 2, TargetSeconds = target,
        Capabilities = new(true, true), TimestampOriginSeconds = 1000,
    };
    private static Y4mFrame Frame(double time) => new(new Y4mHeader { Width = 2, Height = 2 }, 1000 + time, 0.5, new byte[6]);

    [Fact]
    public void RangeReusePreservesRawClockAndRejectsUnsafeEdges()
    {
        var cache = new FrameCache(1024);
        var request = Request();
        var frames = Enumerable.Range(6, 9).Select(i => Frame(i * 0.5)).ToArray();
        var segment = cache.StoreSegment(request, frames, new(1000, 3, true), 5, 6.5)!;
        Assert.True(cache.TryGetAt(Request(6), 6, out var found));
        Assert.Equal(segment.SegmentId, found.SegmentId);
        int selected = FrameSelector.Select(found, 6, 2);
        Assert.Equal(1006, found.Frames[selected].Xts);
        Assert.Equal(6, found.Clock.ToInputSeconds(found.Frames[selected], selected, 2));
        Assert.False(cache.TryGetAt(request, 4.99, out _));
        Assert.False(cache.TryGetAt(request, 6.51, out _));
        Assert.False(cache.TryGetAt(request with { TimestampOriginSeconds = 0 }, 6, out _));
        Assert.False(cache.TryGetAt(request with { VppArgs = new[] { "changed" } }, 6, out _));
    }

    [Fact]
    public void UntrustedClockNeverStitchesAcrossRequests()
    {
        var cache = new FrameCache();
        cache.StoreSegment(Request(), new[] { Frame(5) }, new(1000, 3, false), 3, 10);
        Assert.False(cache.TryGetAt(Request(), 5, out _));
    }

    [Fact]
    public void RollingActivePinReclaimsPrefixAtFrameGranularityOnlyForBudget()
    {
        var cache = new FrameCache(60);
        long id = cache.BeginSegment(Request(0), new(1000, 0, true), 0);
        for (int i = 0; i < 100; ++i)
        {
            cache.PinCurrent(id, i * 0.5);
            Assert.True(cache.Append(id, Frame(i * 0.5)));
            Assert.InRange(cache.SizeBytes, 0, 60);
        }
        var segment = cache.GetSegment(id)!;
        Assert.True(segment.IsActive);
        Assert.Equal(1049.5, segment.Frames[^1].Xts);
        // 固定2秒ではなく60bytesの予算（6bytes×10枚）まで過去を保持する。
        Assert.Equal(10, segment.Frames.Count);
        Assert.Equal(45, segment.SafeStartSeconds);
    }

    [Fact]
    public void BudgetNeverDropsUnreadFutureOrCurrentFrame()
    {
        var cache = new FrameCache(18);
        long id = cache.BeginSegment(Request(0), new(1000, 0, true), 0);
        cache.PinCurrent(id, 0);
        Assert.True(cache.Append(id, Frame(0)));
        Assert.True(cache.Append(id, Frame(0.5)));
        Assert.True(cache.Append(id, Frame(1)));
        Assert.False(cache.CanAppend(id, 6));
        Assert.Equal(new[] { 1000d, 1000.5, 1001 }, cache.GetSegment(id)!.Frames.Select(f => f.Xts!.Value));
        cache.PinCurrent(id, 0.5);
        Assert.True(cache.CanAppend(id, 6));
        Assert.True(cache.Append(id, Frame(1.5)));
        Assert.Equal(18, cache.SizeBytes);
        Assert.Equal(1000.5, cache.GetSegment(id)!.Frames[0].Xts);
    }

    [Fact]
    public void MissingTimestampsKeepTheirPositionAfterPrefixRemoval()
    {
        var cache = new FrameCache(24);
        long id = cache.BeginSegment(Request(0), new(null, 0, false), 0);
        for (int i = 0; i < 20; ++i)
        {
            cache.PinCurrent(id, i * 0.5);
            Assert.True(cache.Append(id, new(new Y4mHeader { Width = 2, Height = 2 }, null, 0.5, new byte[6])));
        }
        var segment = cache.GetSegment(id)!;
        int selected = FrameSelector.Select(segment, 9.5, 2);
        Assert.True(selected >= 0);
        Assert.Equal(9.5, segment.Clock.ToInputSeconds(segment.Frames[selected], selected, 2));
    }

    [Fact]
    public void RangeWithoutSelectableFrameIsNotAHitAndOlderValidRangeRemainsSearchable()
    {
        var cache = new FrameCache();
        var request = Request();
        cache.StoreSegment(request, new[] { Frame(6), Frame(6.5), Frame(7) }, new(1000, 3, true), 5, 8);
        Assert.False(cache.TryGetAt(request, 5, out _));
        Assert.True(cache.TryGetAt(request, 6, out _));
        var valid = cache.StoreSegment(request, new[] { Frame(5), Frame(6), Frame(6.5), Frame(7) }, new(1000, 3, true), 5, 8)!;
        var newestInvalid = cache.StoreSegment(request, new[] { Frame(6), Frame(6.5), Frame(7) }, new(1000, 3, true), 5, 8)!;
        // 無効候補を最新にしても、その1件で探索を打ち切らない。
        Assert.True(cache.TryGetAt(request, 7, out var recent));
        Assert.Equal(newestInvalid.SegmentId, recent.SegmentId);
        Assert.True(cache.TryGetAt(request, 5, out var selected));
        Assert.Equal(valid.SegmentId, selected.SegmentId);
    }

    [Fact]
    public void FiniteTailGuardDoesNotApplyToNaturalInputEnd()
    {
        var cache = new FrameCache();
        long id = cache.BeginSegment(Request(), new(1000, 0, true), 0);
        cache.Append(id, Frame(5));
        cache.CompleteSegment(id, false, 0.5);
        Assert.Equal(5, cache.GetSegment(id)!.SafeEndSeconds);
        long end = cache.BeginSegment(Request(), new(1000, 0, true), 0);
        cache.Append(end, Frame(5));
        cache.CompleteSegment(end, true, 0.5);
        Assert.Equal(5.5, cache.GetSegment(end)!.SafeEndSeconds);
        Assert.True(cache.GetSegment(end)!.ReachedInputEnd);
    }
}

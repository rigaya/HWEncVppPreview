using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class ReverseAdmissionRecoveryTests
{
    private static readonly Y4mHeader Header = new() { Width = 1440, Height = 1080, FpsNum = 30000, FpsDen = 1001 };
    // 同じ配列を共有し、実1080のbyte単価と窓を大きなテストメモリなしで検証する。
    private static readonly byte[] Pixels = new byte[Header.FrameBytes];
    private static PreviewRequest Request() => new()
    {
        EncoderPath = "stub", InputFile = "input", Fps = Header.Fps, TargetSeconds = 300.1,
        Capabilities = new(true), TimestampOriginSeconds = 1000, InputFormatHint = Header,
    };
    private static Y4mFrame[] Frames(double start, int count) => Enumerable.Range(0, count)
        .Select(i => new Y4mFrame(Header, 1000 + start + i / Header.Fps, 1 / Header.Fps, Pixels)).ToArray();
    private static (FrameCache Cache, long Current, long[] Old) Full()
    {
        var cache = new FrameCache(1L << 30);
        var request = Request();
        var old = Enumerable.Range(0, 9).Select(i => cache.StoreSegment(request, Frames(320 + i * 2, 46),
            new(1000, 0, true), 320 + i * 2, 320 + i * 2 + 45 / Header.Fps)!.SegmentId).ToArray();
        var current = cache.StoreSegment(request, Frames(300, 45), new(1000, 0, true), 300, 300 + 44 / Header.Fps)!;
        cache.PinCurrent(current.SegmentId, request.TargetSeconds);
        return (cache, current.SegmentId, old);
    }

    [Fact]
    public void RoomCountsEvictableClosedSegmentsWithoutChangingCache()
    {
        var (cache, current, _) = Full();
        long size = cache.SizeBytes;
        var frames = cache.GetSegment(current)!.Frames;
        Assert.True(cache.ReverseWindowRoom(Request(), 300, 300.1, .5) >= 1L << 29);
        Assert.Equal(size, cache.SizeBytes);
        Assert.Equal(frames, cache.GetSegment(current)!.Frames);
    }

    [Fact]
    public void AdmissionReclaimsOldSegmentsBeforeTrimmingCurrentBridge()
    {
        var (cache, current, old) = Full();
        var before = cache.GetSegment(current)!;
        Assert.True(cache.TryTrimReverseWindow(Request(), 300, 300.1, .5, 1L << 29));
        Assert.Equal(before.Frames, cache.GetSegment(current)!.Frames);
        Assert.Contains(old, id => cache.GetSegment(id) == null);
        Assert.True(cache.SizeBytes <= cache.CapacityBytes - (1L << 29));
    }

    [Fact]
    public async Task FullHdPrefetchStillStartsWithTheOriginal181InputFrameWindow()
    {
        var (cache, _, _) = Full();
        var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        var request = Request();
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.NotifyNavigation(request with { TargetSeconds = 300.08 }, -1);
        await controller.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var run = Assert.Single(backend.Requests);
        Assert.Equal(5, run.PreMarginSeconds);
        Assert.Equal(1, run.PostMarginSeconds);
        Assert.Equal(181, CommandBuilder.Plan(run).FrameCount);
        Assert.Equal(295, CommandBuilder.Plan(run).SeekSeconds);
    }

    [Fact]
    public void ReverseStoreReclaimsAnOldClosedSegmentAfterAdmission()
    {
        var (cache, current, _) = Full();
        var stored = cache.StoreSegment(Request(), Frames(297, 106), new(1000, 0, true), 297, 300.5,
            reverseWindow: true);
        Assert.NotNull(stored);
        Assert.True(stored.CanStitch);
        Assert.NotNull(cache.GetSegment(current));
        Assert.True(cache.CanStitch(Request()));
        Assert.True(cache.SizeBytes <= cache.CapacityBytes);
    }

    [Fact]
    public void RepeatedReverseWindowsDoNotAccumulateProtectedFutureClosedFragments()
    {
        var (cache, current, _) = Full();
        double boundary = 300;
        for (int i = 0; i < 20; i++)
        {
            var request = Request() with { TargetSeconds = boundary + .1 };
            cache.PinCurrent(current, request.TargetSeconds);
            Assert.True(cache.ReverseWindowRoom(request, boundary, request.TargetSeconds, .5) >= 1L << 29);
            Assert.True(cache.TryTrimReverseWindow(request, boundary, request.TargetSeconds, .5, 1L << 29));
            double start = boundary - 90 / Header.Fps;
            var stored = cache.StoreSegment(request with { TargetSeconds = boundary }, Frames(start, 106),
                new(1000, 0, true), start, start + 105 / Header.Fps, reverseWindow: true);
            Assert.NotNull(stored);
            Assert.True(stored.CanStitch);
            Assert.True(cache.SizeBytes <= cache.CapacityBytes);
            current = stored.SegmentId;
            boundary = start;
        }
    }

    [Fact]
    public void MultiEntryBridgeIsProtectedWhileAnUnrelatedClosedEntryIsReclaimed()
    {
        var cache = new FrameCache(Header.FrameBytes * 16L);
        var request = Request() with { TargetSeconds = 300 + 6 / Header.Fps };
        var obsolete = cache.StoreSegment(request, Frames(400, 6), new(1000, 0, true), 400, 401)!;
        var left = cache.StoreSegment(request, Frames(300, 5), new(1000, 0, true), 300, 300 + 4 / Header.Fps)!;
        double rightStart = 300 + 3 / Header.Fps;
        var right = cache.StoreSegment(request, Frames(rightStart, 5), new(1000, 0, true), rightStart, rightStart + 4 / Header.Fps)!;
        cache.PinCurrent(right.SegmentId, request.TargetSeconds);
        Assert.True(cache.CanStitch(request));
        // current単独でbridgeをtrimできなくても、接続に要らないclosedだけは回収できる。
        Assert.Equal(Header.FrameBytes * 6L, cache.ReverseWindowRoom(request, 300, request.TargetSeconds, .1));
        Assert.False(cache.TryTrimReverseWindow(request, 300, request.TargetSeconds, .1, Header.FrameBytes * 7L));
        Assert.NotNull(cache.GetSegment(obsolete.SegmentId));
        Assert.True(cache.TryTrimReverseWindow(request, 300, request.TargetSeconds, .1, Header.FrameBytes * 6L));
        Assert.Null(cache.GetSegment(obsolete.SegmentId));
        Assert.Equal(left.Frames, cache.GetSegment(left.SegmentId)!.Frames);
        Assert.Equal(right.Frames, cache.GetSegment(right.SegmentId)!.Frames);
        Assert.True(cache.TryGetAt(request, 300 + 1 / Header.Fps, out _));
        Assert.True(cache.TryGetAt(request, request.TargetSeconds, out _));
    }

    [Fact]
    public void AdmissionUsesTheExistingLruOrderAcrossClosedAndLegacyEntries()
    {
        var cache = new FrameCache(Header.FrameBytes * 20L);
        var request = Request() with { TargetSeconds = 300.05 };
        var old = cache.StoreSegment(request, Frames(400, 8), new(1000, 0, true), 400, 401)!;
        var legacy = request with { TargetSeconds = 500 };
        cache.Store(legacy, Frames(500, 8));
        var current = cache.StoreSegment(request, Frames(300, 4), new(1000, 0, true), 300, 300 + 3 / Header.Fps)!;
        cache.PinCurrent(current.SegmentId, request.TargetSeconds);
        Assert.True(cache.TryTrimReverseWindow(request, 300, request.TargetSeconds, .1, Header.FrameBytes * 6L));
        Assert.Null(cache.GetSegment(old.SegmentId));
        Assert.True(cache.TryGet(legacy, out _));
        Assert.Equal(current.Frames, cache.GetSegment(current.SegmentId)!.Frames);
    }

    [Fact]
    public void ActiveCompetitionCannotBeCountedAsFreeOrDestroyTheCurrentBridge()
    {
        var cache = new FrameCache(Header.FrameBytes * 4L);
        var request = Request();
        long active = cache.BeginSegment(request with { VppArgs = new[] { "other" } }, new(1000, 0, true), 400);
        foreach (var frame in Frames(400, 2)) Assert.True(cache.Append(active, frame));
        var current = cache.StoreSegment(request, Frames(300, 2), new(1000, 0, true), 300, 300 + 1 / Header.Fps)!;
        cache.PinCurrent(current.SegmentId, 300);
        Assert.Equal(0, cache.ReverseWindowRoom(request with { TargetSeconds = 300 }, 300, 300, .1));
        Assert.False(cache.TryTrimReverseWindow(request with { TargetSeconds = 300 }, 300, 300, .1, Header.FrameBytes));
        Assert.Equal(2, cache.GetSegment(active)!.Frames.Count);
        Assert.Equal(current.Frames, cache.GetSegment(current.SegmentId)!.Frames);
        Assert.Null(cache.StoreSegment(request with { TargetSeconds = 300 }, Frames(297, 2), new(1000, 0, true), 297, 297.1,
            reverseWindow: true));
        Assert.NotNull(cache.GetSegment(active));
        Assert.Equal(current.Frames, cache.GetSegment(current.SegmentId)!.Frames);
    }

    [Fact]
    public void CurrentPrefixRollingRemainsAvailableAtTheCapacityLimit()
    {
        var cache = new FrameCache(Header.FrameBytes * 4L);
        var request = Request() with { TargetSeconds = 300 + 2 / Header.Fps };
        long active = cache.BeginSegment(request, new(1000, 0, true), 300);
        foreach (var frame in Frames(300, 4)) Assert.True(cache.Append(active, frame));
        cache.PinCurrent(active, request.TargetSeconds);
        Assert.True(cache.Append(active, Frames(300 + 4 / Header.Fps, 1)[0]));
        Assert.True(cache.SizeBytes <= cache.CapacityBytes);
        Assert.Equal(4, cache.GetSegment(active)!.Frames.Count);
        Assert.True(cache.TryGetAt(request, request.TargetSeconds, out _));
    }

    private sealed class Backend : IPreviewBackend
    {
        internal readonly List<PreviewRequest> Requests = new();
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new PreviewRunResult(-1, "先読み開始だけを記録するfixture"));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

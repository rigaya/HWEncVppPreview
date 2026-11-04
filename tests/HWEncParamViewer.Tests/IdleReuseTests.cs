using System.Threading.Channels;
using HWEncParamViewer.Core;
using Xunit.Abstractions;

namespace HWEncParamViewer.Tests;

public sealed class IdleReuseTests(ITestOutputHelper output)
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(5);
    private static PreviewRequest Request(long id, double target, bool continuous = true) => new()
    {
        EncoderPath = "encoder", InputFile = "input", CorrelationId = id, Fps = 10, TargetSeconds = target,
        Capabilities = new(true), TimestampOriginSeconds = 1000, Continuous = continuous,
        PreMarginSeconds = continuous ? 2 : 5, PostMarginSeconds = 0.5,
    };

    [Fact]
    public async Task CachedFiniteFrameBeforeActiveFirstFrameDoesNotLeaveIdlePending()
    {
        var backend = new GapBackend();
        var cache = new FrameCache();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        var finite = new TaskCompletionSource<PreviewCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var images = new List<PreviewFrameResult>();
        controller.Completed += value => { if (value.Request.CorrelationId == 2) finite.TrySetResult(value); };
        controller.FrameReady += images.Add;

        controller.Submit(Request(1, 103));
        Assert.Equal(105, await backend.Pauses.Reader.ReadAsync().AsTask().WaitAsync(SetupTimeout), 6);
        await controller.WaitForIdleAsync().WaitAsync(SetupTimeout);
        controller.Submit(Request(2, 100, false), navigation: true);
        var older = await finite.Task.WaitAsync(SetupTimeout);
        await controller.WaitForIdleAsync().WaitAsync(SetupTimeout);
        Assert.Equal(95, CommandBuilder.Plan(backend.Requests[1]).SeekSeconds);
        Assert.Equal(1095.3, older.Frames[0].Xts!.Value, 6);
        Assert.Equal(56, older.Frames.Count);
        Assert.Equal(97, older.Segment!.SafeStartSeconds);
        Assert.Equal(100.4, older.Segment.SafeEndSeconds, 6);

        controller.Submit(Request(3, 101), navigation: true);
        Assert.Equal(103, await backend.Pauses.Reader.ReadAsync().AsTask().WaitAsync(SetupTimeout), 6);
        await controller.WaitForIdleAsync().WaitAsync(SetupTimeout);
        var active = cache.GetSegment(images.Last(value => value.Request.CorrelationId == 3).Segment!.SegmentId)!;
        Assert.True(active.CanStitch);
        Assert.True(active.Clock.TrustedAbsolute);
        Assert.Equal(1100.1, active.Frames[0].Xts!.Value, 6);
        Assert.Equal(-1, FrameSelector.Select(active, 100, 10));
        Assert.True(cache.TryGetAt(Request(4, 100, false), 100, out var cached));
        Assert.True(cached.CanStitch);
        Assert.Equal(older.Segment.SegmentId, cached.SegmentId);
        Assert.True(FrameSelector.Select(cached, 100, 10) >= 0);
        Assert.Equal(3, backend.Requests.Count);
        output.WriteLine($"旧finite safe={older.Segment.SafeStartSeconds}..{older.Segment.SafeEndSeconds:R}、新stream first=100.1 last=103、ownSelect=-1/cacheSelect>=0、stitch=true、requests=3");

        controller.Submit(Request(4, 100, false), navigation: true);
        output.WriteLine($"戻りSubmit後のDemand完了={controller.WaitForCurrentDemandAsync().IsCompleted}、requests={backend.Requests.Count}");
        // 読取済みの旧streamへ再結合しても、cache通知または明示的な終了で必ず需要を解決する。
        await controller.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(1));
        var image = images.Last(value => value.Request.CorrelationId == 4);
        Assert.Equal(1100, image.Frame.Xts!.Value, 6);
        Assert.Equal(3, backend.Requests.Count);
    }

    [Theory]
    [InlineData(100, 100.1)]
    [InlineData(102, 102)]
    public void StreamAndFiniteCacheClampFirstLandingWithoutReducingWarmup(double planned, double expected)
    {
        var request = Request(1, 103) with { SeekSecondsOverride = 100 };
        var clock = new PreviewClockMap(1000, 100, true);
        var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 10, FpsDen = 1 };
        var frames = Enumerable.Range(0, 23).Select(index => new Y4mFrame(header, 1100.1 + index / 10.0, 0.1, new byte[6])).ToArray();
        var streaming = new FrameCache();
        long id = streaming.BeginSegment(request, clock, planned);
        foreach (var frame in frames) Assert.True(streaming.Append(id, frame));
        Assert.Equal(expected, streaming.GetSegment(id)!.SafeStartSeconds, 6);
        var finite = new FrameCache().StoreSegment(request, frames, clock, planned, 102.4)!;
        Assert.Equal(expected, finite.SafeStartSeconds, 6);
    }

    [Fact]
    public async Task FiniteEarlyImageAndStoredSegmentShareActualFirstLanding()
    {
        var backend = new GapBackend();
        var cache = new FrameCache();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        PreviewFrameResult? early = null;
        PreviewCompletion? done = null;
        controller.FrameReady += value => early = value;
        controller.Completed += value => done = value;
        controller.Submit(Request(1, 101, false) with { PreMarginSeconds = 2 });
        await controller.WaitForIdleAsync().WaitAsync(SetupTimeout);
        Assert.Equal(0, Assert.Single(backend.Requests).WarmupSeconds);
        Assert.NotNull(early);
        Assert.NotNull(done);
        Assert.Equal(100.3, early.Segment!.SafeStartSeconds, 6);
        Assert.Equal(100.3, done.Segment!.SafeStartSeconds, 6);
        Assert.Equal(1101, early.Frame.Xts!.Value, 6);
        Assert.False(cache.TryGetAt(Request(2, 100.1, false), 100.1, out _));
    }

    [Fact]
    public void MissingOrNonFiniteFirstTimestampDoesNotInventSafetyBoundary()
    {
        var clock = new PreviewClockMap(1000, 100, true);
        var header = new Y4mHeader { Width = 2, Height = 2 };
        Assert.Equal(102, FrameCache.SafeStartIncludingFirst(102, clock, null, 10));
        Assert.Equal(102, FrameCache.SafeStartIncludingFirst(102, clock, new(header, null, 0.1, new byte[6]), 10));
        Assert.Equal(102, FrameCache.SafeStartIncludingFirst(102, clock, new(header, double.NaN, 0.1, new byte[6]), 10));
    }

    private sealed class GapBackend : IPreviewBackend
    {
        public Channel<double> Pauses { get; } = Channel.CreateUnbounded<double>();
        public List<PreviewRequest> Requests { get; } = new();
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            double first = seek + (request.Continuous ? 0.1 : 0.3);
            var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 10, FpsDen = 1 };
            double last = double.NaN;
            try
            {
                for (int index = 0; request.Continuous || index < CommandBuilder.Plan(request).FrameCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var permission = sink.BeforeReadAsync(cancellationToken);
                    if (!permission.IsCompleted)
                    {
                        sink.OnReadStateChanged(false);
                        Pauses.Writer.TryWrite(last);
                    }
                    await permission;
                    sink.OnReadStateChanged(true);
                    sink.OnOutput();
                    last = first + index / 10.0;
                    byte pixel = (byte)((int)Math.Round(last * 10) % 251);
                    sink.OnFrame(new(header, 1000 + last, 0.1, Enumerable.Repeat(pixel, 6).ToArray()));
                }
                return new(0, "");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(-12, "", true); }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

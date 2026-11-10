using System.Threading.Channels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class ForwardAdoptionTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static PreviewRequest Request(double target, long id = 1) => new()
    {
        EncoderPath = "forward-observed", InputFile = "input", Fps = 10, TargetSeconds = target,
        CorrelationId = id, Continuous = true, Capabilities = new(true), TimestampOriginSeconds = 1000,
    };
    private static Y4mFrame Frame(double input) => new(new Y4mHeader { Width = 2, Height = 2 },
        1000 + input, 0.1, new byte[6]);

    [Theory]
    [InlineData("near")]
    [InlineData("stale")]
    [InlineData("paused")]
    [InlineData("unobserved")]
    [InlineData("first_after")]
    [InlineData("disconnected")]
    [InlineData("no_capacity")]
    [InlineData("no_reuse")]
    [InlineData("content")]
    [InlineData("legacy")]
    [InlineData("explicit")]
    [InlineData("eof")]
    [InlineData("watchdog")]
    [InlineData("generation")]
    public async Task 未到達合流の条件と実中心Vfr隣と取消を検証する(string reason)
    {
        var clock = new FakeTimeProvider(); var cache = new FrameCache(reason == "no_capacity" ? 600 : 1L << 30); var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, timeProvider: clock, debounce: TimeSpan.Zero, watchdog: TimeSpan.FromSeconds(5));
        var images = new List<PreviewFrameResult>(); var updates = new List<PreviewSegmentUpdate>();
        controller.FrameReady += images.Add; controller.SegmentUpdated += updates.Add;
        // 同内容の先行runも観測し、初frame遅延の最短を比較に使える状態にする。
        for (int run = 1; run <= 1; ++run)
        {
            controller.Submit(Request(30, run), bypassCache: true);
            var calibration = await backend.Started.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
            clock.Advance(TimeSpan.FromSeconds(1)); await calibration.Send(29);
            for (int i = 291; i <= 320; ++i) { clock.Advance(TimeSpan.FromMilliseconds(50)); await calibration.Send(i / 10d); }
            await controller.WaitForCurrentDemandAsync().WaitWithDiagnosticsAsync(Safety);
        }
        var seed = cache.StoreSegment(Request(10), Enumerable.Range(90, 40).Select(i => Frame(i / 10d)).ToArray(), new(1000, 9, true), 9, 13);
        controller.Submit(Request(10, 2), navigation: true); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5, 3), 1);
        var forward = await backend.Started.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        if (reason != "unobserved")
        {
            double first = reason == "first_after" ? 13.1 : 12;
            clock.Advance(TimeSpan.FromSeconds(1)); await forward.Send(first);
            for (int i = 1; i <= 7; ++i) { clock.Advance(TimeSpan.FromMilliseconds(50)); await forward.Send(first + i / 10d); }
        }
        if (reason == "stale") clock.Advance(TimeSpan.FromMilliseconds(101));
        if (reason == "paused") forward.Pause();
        if (reason == "disconnected") cache.RemoveSegment(seed!.SegmentId);
        if (reason == "no_capacity")
        {
            cache.RemoveSegment(1); cache.PinCurrent(seed!.SegmentId, 9);
            long blocker = cache.BeginSegment(Request(100) with { InputFile = "blocked" }, new(1000, 0, true), 0);
            Assert.True(cache.Append(blocker, new(new Y4mHeader { Width = 2, Height = 2 }, 1100, 0.1,
                new byte[checked((int)(cache.CapacityBytes - cache.SizeBytes))])));
        }
        if (reason == "eof") { forward.Finished.TrySetResult(); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety); }
        var neighbor = Request(12.9, 20);
        if (reason == "content") neighbor = neighbor with { VppArgs = new[] { "different" } };
        if (reason == "legacy") neighbor = neighbor with { TimestampOriginTrusted = false };
        controller.Submit(neighbor, bypassCache: true, navigation: reason != "explicit", reuseActiveStream: reason != "no_reuse");
        if (reason == "eof")
        {
            await backend.Started.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(Safety); Assert.Equal(3, backend.Calls); return;
        }
        if (reason is not ("near" or "watchdog" or "generation"))
        {
            Assert.True(forward.Token.IsCancellationRequested); return;
        }
        Assert.False(forward.Token.IsCancellationRequested);
        Assert.Equal(2, backend.Calls);
        Assert.DoesNotContain(images, image => image.Request.CorrelationId == 20);
        if (reason == "watchdog")
        {
            PreviewFailure? failure = null; controller.Failed += value => failure = value;
            clock.Advance(TimeSpan.FromSeconds(6)); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            Assert.True(failure?.TimedOut); Assert.True(forward.Token.IsCancellationRequested); return;
        }
        if (reason == "generation")
        {
            controller.Submit(Request(30, 21), bypassCache: true);
            Assert.True(forward.Token.IsCancellationRequested);
            var latest = await backend.Started.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
            clock.Advance(TimeSpan.FromSeconds(1)); await latest.Send(29);
            for (int i = 291; i <= 301; ++i) { clock.Advance(TimeSpan.FromMilliseconds(50)); await latest.Send(i / 10d); }
            forward.EmitLate(12.9);
            Assert.Contains(images, image => image.Request.CorrelationId == 21);
            Assert.DoesNotContain(images, image => image.Request.CorrelationId == 20); return;
        }
        clock.Advance(TimeSpan.FromMilliseconds(50)); await forward.Send(12.9);
        Assert.Contains(images, image => image.Request.CorrelationId == 20 && image.Frame.Xts == 1012.9);
        Assert.DoesNotContain(updates, update => update.Request.CorrelationId == 20 && update.Segment.Frames.Any(frame => frame.Xts > 1012.9));
        // 実際の次frameが疎でも入力fpsから架空の13.0秒frameを作らない。
        clock.Advance(TimeSpan.FromMilliseconds(50)); await forward.Send(13.2);
        Assert.Contains(updates, update => update.Request.CorrelationId == 20 && update.Segment.Frames.Any(frame => frame.Xts == 1013.2));
        Assert.DoesNotContain(updates.Where(update => update.Request.CorrelationId == 20).SelectMany(update => update.Segment.Frames), frame => frame.Xts == 1013.0);
        Assert.Equal(2, backend.Calls);
    }

    private sealed class Backend : IPreviewBackend
    {
        public Channel<Run> Started { get; } = Channel.CreateUnbounded<Run>();
        public int Calls { get; private set; }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            ++Calls; var run = new Run(sink, cancellationToken); Started.Writer.TryWrite(run);
            try { await run.Finished.Task.WaitAsync(cancellationToken); return new(0, ""); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(-12, "", true); }
        }
    }
    private sealed class Run(IPreviewSink sink, CancellationToken token)
    {
        public CancellationToken Token => token;
        public void Pause() => sink.OnReadStateChanged(false);
        public void EmitLate(double input) => sink.OnFrame(Frame(input));
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Send(double input)
        {
            await sink.BeforeReadAsync(token);
            sink.OnReadStateChanged(true); sink.OnOutput(); sink.OnFrame(Frame(input));
        }
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class StreamNeighborReuseTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);
    private static PreviewRequest Request(double target = 10, long id = 1) => new()
    {
        EncoderPath = "encoder", InputFile = "input", TargetSeconds = target, CorrelationId = id,
        Fps = 2, Continuous = true, TimestampOriginSeconds = 1000, Capabilities = new(true),
    };
    private static Y4mFrame Frame(double time) => new(Y4mHeader.Parse("YUV4MPEG2 W2 H2 F2:1 C420"),
        1000 + time, 0.5, Enumerable.Repeat((byte)((int)Math.Round(time * 2) % 251), 6).ToArray());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNeighborCanJoinReadingAndPausedStream(bool reading)
    {
        var backend = new Backend(holdCenter: reading);
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var updated = new TaskCompletionSource<PreviewSegmentUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        double center = reading ? 10 : 12;
        controller.SegmentUpdated += value =>
        {
            if (value.Request.CorrelationId == 2 && value.Segment.Frames.Any(frame => frame.Xts == 1000 + center + 0.5))
                updated.TrySetResult(value);
        };
        controller.Submit(Request());
        if (reading) await backend.Center.Task.WaitAsync(Safety);
        else await backend.Pauses.Reader.ReadAsync().AsTask().WaitAsync(Safety);
        controller.Submit(Request(center, 2), bypassCache: true, navigation: true, reuseActiveStream: true);
        backend.Next.TrySetResult();
        var result = await updated.Task.WaitAsync(Safety);
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Single(backend.Requests);
        Assert.Equal(0, backend.Cancelled);
        Assert.Equal(2, result.Request.CorrelationId);
        Assert.Equal(1, result.Segment.PhysicalRequest.CorrelationId);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("before")]
    [InlineData("jump")]
    [InlineData("missing_center")]
    [InlineData("refresh")]
    [InlineData("not_navigation")]
    [InlineData("finite")]
    [InlineData("untrusted")]
    public async Task IneligibleOptInOrDefaultBypassStillRefetches(string reason)
    {
        var backend = new Backend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        controller.Submit(Request());
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitAsync(Safety);
        var request = reason switch
        {
            "content" => Request(12, 2) with { VppArgs = new[] { "changed" } },
            "before" => Request(8, 2),
            "jump" => Request(20, 2),
            "missing_center" => Request(12.4, 2),
            "finite" => Request(10, 2) with { Continuous = false, PreMarginSeconds = 5 },
            "untrusted" => Request(12, 2) with { TimestampOriginTrusted = false },
            _ => Request(12, 2),
        };
        if (reason == "refresh") controller.Submit(request, bypassCache: true, navigation: true);
        else controller.Submit(request, bypassCache: true, navigation: reason != "not_navigation", reuseActiveStream: true);
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
        Assert.Equal(1, backend.Cancelled);
    }

    [Fact]
    public async Task AbsentActiveStreamDoesNotConvertBypassToCacheHit()
    {
        var cache = new FrameCache();
        cache.StoreSegment(Request(), new[] { Frame(9), Frame(9.5), Frame(10) }, new(1000, 9, true), 9, 10.5);
        var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request(), bypassCache: true, navigation: true, reuseActiveStream: true);
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task EndedStreamIsNotReused()
    {
        var backend = new Backend(end: true);
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        controller.Submit(Request());
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        controller.Submit(Request(10, 2), bypassCache: true, navigation: true, reuseActiveStream: true);
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task PausedWithoutNeighborCapacityUsesOldRefetch()
    {
        var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: new FrameCache(6), debounce: TimeSpan.Zero);
        controller.Submit(Request());
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitAsync(Safety);
        controller.Submit(Request(10, 2), bypassCache: true, navigation: true, reuseActiveStream: true);
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
    }

    private sealed class Backend(bool holdCenter = false, bool end = false) : IPreviewBackend
    {
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public Channel<double> Pauses { get; } = Channel.CreateUnbounded<double>();
        public TaskCompletionSource Center { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Next { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Cancelled => Volatile.Read(ref _cancelled);
        private int _calls, _cancelled;
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            int call = Interlocked.Increment(ref _calls);
            Requests.Enqueue(request);
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            double last = double.NaN;
            try
            {
                for (int index = 0; request.Continuous || index < CommandBuilder.Plan(request).FrameCount; ++index)
                {
                    var permission = sink.BeforeReadAsync(token);
                    if (!permission.IsCompleted) { sink.OnReadStateChanged(false); Pauses.Writer.TryWrite(last); }
                    await permission;
                    sink.OnReadStateChanged(true);
                    token.ThrowIfCancellationRequested();
                    last = seek + index * 0.5;
                    sink.OnOutput();
                    sink.OnFrame(Frame(last));
                    if (call == 1 && holdCenter && last == 10) { Center.TrySetResult(); await Next.Task.WaitAsync(token); }
                    if (end && last >= request.TargetSeconds + 2) return new(0, "");
                }
                return new(0, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { Interlocked.Increment(ref _cancelled); return new(-12, "", true); }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

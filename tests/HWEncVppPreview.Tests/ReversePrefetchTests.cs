using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class ReversePrefetchTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static PreviewRequest Request(double target = 10, double fps = 100) => new()
    {
        EncoderPath = "prefetch-stub", InputFile = "input", TargetSeconds = target, Fps = fps,
        Capabilities = new(true), TimestampOriginSeconds = 1000, Continuous = true,
        // 従来の2秒暖機を検証するfixtureなので、時間方向の処理を明示する。
        VppArgs = new[] { "--vpp-kfm" },
        PreMarginSeconds = 2, PostMarginSeconds = 0.5,
    };

    private static void ReverseFive(PreviewController controller, PreviewRequest request)
    {
        for (int i = 1; i <= 5; ++i) controller.NotifyNavigation(request with { TargetSeconds = request.TargetSeconds - i / request.Fps }, -1);
    }

    [Fact]
    public async Task ReversePrefetchStartsBeforeEdgeIsUniqueAndCannotPublishImages()
    {
        var backend = new Backend { HoldPrefetch = true };
        var cache = new FrameCache();
        await using var controller = new PreviewController(backend, cache: cache, timeProvider: new FakeTimeProvider(), debounce: TimeSpan.Zero);
        int images = 0, completed = 0;
        controller.FrameReady += _ => ++images;
        controller.Completed += _ => ++completed;
        var request = Request();
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(9, cache.GetConnectedSafeRange(request)!.Value.Start);
        Assert.Equal(1, images);
        ReverseFive(controller, request);
        await backend.PrefetchStarted.Task.WaitWithDiagnosticsAsync(Safety);
        Task pending = controller.GetPendingReversePrefetch(request with { TargetSeconds = 9 })!;
        Assert.NotNull(pending);
        Assert.Same(pending, controller.GetPendingReversePrefetch(request with { TargetSeconds = 8.99 }));
        for (int i = 0; i < 10; ++i) controller.NotifyNavigation(request with { TargetSeconds = 9.94 - i * .01 }, -1);
        Assert.Equal(2, backend.Requests.Count);
        // 先読みの受信途中も既存safe範囲をブロックしない。
        Assert.True(cache.TryGetAt(request, 9.9, out _));
        var physical = backend.Requests[1];
        Assert.True(physical.CorrelationId < 0);
        Assert.False(physical.Continuous);
        Assert.Equal(4, CommandBuilder.Plan(physical).SeekSeconds);
        Assert.Equal(1, physical.PostMarginSeconds);
        backend.ReleasePrefetch.TrySetResult();
        await pending.WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(6, cache.GetConnectedSafeRange(request)!.Value.Start);
        Assert.True(cache.TryGetAt(request, 8.99, out _));
        Assert.Equal(1, images);
        Assert.Equal(0, completed);
        Assert.Equal(1, backend.MaxConcurrent);
    }

    [Fact]
    public async Task ShortPrefetchKeepsItsUsablePrefixWithoutPublishingOrRetryingTheBoundary()
    {
        var backend = new Backend { ShortPrefetch = true };
        var cache = new FrameCache();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        int images = 0, failures = 0;
        controller.FrameReady += _ => ++images;
        controller.Failed += _ => ++failures;
        var request = Request();
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        ReverseFive(controller, request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
        var partial = cache.GetSegment(2);
        Assert.NotNull(partial);
        Assert.NotEmpty(partial.Frames);
        Assert.True(partial.SafeEndSeconds < 9);
        Assert.Equal(1, images);
        Assert.Equal(0, failures);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(100)]
    public async Task OneReverseFrameNeverCancelsPausedStream(double fps)
    {
        var backend = new Backend();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider(), debounce: TimeSpan.Zero);
        var request = Request(fps: fps);
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(request with { TargetSeconds = 10 - 1 / fps }, -1);
        Assert.Single(backend.Requests);
        Assert.Equal(0, backend.Cancelled);
        Assert.Null(controller.GetPendingReversePrefetch(request));
    }

    [Fact]
    public async Task TwoLowRateFramesCanQualifyByMediaDistance()
    {
        var backend = new Backend();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider(), debounce: TimeSpan.Zero);
        var request = Request(fps: 2);
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(request with { TargetSeconds = 9.5 }, -1);
        controller.NotifyNavigation(request with { TargetSeconds = 9 }, -1);
        await backend.PrefetchStarted.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
        Assert.Equal(1, backend.Cancelled);
    }

    [Fact]
    public async Task ForwardDirectionKeepsPrefetchAndNewStreamWaitsForSameBackend()
    {
        var backend = new Backend { HoldPrefetch = true };
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider(), debounce: TimeSpan.Zero);
        var request = Request();
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        ReverseFive(controller, request);
        await backend.PrefetchStarted.Task.WaitWithDiagnosticsAsync(Safety);
        Task pending = controller.GetPendingReversePrefetch(request)!;
        controller.NotifyNavigation(request, 1);
        controller.Submit(request with { TargetSeconds = 12.1 }, navigation: true);
        Assert.False(backend.PrefetchToken.IsCancellationRequested);
        Assert.Equal(2, backend.Requests.Count);
        backend.ReleasePrefetch.TrySetResult();
        await pending.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(3, backend.Requests.Count);
        Assert.True(backend.Requests[2].Continuous);
        Assert.Equal(1, backend.MaxConcurrent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditingOrLargeSeekCancelsPrefetch(bool seek)
    {
        var backend = new Backend { HoldPrefetch = true };
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider(), debounce: TimeSpan.Zero);
        var request = Request();
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        ReverseFive(controller, request);
        await backend.PrefetchStarted.Task.WaitWithDiagnosticsAsync(Safety);
        controller.Submit(seek ? request with { TargetSeconds = 20 } : request with { VppArgs = new[] { "edited" } }, navigation: seek);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.True(backend.PrefetchToken.IsCancellationRequested);
        Assert.Null(controller.GetPendingReversePrefetch(request));
        Assert.Equal(3, backend.Requests.Count);
    }

    [Fact]
    public async Task PixelMismatchSeparatesRangesAndDoesNotShowPrefetch()
    {
        var backend = new Backend { ShiftPrefetchPixels = true };
        var cache = new FrameCache();
        await using var controller = new PreviewController(backend, cache: cache, timeProvider: new FakeTimeProvider(), debounce: TimeSpan.Zero);
        int images = 0;
        controller.FrameReady += _ => ++images;
        var request = Request();
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        ReverseFive(controller, request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
        Assert.False(cache.CanStitch(request));
        Assert.Equal(1, images);
        Assert.False(cache.TryGetAt(request, 10, out _));
        controller.NotifyNavigation(request with { TargetSeconds = 9.8 }, -1);
        Assert.Equal(2, backend.Requests.Count);
        controller.Submit(request with { TargetSeconds = 8.9, Continuous = false, PreMarginSeconds = 5 }, navigation: true);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(3, backend.Requests.Count);
        Assert.Equal(2, images);
    }

    [Fact]
    public async Task InputStartNeverCreatesNegativePrefetch()
    {
        var backend = new Backend();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider(), debounce: TimeSpan.Zero);
        var request = Request(1);
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        ReverseFive(controller, request);
        Assert.Single(backend.Requests);
        Assert.Equal(0, backend.Cancelled);
    }

    [Fact]
    public async Task PrefetchNearInputStartClampsItsPhysicalWindowToZero()
    {
        var backend = new Backend();
        var cache = new FrameCache();
        await using var controller = new PreviewController(backend, cache: cache, timeProvider: new FakeTimeProvider(), debounce: TimeSpan.Zero);
        var request = Request(4);
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        ReverseFive(controller, request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
        Assert.Null(CommandBuilder.Plan(backend.Requests[1]).SeekSeconds);
        Assert.Equal(0, cache.GetConnectedSafeRange(request)!.Value.Start);
    }

    [Fact]
    public async Task ReverseEligibilityIsReevaluatedWhenRunningStreamBecomesPaused()
    {
        var backend = new Backend { HoldContinuousAtSeconds = 9.1 };
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider(), debounce: TimeSpan.Zero);
        var request = Request();
        controller.Submit(request);
        await backend.ContinuousHeld.Task.WaitWithDiagnosticsAsync(Safety);
        ReverseFive(controller, request with { TargetSeconds = 9.1 });
        Assert.Single(backend.Requests);
        Assert.Equal(0, backend.Cancelled);
        backend.ReleaseContinuous.TrySetResult();
        await backend.PrefetchStarted.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
        Assert.Equal(1, backend.Cancelled);
    }

    [Fact]
    public async Task DisposeTracksAndCancelsUnfinishedPrefetch()
    {
        var backend = new Backend { HoldPrefetch = true };
        var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider(), debounce: TimeSpan.Zero);
        var request = Request();
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        ReverseFive(controller, request);
        await backend.PrefetchStarted.Task.WaitWithDiagnosticsAsync(Safety);
        Task pending = controller.GetPendingReversePrefetch(request)!;
        await controller.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        Assert.True(pending.IsCompletedSuccessfully);
        Assert.True(backend.PrefetchToken.IsCancellationRequested);
        Assert.False(controller.ShutdownIncomplete);
    }

    [Fact]
    public void ConnectedRangeDoesNotJumpAcrossCacheGaps()
    {
        var cache = new FrameCache();
        var request = Request(12, 1);
        void Store(int start, int end)
        {
            var frames = Enumerable.Range(start, end - start + 1).Select(time => new Y4mFrame(
                new Y4mHeader { Width = 2, Height = 2 }, 1000 + time, 1, new byte[6])).ToArray();
            cache.StoreSegment(request, frames, new(1000, start, true), start, end);
        }
        Store(0, 5);
        Store(10, 15);
        Assert.Equal((10d, 15d), cache.GetConnectedSafeRange(request)!.Value);
        Store(4, 11);
        Assert.Equal((0d, 15d), cache.GetConnectedSafeRange(request)!.Value);
    }

    private sealed class Backend : IPreviewBackend
    {
        public List<PreviewRequest> Requests { get; } = new();
        public bool HoldPrefetch { get; init; }
        public bool ShortPrefetch { get; init; }
        public double? HoldContinuousAtSeconds { get; init; }
        public TaskCompletionSource ContinuousHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseContinuous { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ShiftPrefetchPixels { get; init; }
        public TaskCompletionSource PrefetchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePrefetch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken PrefetchToken { get; private set; }
        public int Cancelled { get; private set; }
        private int _concurrent;
        public int MaxConcurrent { get; private set; }
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Add(request);
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _concurrent));
            bool prefetch = request.CorrelationId < 0;
            try
            {
                if (prefetch)
                {
                    PrefetchToken = token;
                    PrefetchStarted.TrySetResult();
                    if (HoldPrefetch) await ReleasePrefetch.Task.WaitAsync(token);
                }
                double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
                double duration = 1 / request.Fps;
                var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = (int)request.Fps, FpsDen = 1 };
                int count = CommandBuilder.Plan(request).FrameCount - (prefetch && ShortPrefetch ? (int)(request.Fps * 1.2) : 0);
                for (int i = 0; request.Continuous || i < count; ++i)
                {
                    token.ThrowIfCancellationRequested();
                    ValueTask gate = sink.BeforeReadAsync(token);
                    if (!gate.IsCompleted) sink.OnReadStateChanged(false);
                    await gate.ConfigureAwait(false);
                    sink.OnReadStateChanged(true);
                    double time = seek + i * duration;
                    if (request.Continuous && HoldContinuousAtSeconds is double hold && time >= hold && !ContinuousHeld.Task.IsCompleted)
                    {
                        ContinuousHeld.TrySetResult();
                        await ReleaseContinuous.Task.WaitAsync(token);
                    }
                    sink.OnOutput();
                    sink.OnFrame(new(header, 1000 + time, duration, Enumerable.Repeat((byte)((int)Math.Round(time * request.Fps)
                        + (prefetch && ShiftPrefetchPixels ? 1 : 0)), 6).ToArray()));
                }
                return new(0, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { ++Cancelled; return new(-12, "", true); }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class NavigationControllerTests
{
    [Fact]
    public async Task FinishedRunningNavigationCanPublishWhileANewerRequestIsPending()
    {
        var backend = new ControlledBackend();
        var images = new ConcurrentQueue<PreviewFrameResult>();
        var clock = new NavigationClock();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.FrameReady += images.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        // 先行要求の実行中に次の移動を受け付け、最後の画像が残ることを確かめる。
        controller.Submit(Request(1), navigation: true);
        first.End();
        var latest = await backend.NextAsync();
        latest.End();
        await controller.WaitForIdleAsync();
        Assert.NotEmpty(images);
        Assert.Equal(1, images.Last().Request.TargetSeconds);
    }

    [Fact]
    public async Task NavigationKeepsOnlyTheLatestWaitingRequestWithoutAdvancingTheClock()
    {
        var backend = new ControlledBackend();
        var images = new ConcurrentQueue<PreviewFrameResult>();
        var clock = new NavigationClock();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.FrameReady += images.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        controller.Submit(Request(2), navigation: true);
        controller.Submit(Request(3), navigation: true);
        first.End();
        var latest = await backend.NextAsync();
        latest.End();
        await controller.WaitForIdleAsync();
        Assert.NotEmpty(images);
        Assert.Equal(3, images.Last().Request.TargetSeconds);
    }

    [Fact]
    public async Task EditingCancelsTheRunningRequestAndSuppressesItsNotifications()
    {
        var backend = new ControlledBackend();
        var images = new ConcurrentQueue<PreviewFrameResult>();
        var completions = new ConcurrentQueue<PreviewCompletion>();
        var clock = new NavigationClock();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.FrameReady += images.Enqueue;
        controller.Completed += completions.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        controller.Submit(Request(2), bypassDebounce: true);
        first.End();
        var edit = await backend.NextAsync();
        edit.End();
        await controller.WaitForIdleAsync();
        Assert.Equal(2, Assert.Single(images).Request.TargetSeconds);
        Assert.Equal(2, Assert.Single(completions).Request.TargetSeconds);
    }

    [Fact]
    public async Task EditingImmediatelySuppressesOldNavigationDuringItsDebounce()
    {
        var backend = new ControlledBackend();
        var clock = new NavigationClock();
        var images = new ConcurrentQueue<PreviewFrameResult>();
        var completions = new ConcurrentQueue<PreviewCompletion>();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.FrameReady += images.Enqueue;
        controller.Completed += completions.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1));
        first.End();
        clock.Advance(TimeSpan.FromMilliseconds(150));
        var edit = await backend.NextAsync();
        edit.End();
        await controller.WaitForIdleAsync();
        Assert.Equal(1, Assert.Single(images).Request.TargetSeconds);
        Assert.Equal(1, Assert.Single(completions).Request.TargetSeconds);
    }

    [Fact]
    public async Task OnlyTheCurrentRequestPublishesFailure()
    {
        var backend = new ControlledBackend();
        var failures = new ConcurrentQueue<PreviewFailure>();
        var clock = new NavigationClock();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.Failed += failures.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        first.Fail();
        var latest = await backend.NextAsync();
        latest.Fail();
        await controller.WaitForIdleAsync();
        Assert.Equal(1, Assert.Single(failures).Request.TargetSeconds);
    }

    [Fact]
    public async Task EditingFromFrameHandlerSuppressesTheOldCompletion()
    {
        var backend = new ControlledBackend();
        var notifications = new ConcurrentQueue<string>();
        var clock = new NavigationClock();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.FrameReady += frame =>
        {
            notifications.Enqueue($"frame:{frame.Request.TargetSeconds}");
            if (frame.Request.TargetSeconds == 0) controller.Submit(Request(1), bypassDebounce: true);
        };
        controller.Completed += result => notifications.Enqueue($"completed:{result.Request.TargetSeconds}");
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        first.End();
        var edit = await backend.NextAsync();
        edit.End();
        await controller.WaitForIdleAsync();
        Assert.Equal(new[] { "frame:0", "frame:1", "completed:1" }, notifications);
    }

    [Fact]
    public async Task BackgroundSubmitDoesNotCancelOrInvalidateRunningForeground()
    {
        var backend = new ControlledBackend();
        var frames = new ConcurrentQueue<PreviewFrameResult>();
        var completions = new ConcurrentQueue<PreviewCompletion>();
        var clock = new NavigationClock();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.FrameReady += frames.Enqueue;
        controller.Completed += completions.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var foreground = await backend.NextAsync();
        controller.Submit(Request(10), background: true);
        Assert.False(foreground.Token.IsCancellationRequested);
        Assert.Equal(1, backend.Starts);
        foreground.End();
        var background = await backend.NextAsync();
        Assert.Equal(10, background.Request.TargetSeconds);
        Assert.Equal(0, Assert.Single(frames).Request.TargetSeconds);
        Assert.Equal(0, Assert.Single(completions).Request.TargetSeconds);
        background.End();
        await controller.WaitForIdleAsync();
        Assert.Equal(new[] { 0.0, 10.0 }, completions.Select(value => value.Request.TargetSeconds));
    }

    [Fact]
    public async Task BackgroundKeepsPendingNavigationAndRunsAfterTheLatestForeground()
    {
        var backend = new ControlledBackend();
        var completions = new ConcurrentQueue<PreviewCompletion>();
        var clock = new NavigationClock();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.Completed += completions.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        controller.Submit(Request(10), background: true);
        controller.Submit(Request(2), navigation: true);
        first.End();
        var latest = await backend.NextAsync();
        latest.End();
        var background = await backend.NextAsync();
        background.End();
        await controller.WaitForIdleAsync();
        Assert.Contains(completions, item => item.Request.TargetSeconds == 2);
        Assert.Contains(completions, item => item.Request.TargetSeconds == 10);
        Assert.DoesNotContain(completions, item => item.Request.TargetSeconds == 1);
    }

    [Fact]
    public async Task EditingCancelsRunningBackgroundAndSuppressesItsResult()
    {
        var backend = new ControlledBackend();
        var frames = new ConcurrentQueue<PreviewFrameResult>();
        var completions = new ConcurrentQueue<PreviewCompletion>();
        var clock = new NavigationClock();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.FrameReady += frames.Enqueue;
        controller.Completed += completions.Enqueue;
        controller.Submit(Request(0), background: true);
        var background = await backend.NextAsync();
        controller.Submit(Request(1), bypassDebounce: true);
        background.End();
        var foreground = await backend.NextAsync();
        foreground.End();
        await controller.WaitForIdleAsync();
        Assert.Equal(1, Assert.Single(frames).Request.TargetSeconds);
        Assert.Equal(1, Assert.Single(completions).Request.TargetSeconds);
    }

    [Fact]
    public async Task BackgroundExternalCancellationReachesTheRunningBackend()
    {
        var backend = new ControlledBackend();
        var frames = new ConcurrentQueue<PreviewFrameResult>();
        using var cancellation = new CancellationTokenSource();
        var clock = new NavigationClock();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.FrameReady += frames.Enqueue;
        controller.Submit(Request(0), background: true, cancellationToken: cancellation.Token);
        var background = await backend.NextAsync();
        cancellation.Cancel();
        background.End();
        await controller.WaitForIdleAsync();
        Assert.Empty(frames);
    }

    [Fact]
    public async Task DisposeCollectsForegroundAndBackgroundWaitersWithoutAdvancingFakeTime()
    {
        var backend = new ControlledBackend();
        var clock = new NavigationClock();
        var controller = new PreviewController(backend, timeProvider: clock);
        await using var teardown = new ControllerLifetime(controller, clock);
        controller.Submit(Request(0));
        controller.Submit(Request(10), background: true);
        await controller.DisposeAsync().AsTask();
        Assert.Equal(0, backend.Starts);
    }

    private sealed class ControllerLifetime(PreviewController controller, NavigationClock clock) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Task dispose = controller.DisposeAsync().AsTask();
            // このstubはcancelを観測するだけで終了しない。既定の協調猶予を時計で進めてAbort回収する。
            Task winner = await Task.WhenAny(dispose, clock.GraceArmed.Task);
            if (ReferenceEquals(winner, clock.GraceArmed.Task)) clock.Advance(TimeSpan.FromSeconds(2));
            await dispose;
            Assert.False(controller.ShutdownIncomplete);
        }
    }

    private sealed class NavigationClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        public TaskCompletionSource GraceArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public void Advance(TimeSpan delta) => _clock.Advance(delta);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = _clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(2)) GraceArmed.TrySetResult();
            return timer;
        }
    }

    private static PreviewRequest Request(double time) => new()
    { EncoderPath = "fake", InputFile = "input.ts", Fps = 30, TargetSeconds = time };

    private sealed class ControlledBackend : IPreviewBackend
    {
        private readonly Channel<Run> _started = Channel.CreateUnbounded<Run>();
        private readonly ConcurrentDictionary<Run, bool> _active = new();
        private int _cancels;
        private int _starts;
        public int Starts => Volatile.Read(ref _starts);
        public int Cancels => Volatile.Read(ref _cancels);
        public Task<Run> NextAsync() => _started.Reader.ReadAsync().AsTask();
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            var run = new Run(request, sink, cancellationToken);
            _active.TryAdd(run, true);
            using var registration = cancellationToken.Register(() => Interlocked.Increment(ref _cancels));
            Interlocked.Increment(ref _starts);
            _started.Writer.TryWrite(run);
            try { return await run.Done.Task; }
            finally { _active.TryRemove(run, out _); }
        }
        public ValueTask AbortAsync()
        {
            foreach (var run in _active.Keys) run.Done.TrySetResult(new(-12, "cancelled", true));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => AbortAsync();
    }

    private sealed class Run(PreviewRequest request, IPreviewSink sink, CancellationToken token)
    {
        public PreviewRequest Request => request;
        public CancellationToken Token => token;
        public void Fail() => Done.TrySetResult(new(-1, "failed"));
        public TaskCompletionSource<PreviewRunResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void End()
        {
            var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
            double target = CommandBuilder.Plan(request).ActualPreMargin;
            sink.OnOutput();
            sink.OnHeader(header);
            sink.OnFrame(new(header, target, 1.0 / 30, new byte[] { 16, 16, 16, 16, 128, 128 }));
            sink.OnFrame(new(header, target + 1.0 / 30, 1.0 / 30, new byte[] { 32, 32, 32, 32, 128, 128 }));
            Done.TrySetResult(new(0, "completed"));
        }
    }
}

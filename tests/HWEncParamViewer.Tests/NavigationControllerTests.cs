using System.Collections.Concurrent;
using System.Threading.Channels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;
using Xunit.Abstractions;

namespace HWEncParamViewer.Tests;

public sealed class NavigationControllerTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task NewNavigationDoesNotCancelTheAlreadyRunningRequest()
    {
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        // 移動の追加でも実行中の要求が完了まで保持される。
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        output.WriteLine($"2件目Submit直後: 先行token中断={first.Token.IsCancellationRequested}, cancellation通知={backend.Cancels}");
        Assert.False(first.Token.IsCancellationRequested, "移動の追加で実行中の要求が取り消された。");
        first.End();
    }

    [Fact]
    public async Task FinishedRunningNavigationCanPublishWhileANewerRequestIsPending()
    {
        var backend = new ControlledBackend();
        var cache = new FrameCache();
        var images = new ConcurrentQueue<PreviewFrameResult>();
        await using var controller = new PreviewController(backend, cache: cache, timeProvider: new FakeTimeProvider());
        controller.FrameReady += images.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        // 次の移動が待っていても、先行要求の正常な画像を表示する。
        controller.Submit(Request(1), navigation: true);
        first.End();
        async Task WaitUntilStoredAsync()
        {
            while (cache.Count == 0) await Task.Yield();
        }
        await WaitUntilStoredAsync().WaitAsync(Safety);
        output.WriteLine($"先行要求の成功保存={cache.Count}, cancellation通知={backend.Cancels}, FrameReady={images.Count}");
        Assert.False(first.Token.IsCancellationRequested);
        Assert.Single(images);
    }

    [Fact]
    public async Task NavigationKeepsOnlyTheLatestWaitingRequestWithoutAdvancingTheClock()
    {
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        controller.Submit(Request(2), navigation: true);
        controller.Submit(Request(3), navigation: true);
        Assert.False(first.Token.IsCancellationRequested);
        Assert.Equal(1, backend.Starts);
        first.End();
        var latest = await backend.NextAsync();
        Assert.Equal(3, latest.Request.TargetSeconds);
        latest.End();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(2, backend.Starts);
    }

    [Fact]
    public async Task EditingCancelsTheRunningRequestAndSuppressesItsNotifications()
    {
        var backend = new ControlledBackend();
        var images = new ConcurrentQueue<PreviewFrameResult>();
        var completions = new ConcurrentQueue<PreviewCompletion>();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        controller.FrameReady += images.Enqueue;
        controller.Completed += completions.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        controller.Submit(Request(2), bypassDebounce: true);
        Assert.True(first.Token.IsCancellationRequested);
        first.End();
        var edit = await backend.NextAsync();
        Assert.Equal(2, edit.Request.TargetSeconds);
        edit.End();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(2, Assert.Single(images).Request.TargetSeconds);
        Assert.Equal(2, Assert.Single(completions).Request.TargetSeconds);
        Assert.Equal(2, backend.Starts);
    }

    [Fact]
    public async Task EditingImmediatelySuppressesOldNavigationDuringItsDebounce()
    {
        var backend = new ControlledBackend();
        var clock = new FakeTimeProvider();
        var images = new ConcurrentQueue<PreviewFrameResult>();
        var completions = new ConcurrentQueue<PreviewCompletion>();
        await using var controller = new PreviewController(backend, timeProvider: clock);
        controller.FrameReady += images.Enqueue;
        controller.Completed += completions.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1));
        Assert.False(first.Token.IsCancellationRequested);
        first.End();
        Assert.Empty(images);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        var edit = await backend.NextAsync();
        Assert.Empty(completions);
        edit.End();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(1, Assert.Single(images).Request.TargetSeconds);
        Assert.Equal(1, Assert.Single(completions).Request.TargetSeconds);
    }

    [Fact]
    public async Task OnlyTheCurrentRequestPublishesFailure()
    {
        var backend = new ControlledBackend();
        var failures = new ConcurrentQueue<PreviewFailure>();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        controller.Failed += failures.Enqueue;
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        first.Fail();
        var latest = await backend.NextAsync();
        Assert.Empty(failures);
        latest.Fail();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(1, Assert.Single(failures).Request.TargetSeconds);
    }

    [Fact]
    public async Task EachNavigationPublishesFrameThenCompletionWithoutReversingGenerations()
    {
        var backend = new ControlledBackend();
        var notifications = new ConcurrentQueue<string>();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        controller.FrameReady += frame => notifications.Enqueue($"frame:{frame.Request.TargetSeconds}");
        controller.Completed += result => notifications.Enqueue($"completed:{result.Request.TargetSeconds}");
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        first.End();
        var latest = await backend.NextAsync();
        latest.End();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(new[] { "frame:0", "completed:0", "frame:1", "completed:1" }, notifications);
    }

    [Fact]
    public async Task EditingFromFrameHandlerSuppressesTheOldCompletion()
    {
        var backend = new ControlledBackend();
        var notifications = new ConcurrentQueue<string>();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
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
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(new[] { "frame:0", "frame:1", "completed:1" }, notifications);
    }

    [Fact]
    public async Task BackgroundSubmitDoesNotCancelOrInvalidateRunningForeground()
    {
        var backend = new ControlledBackend();
        var frames = new ConcurrentQueue<PreviewFrameResult>();
        var completions = new ConcurrentQueue<PreviewCompletion>();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
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
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(new[] { 0.0, 10.0 }, completions.Select(value => value.Request.TargetSeconds));
    }

    [Fact]
    public async Task BackgroundKeepsPendingNavigationAndRunsAfterTheLatestForeground()
    {
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        controller.Submit(Request(0), navigation: true);
        var first = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        controller.Submit(Request(10), background: true);
        controller.Submit(Request(2), navigation: true);
        Assert.False(first.Token.IsCancellationRequested);
        first.End();
        var latest = await backend.NextAsync();
        Assert.Equal(2, latest.Request.TargetSeconds);
        latest.End();
        var background = await backend.NextAsync();
        Assert.Equal(10, background.Request.TargetSeconds);
        background.End();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(3, backend.Starts);
    }

    [Fact]
    public async Task BackgroundWaitsForForegroundDebounceWithoutReplacingIt()
    {
        var backend = new ControlledBackend();
        var clock = new FakeTimeProvider();
        await using var controller = new PreviewController(backend, timeProvider: clock);
        controller.Submit(Request(0));
        controller.Submit(Request(10), background: true);
        Assert.Equal(0, backend.Starts);
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(0, backend.Starts);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var foreground = await backend.NextAsync();
        Assert.Equal(0, foreground.Request.TargetSeconds);
        foreground.End();
        var background = await backend.NextAsync();
        Assert.Equal(10, background.Request.TargetSeconds);
        background.End();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
    }

    [Fact]
    public async Task NavigationPreservesRunningBackgroundAndBothNotificationOrders()
    {
        var backend = new ControlledBackend();
        var completions = new ConcurrentQueue<PreviewCompletion>();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        controller.Completed += completions.Enqueue;
        controller.Submit(Request(0), background: true);
        var background = await backend.NextAsync();
        controller.Submit(Request(1), navigation: true);
        Assert.False(background.Token.IsCancellationRequested);
        background.End();
        var foreground = await backend.NextAsync();
        Assert.Equal(1, foreground.Request.TargetSeconds);
        foreground.End();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(new[] { 0.0, 1.0 }, completions.Select(value => value.Request.TargetSeconds));
    }

    [Fact]
    public async Task EditingCancelsRunningBackgroundAndSuppressesItsResult()
    {
        var backend = new ControlledBackend();
        var frames = new ConcurrentQueue<PreviewFrameResult>();
        var completions = new ConcurrentQueue<PreviewCompletion>();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        controller.FrameReady += frames.Enqueue;
        controller.Completed += completions.Enqueue;
        controller.Submit(Request(0), background: true);
        var background = await backend.NextAsync();
        controller.Submit(Request(1), bypassDebounce: true);
        Assert.True(background.Token.IsCancellationRequested);
        background.End();
        var foreground = await backend.NextAsync();
        foreground.End();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(1, Assert.Single(frames).Request.TargetSeconds);
        Assert.Equal(1, Assert.Single(completions).Request.TargetSeconds);
    }

    [Fact]
    public async Task BackgroundExternalCancellationRemovesWaitingWorkOnly()
    {
        var backend = new ControlledBackend();
        using var cancellation = new CancellationTokenSource();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        controller.Submit(Request(0), navigation: true);
        var foreground = await backend.NextAsync();
        controller.Submit(Request(10), background: true, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        Assert.False(foreground.Token.IsCancellationRequested);
        foreground.End();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(1, backend.Starts);
    }

    [Fact]
    public async Task BackgroundExternalCancellationReachesTheRunningBackend()
    {
        var backend = new ControlledBackend();
        var frames = new ConcurrentQueue<PreviewFrameResult>();
        using var cancellation = new CancellationTokenSource();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        controller.FrameReady += frames.Enqueue;
        controller.Submit(Request(0), background: true, cancellationToken: cancellation.Token);
        var background = await backend.NextAsync();
        cancellation.Cancel();
        Assert.True(background.Token.IsCancellationRequested);
        background.End();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Empty(frames);
    }

    [Fact]
    public async Task DisposeCollectsForegroundAndBackgroundWaitersWithoutAdvancingFakeTime()
    {
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, timeProvider: new FakeTimeProvider());
        controller.Submit(Request(0));
        controller.Submit(Request(10), background: true);
        await controller.DisposeAsync().AsTask().WaitAsync(Safety);
        Assert.Equal(0, backend.Starts);
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
        public Task<Run> NextAsync() => _started.Reader.ReadAsync().AsTask().WaitAsync(Safety);
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

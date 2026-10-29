using System.Threading.Channels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.Tests;

public sealed class PreviewControllerTests
{
    private static PreviewRequest Request(string vpp = "a", double target = 1) => new()
    {
        EncoderPath = "fake", InputFile = "in", Fps = 2, TargetSeconds = target,
        PreMarginSeconds = 2, PostMarginSeconds = 1, VppArgs = new[] { vpp },
    };
    private static Y4mFrame Frame(double time, byte value = 0) =>
        new(new Y4mHeader { Width = 2, Height = 2 }, time, 0.5, Enumerable.Repeat(value, 6).ToArray());

    [Fact]
    public async Task DebounceKeepsOnlyLastAndSpacedRequestsRun()
    {
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, timeProvider: clock);
        controller.Submit(Request("a"));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        controller.Submit(Request("b"));
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(0, backend.Calls);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var b = await backend.NextAsync();
        Assert.Equal("b", b.Request.VppArgs[0]);
        b.End(0);
        await controller.WaitForIdleAsync();
        controller.Submit(Request("c"));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        var c = await backend.NextAsync();
        c.End(0);
        await controller.WaitForIdleAsync();
        Assert.Equal(2, backend.Calls);
    }

    [Fact]
    public async Task SupersededRequestCannotPublishOrPopulateVisibleResult()
    {
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var images = new List<PreviewFrameResult>();
        var completions = new List<PreviewCompletion>();
        controller.FrameReady += images.Add;
        controller.Completed += completions.Add;
        controller.Submit(Request("old"));
        var old = await backend.NextAsync();
        controller.Submit(Request("new"));
        await old.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        old.Sink.OnFrame(Frame(0));
        old.Sink.OnFrame(Frame(1.5));
        old.End(0);
        var current = await backend.NextAsync();
        current.Sink.OnFrame(Frame(1, 42));
        current.End(0);
        await controller.WaitForIdleAsync();
        Assert.Single(images);
        Assert.Equal("new", images[0].Request.VppArgs[0]);
        Assert.Single(completions);
        Assert.Equal("new", completions[0].Request.VppArgs[0]);
    }

    [Fact]
    public async Task ConfirmsTargetBeforeEndAndHandlesDecimatedTimestamps()
    {
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var selected = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;
        controller.FrameReady += f => selected.TrySetResult(f);
        controller.Completed += _ => ++completed;
        controller.Submit(Request(target: 1));
        var run = await backend.NextAsync();
        run.Sink.OnFrame(Frame(0));
        run.Sink.OnFrame(Frame(0.9, 7));
        run.Sink.OnFrame(Frame(1.8));
        var image = await selected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, image.Frame.Data[0]);
        Assert.False(image.TargetNotReached);
        Assert.Equal(0, completed);
        run.End(0);
        await controller.WaitForIdleAsync();
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task EndBeforeTargetUsesLastFrameAndMarksResult()
    {
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFrameResult? image = null;
        PreviewCompletion? result = null;
        controller.FrameReady += f => image = f;
        controller.Completed += r => result = r;
        controller.Submit(Request(target: 2));
        var run = await backend.NextAsync();
        run.Sink.OnFrame(Frame(0));
        run.Sink.OnFrame(Frame(0.5, 9));
        run.End(0);
        await controller.WaitForIdleAsync();
        Assert.NotNull(image);
        Assert.Equal(9, image.Frame.Data[0]);
        Assert.True(image.TargetNotReached);
        Assert.True(result!.TargetNotReached);
    }

    [Fact]
    public async Task MissingTimestampsUseFrameRate()
    {
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFrameResult? image = null;
        controller.FrameReady += f => image = f;
        controller.Submit(Request(target: 0.5));
        var run = await backend.NextAsync();
        for (byte i = 0; i < 4; ++i)
            run.Sink.OnFrame(new(new Y4mHeader { Width = 2, Height = 2 }, null, null, Enumerable.Repeat(i, 6).ToArray()));
        run.End(0);
        await controller.WaitForIdleAsync();
        Assert.Equal(1, image!.Frame.Data[0]);
    }

    [Fact]
    public async Task OnlySuccessfulRequestsAreCached()
    {
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var completions = new List<PreviewCompletion>();
        int failures = 0;
        controller.Completed += completions.Add;
        controller.Failed += _ => ++failures;
        controller.Submit(Request());
        var first = await backend.NextAsync();
        first.Sink.OnFrame(Frame(1));
        first.End(-24);
        await controller.WaitForIdleAsync();
        controller.Submit(Request());
        var second = await backend.NextAsync();
        second.Sink.OnFrame(Frame(1));
        second.End(0);
        await controller.WaitForIdleAsync();
        controller.Submit(Request());
        await controller.WaitForIdleAsync();
        Assert.Equal(2, backend.Calls);
        Assert.Equal(1, failures);
        Assert.False(completions[0].FromCache);
        Assert.True(completions[1].FromCache);
    }

    [Fact]
    public async Task WatchdogCountsByteProgressAndAbortsWithoutSleeping()
    {
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, timeProvider: clock, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        controller.Failed += f => failure = f;
        controller.Submit(Request());
        var run = await backend.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(29));
        run.Sink.OnOutput();
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(failure);
        clock.Advance(TimeSpan.FromSeconds(1));
        await controller.WaitForIdleAsync();
        Assert.NotNull(failure);
        Assert.Contains("タイムアウト", failure.Reason);
        Assert.Equal(1, backend.Aborts);
    }

    [Fact]
    public async Task UnsupportedServerFallsBackOncePerEncoder()
    {
        var preferred = new UnsupportedBackend();
        var fallback = new ControlledBackend();
        await using var controller = new PreviewController(preferred, fallback, debounce: TimeSpan.Zero);
        foreach (string option in new[] { "a", "b" })
        {
            controller.Submit(Request(option));
            var run = await fallback.NextAsync();
            run.End(0);
            await controller.WaitForIdleAsync();
        }
        Assert.Equal(1, preferred.Calls);
        controller.Submit(Request("c") with { EncoderPath = "other" });
        var other = await fallback.NextAsync();
        other.End(0);
        await controller.WaitForIdleAsync();
        Assert.Equal(2, preferred.Calls);
    }

    [Fact]
    public async Task DisposeAbortsActiveRequestAndRejectsSubmit()
    {
        var backend = new ControlledBackend();
        var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        controller.Submit(Request());
        await backend.NextAsync();
        await controller.DisposeAsync();
        Assert.True(backend.Disposed);
        Assert.Throws<ObjectDisposedException>(() => controller.Submit(Request()));
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task MixedMissingTimestampsFollowFrameSelector()
    {
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFrameResult? selected = null;
        controller.FrameReady += frame => selected = frame;
        controller.Submit(Request(target: 1) with { Fps = 1 });
        var run = await backend.NextAsync();
        var header = new Y4mHeader { Width = 2, Height = 2 };
        run.Sink.OnFrame(new(header, 0, 0.1, new byte[6]));
        run.Sink.OnFrame(new(header, null, 0.1, new byte[6]));
        var expected = new Y4mFrame(header, 0.1, 0.1, new byte[6]);
        run.Sink.OnFrame(expected);
        run.Sink.OnFrame(new(header, 2, 0.1, new byte[6]));
        Assert.Same(expected, selected!.Frame);
        Assert.False(selected.TargetNotReached);
        run.End(0);
        await controller.WaitForIdleAsync();
    }

    [Fact]
    public async Task DisposeFromSynchronousCacheNotificationWaitsForCurrentExecution()
    {
        var backend = new ControlledBackend();
        var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        controller.Submit(Request());
        var run = await backend.NextAsync();
        run.Sink.OnFrame(Frame(1));
        run.End(0);
        await controller.WaitForIdleAsync();
        Task? disposal = null;
        controller.Completed += _ => disposal = controller.DisposeAsync().AsTask();
        controller.Submit(Request());
        Assert.NotNull(disposal);
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        await controller.WaitForIdleAsync();
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task QueuedOldWatchdogCallbackRechecksLatestProgress()
    {
        var clock = new QueuedClock();
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, timeProvider: clock, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        PreviewCompletion? completed = null;
        controller.Failed += value => failure = value;
        controller.Completed += value => completed = value;
        controller.Submit(Request());
        var run = await backend.NextAsync();
        // 旧期限のコールバックがキュー済みでも、直後の受信を優先する。
        clock.Advance(TimeSpan.FromSeconds(31));
        run.Sink.OnOutput();
        clock.FireQueued();
        run.End(0);
        await controller.WaitForIdleAsync();
        Assert.Null(failure);
        Assert.NotNull(completed);
    }

    private sealed class QueuedClock : TimeProvider
    {
        private long _ticks;
        private Action? _queued;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan time) => _ticks += time.Ticks;
        public void FireQueued() => _queued!();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _queued = () => callback(state);
            return new QueuedTimer();
        }
        private sealed class QueuedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ForcedRefreshBypassesCacheFilledByPreviousRunDuringDebounce()
    {
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend();
        var cache = new FrameCache();
        await using var controller = new PreviewController(backend, cache: cache, timeProvider: clock);
        var completions = new List<PreviewCompletion>();
        controller.Completed += completions.Add;
        controller.Submit(Request() with { CorrelationId = 1 });
        clock.Advance(TimeSpan.FromMilliseconds(150));
        var first = await backend.NextAsync();
        controller.Submit(Request() with { CorrelationId = 2 }, bypassCache: true);
        first.Sink.OnFrame(Frame(1, 3));
        first.End(0);
        async Task WaitForCacheAsync()
        {
            while (cache.Count == 0) await Task.Yield();
        }
        // 再要求のdebounce待ち中に旧要求が成功して保存される競合を再現する。
        await WaitForCacheAsync().WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        var second = await backend.NextAsync();
        Assert.Equal(2, second.Request.CorrelationId);
        second.Sink.OnFrame(Frame(1, 9));
        second.End(0);
        await controller.WaitForIdleAsync();
        Assert.Equal(2, backend.Calls);
        var completion = Assert.Single(completions);
        Assert.False(completion.FromCache);
        Assert.Equal(2, completion.Request.CorrelationId);
    }

    [Fact]
    public async Task InternalRequestCanStartImmediatelyWhileNormalRequestStillDebounces()
    {
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend();
        await using var controller = new PreviewController(backend, timeProvider: clock);
        controller.Submit(Request("internal"), bypassDebounce: true);
        var immediate = await backend.NextAsync();
        Assert.Equal("internal", immediate.Request.VppArgs[0]);
        immediate.End(0);
        await controller.WaitForIdleAsync();
        controller.Submit(Request("edited"));
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(1, backend.Calls);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var debounced = await backend.NextAsync();
        Assert.Equal("edited", debounced.Request.VppArgs[0]);
        debounced.End(0);
        await controller.WaitForIdleAsync();
        Assert.Equal(2, backend.Calls);
    }

    private sealed class UnsupportedBackend : IPreviewBackend
    {
        public int Calls { get; private set; }
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            ++Calls;
            throw new PreviewServerNotSupportedException("非対応");
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledBackend : IPreviewBackend
    {
        private readonly Channel<Invocation> _runs = Channel.CreateUnbounded<Invocation>();
        private Invocation? _active;
        public int Calls { get; private set; }
        public int Aborts { get; private set; }
        public bool Disposed { get; private set; }
        public Task<Invocation> NextAsync() => _runs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            ++Calls;
            var invocation = new Invocation(request, sink);
            _active = invocation;
            using var registration = cancellationToken.Register(() => invocation.Cancelled.TrySetResult());
            _runs.Writer.TryWrite(invocation);
            return await invocation.Done.Task;
        }
        public ValueTask AbortAsync()
        {
            ++Aborts;
            _active?.Done.TrySetResult(new(-12, "", true));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Invocation(PreviewRequest request, IPreviewSink sink)
    {
        public PreviewRequest Request { get; } = request;
        public IPreviewSink Sink { get; } = sink;
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PreviewRunResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void End(int code) => Done.TrySetResult(new(code, ""));
    }
}

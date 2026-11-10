using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class DisposeCooperationTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);
    private static PreviewRequest Request(bool continuous = false) => new()
    {
        EncoderPath = "dispose-stub", InputFile = "input", Fps = 2,
        Capabilities = new(continuous), TimestampOriginSeconds = continuous ? 1000 : null, Continuous = continuous,
    };

    [Fact]
    public async Task CooperativeCancellationDoesNotNeedForcedAbort()
    {
        var backend = new ControlledBackend();
        var clock = new DisposalClock();
        var controller = new PreviewController(backend, timeProvider: clock, debounce: TimeSpan.Zero, disposeGrace: Grace);
        try
        {
            controller.Submit(Request());
            await backend.Started.Task;
            await controller.DisposeAsync().AsTask().WaitAsync(Safety);
            Assert.True(backend.Cancelled);
            Assert.Equal(0, backend.Aborts);
            Assert.Equal(1, backend.Disposals);
        }
        finally { backend.Finish(); await controller.DisposeAsync(); }
    }

    [Fact]
    public async Task IgnoredCancellationAbortsOnlyAfterFakeClockGrace()
    {
        var backend = new ControlledBackend(ignoreCancellation: true);
        var clock = new DisposalClock();
        var controller = new PreviewController(backend, timeProvider: clock, debounce: TimeSpan.Zero, disposeGrace: Grace);
        try
        {
            controller.Submit(Request());
            await backend.Started.Task;
            Task dispose = controller.DisposeAsync().AsTask();
            await clock.GraceArmed.Task.WaitAsync(Safety);
            await backend.CancellationStarted.Task.WaitAsync(Safety);
            Assert.True(backend.Cancelled);
            Assert.Equal(0, backend.Aborts);
            clock.Advance(Grace - TimeSpan.FromMilliseconds(1));
            Assert.Equal(0, backend.Aborts);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await dispose.WaitAsync(Safety);
            Assert.Equal(1, backend.Aborts);
            Assert.Equal(1, backend.Disposals);
        }
        finally { backend.ReleaseAbort(); backend.Finish(); await controller.DisposeAsync(); }
    }

    [Fact]
    public async Task CompletedBackendIsNotAbortedWhenOtherBackendIgnoresCancellation()
    {
        var foreground = new ControlledBackend();
        var background = new ControlledBackend(ignoreCancellation: true);
        var clock = new DisposalClock();
        var controller = new PreviewController(foreground, backgroundBackend: background, timeProvider: clock,
            debounce: TimeSpan.Zero, disposeGrace: Grace);
        try
        {
            controller.Submit(Request(continuous: true));
            await foreground.Paused.Task.WaitAsync(Safety);
            controller.Submit(Request() with { VppArgs = new[] { "before" } }, background: true);
            await background.Started.Task.WaitAsync(Safety);
            Task dispose = controller.DisposeAsync().AsTask();
            await clock.GraceArmed.Task.WaitAsync(Safety);
            await foreground.Running!.WaitAsync(Safety);
            clock.Advance(Grace);
            await dispose.WaitAsync(Safety);
            Assert.Equal(0, foreground.Aborts);
            Assert.Equal(1, background.Aborts);
            Assert.Equal(1, foreground.Disposals);
            Assert.Equal(1, background.Disposals);
        }
        finally { foreground.Finish(); background.ReleaseAbort(); background.Finish(); await controller.DisposeAsync(); }
    }

    [Fact]
    public async Task BlockedAbortDoesNotDelayDisposalOfCompletedOtherBackend()
    {
        var foreground = new ControlledBackend();
        var background = new ControlledBackend(ignoreCancellation: true, blockAbort: true);
        var clock = new DisposalClock();
        var controller = new PreviewController(foreground, backgroundBackend: background, timeProvider: clock,
            debounce: TimeSpan.Zero, disposeGrace: Grace);
        try
        {
            controller.Submit(Request(continuous: true));
            await foreground.Paused.Task.WaitAsync(Safety);
            controller.Submit(Request() with { VppArgs = new[] { "before" } }, background: true);
            await background.Started.Task.WaitAsync(Safety);
            Task dispose = controller.DisposeAsync().AsTask();
            await clock.GraceArmed.Task.WaitAsync(Safety);
            await foreground.Running!.WaitAsync(Safety);
            clock.Advance(Grace);
            await background.AbortStarted.Task.WaitAsync(Safety);
            await foreground.Disposed.Task.WaitAsync(Safety);
            Assert.False(dispose.IsCompleted);
            background.ReleaseAbort();
            await dispose.WaitAsync(Safety);
            Assert.Equal(0, foreground.Aborts);
            Assert.Equal(1, background.Aborts);
        }
        finally { foreground.Finish(); background.ReleaseAbort(); background.Finish(); await controller.DisposeAsync(); }
    }

    [Fact]
    public async Task MultipleDisposeCallsShareOneShutdown()
    {
        var backend = new ControlledBackend(ignoreCancellation: true);
        var clock = new DisposalClock();
        var controller = new PreviewController(backend, timeProvider: clock, debounce: TimeSpan.Zero, disposeGrace: Grace);
        try
        {
            controller.Submit(Request());
            await backend.Started.Task;
            Task first = controller.DisposeAsync().AsTask();
            Task second = controller.DisposeAsync().AsTask();
            Assert.Same(first, second);
            await clock.GraceArmed.Task.WaitAsync(Safety);
            clock.Advance(Grace);
            await Task.WhenAll(first, second).WaitAsync(Safety);
            Assert.Equal(1, backend.Aborts);
            Assert.Equal(1, backend.Disposals);
        }
        finally { backend.ReleaseAbort(); backend.Finish(); await controller.DisposeAsync(); }
    }

    [Fact]
    public async Task UnresponsiveAbortAndRunReturnAtOverallDeadlineAndAllowLateRelease()
    {
        var backend = new ControlledBackend(ignoreCancellation: true, blockAbort: true);
        var clock = new DisposalClock();
        var controller = new PreviewController(backend, timeProvider: clock, debounce: TimeSpan.Zero,
            disposeGrace: Grace, disposeTimeout: TimeSpan.FromSeconds(1));
        controller.Submit(Request());
        await backend.Started.Task.WaitAsync(Safety);
        Task dispose = controller.DisposeAsync().AsTask();
        await clock.GraceArmed.Task.WaitAsync(Safety);
        clock.Advance(Grace);
        await backend.AbortStarted.Task.WaitAsync(Safety);
        await clock.DeadlineArmed.Task.WaitAsync(Safety);
        clock.Advance(TimeSpan.FromSeconds(1));
        await dispose.WaitAsync(Safety);
        Assert.True(controller.ShutdownIncomplete);
        // Disposeが期限で戻った後でも、実行finallyのsemaphore.Releaseは成功する。
        backend.ReleaseAbort();
        backend.Finish();
        await controller.WaitForIdleAsync().WaitAsync(Safety);
        await backend.Disposed.Task.WaitAsync(Safety);
    }

    [Fact]
    public async Task UnresponsiveBackendDisposeReturnsAtOverallDeadline()
    {
        var backend = new ControlledBackend(blockDispose: true);
        var clock = new DisposalClock();
        var controller = new PreviewController(backend, timeProvider: clock, debounce: TimeSpan.Zero,
            disposeGrace: Grace, disposeTimeout: TimeSpan.FromSeconds(1));
        controller.Submit(Request());
        await backend.Started.Task.WaitAsync(Safety);
        Task dispose = controller.DisposeAsync().AsTask();
        await backend.Disposed.Task.WaitAsync(Safety);
        await clock.DeadlineArmed.Task.WaitAsync(Safety);
        clock.Advance(TimeSpan.FromSeconds(1));
        await dispose.WaitAsync(Safety);
        Assert.True(controller.ShutdownIncomplete);
        Assert.Equal(0, backend.Aborts);
        backend.ReleaseDispose();
    }

    [Fact]
    public async Task BlockingCancellationCallbackCannotBlockDisposeDeadline()
    {
        var backend = new ControlledBackend(blockCancellation: true);
        var clock = new DisposalClock();
        var controller = new PreviewController(backend, timeProvider: clock, debounce: TimeSpan.Zero,
            disposeGrace: Grace, disposeTimeout: TimeSpan.FromSeconds(1));
        controller.Submit(Request());
        await backend.Started.Task.WaitAsync(Safety);
        Task dispose = controller.DisposeAsync().AsTask();
        try
        {
            await backend.CancellationStarted.Task.WaitAsync(Safety);
            await clock.GraceArmed.Task.WaitAsync(Safety);
            clock.Advance(Grace);
            await clock.DeadlineArmed.Task.WaitAsync(Safety);
            clock.Advance(TimeSpan.FromSeconds(1));
            await dispose.WaitAsync(Safety);
            Assert.True(controller.ShutdownIncomplete);
        }
        finally { backend.ReleaseCancellation(); backend.Finish(); }
    }

    [Fact]
    public async Task DisposeDoesNotRequireCallingSynchronizationContextToPump()
    {
        var controller = new PreviewController(new ControlledBackend(), debounce: TimeSpan.Zero);
        var context = new QueuedContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task dispose;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            dispose = controller.DisposeAsync().AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        try
        {
            Task winner = await Task.WhenAny(dispose, context.Posted.Task).WaitAsync(Safety);
            Assert.Same(dispose, winner);
            await dispose.WaitAsync(Safety);
        }
        finally
        {
            // 修正前でもUIへ残した継続を回収し、テスト自身の未完了処理を残さない。
            context.Drain();
            await dispose.WaitAsync(Safety);
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state)
        { _queue.Enqueue((callback, state)); Posted.TrySetResult(); }
        public void Drain()
        { while (_queue.TryDequeue(out var item)) item.Callback(item.State); }
    }

    private sealed class DisposalClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        public TaskCompletionSource DeadlineArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource GraceArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public void Advance(TimeSpan delta) => _clock.Advance(delta);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = _clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime == Grace) GraceArmed.TrySetResult();
            if (dueTime > Grace && dueTime <= TimeSpan.FromSeconds(1) && period == Timeout.InfiniteTimeSpan) DeadlineArmed.TrySetResult();
            return timer;
        }
    }

    private sealed class ControlledBackend(bool ignoreCancellation = false, bool blockAbort = false, bool blockDispose = false, bool blockCancellation = false) : IPreviewBackend
    {
        private readonly TaskCompletionSource<PreviewRunResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAbort = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AbortStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewRunResult>? Running { get; private set; }
        public bool Cancelled { get; private set; }
        public int Aborts { get; private set; }
        public int Disposals { get; private set; }
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => Running = RunCoreAsync(request, sink, cancellationToken);
        private async Task<PreviewRunResult> RunCoreAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token)
        {
            using var registration = token.Register(() =>
            {
                Cancelled = true;
                CancellationStarted.TrySetResult();
                // 終了不能な同期callbackを意図的に再現し、Dispose呼出元を占有しないことを確認する。
                if (blockCancellation) _releaseCancellation.Task.GetAwaiter().GetResult();
                if (!ignoreCancellation) Finish();
            });
            Started.TrySetResult();
            if (request.Continuous)
            {
                var header = new Y4mHeader { Width = 2, Height = 2 };
                try
                {
                    for (int i = 0; ; ++i)
                    {
                        token.ThrowIfCancellationRequested();
                        ValueTask gate = sink.BeforeReadAsync(token);
                        if (!gate.IsCompleted) { sink.OnReadStateChanged(false); Paused.TrySetResult(); }
                        await gate;
                        sink.OnReadStateChanged(true);
                        sink.OnOutput();
                        sink.OnFrame(new(header, 1000 + i * 0.5, 0.5, new byte[6]));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
            return await _done.Task.ConfigureAwait(false);
        }
        public void Finish() => _done.TrySetResult(new(-12, "END", true));
        public void ReleaseCancellation() => _releaseCancellation.TrySetResult();
        public void ReleaseDispose() => _releaseDispose.TrySetResult();
        public void ReleaseAbort() => _releaseAbort.TrySetResult();
        public async ValueTask AbortAsync()
        {
            ++Aborts;
            AbortStarted.TrySetResult();
            if (blockAbort) await _releaseAbort.Task.ConfigureAwait(false);
            Finish();
        }
        public async ValueTask DisposeAsync()
        { ++Disposals; Disposed.TrySetResult(); if (blockDispose) await _releaseDispose.Task.ConfigureAwait(false); }
    }
}

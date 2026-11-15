using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class CoreSelectionResultTests
{
    [Fact]
    public async Task キャッシュ通知中に区間が消えても選択した画素を渡し終了後に回収する()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 4096);
        var request = new PreviewRequest
        {
            EncoderPath = "fake", InputFile = "input", TargetSeconds = 1, Fps = 30,
            Capabilities = new(TimestampPassthrough: true), TimestampOriginSeconds = 1000,
        };
        var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
        var producers = Enumerable.Range(0, 4).Select(index => scope.Create(header, 1000 + index,
            1d / 30, Enumerable.Repeat((byte)index, 6).ToArray(), scope.NewRunId(), "試験の読取")).ToArray();
        cache.StoreSegment(request, producers.Select(item => item.Frame).ToArray(), new(1000, 0, true), 0, 4);
        foreach (var producer in producers) producer.Dispose();
        var backend = new UnusedBackend();
        var controller = new PreviewController(scope, backend, backend, cache, debounce: TimeSpan.Zero);
        byte[]? image = null;
        controller.OwnedFrameReady += envelope =>
        {
            // 購読中のキャッシュ消去は実際に起こり得るため、選択済み画像の寿命を確かめる。
            cache.Clear();
            image = envelope.FrameReady!.Frame.RawPixels.ToArray();
        };
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        await controller.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        cache.Clear();
        Assert.Equal(new byte[] { 1, 1, 1, 1, 1, 1 }, image);
        Assert.True(scope.Capture().IsEmpty);
    }

    [Fact]
    public async Task 連続読取の通知中に区間を消しても選択画素を渡し全て回収する()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 4096);
        var backend = new StreamBackend();
        var controller = new PreviewController(scope, backend, backend, cache, debounce: TimeSpan.Zero);
        var selected = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.OwnedFrameReady += envelope =>
        {
            cache.Clear();
            selected.TrySetResult(envelope.FrameReady!.Frame.RawPixels.ToArray());
        };
        controller.Submit(new PreviewRequest
        {
            EncoderPath = "fake", InputFile = "input", TargetSeconds = 1, Fps = 30, Continuous = true,
            Capabilities = new(TimestampPassthrough: true), TimestampOriginSeconds = 1000, DependsOnHistory = false,
        });
        var image = await selected.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        await controller.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        cache.Clear();
        Assert.Equal(new byte[] { 42, 42, 42, 42, 42, 42 }, image);
        Assert.True(scope.Capture().IsEmpty);
    }

    [Fact]
    public async Task 読取停止時に所属が失われても失敗を通知して回収する()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 4096);
        var backend = new StreamBackend(failAfterEviction: true);
        var controller = new PreviewController(scope, backend, backend, cache, debounce: TimeSpan.Zero);
        var failed = new TaskCompletionSource<PreviewFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.OwnedFrameReady += _ => cache.Clear();
        controller.Failed += failure => failed.TrySetResult(failure);
        controller.Submit(new PreviewRequest
        {
            EncoderPath = "fake", InputFile = "input", TargetSeconds = 1, Fps = 30, Continuous = true,
            Capabilities = new(TimestampPassthrough: true), TimestampOriginSeconds = 1000, DependsOnHistory = false,
        });
        var failure = await failed.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        await controller.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        cache.Clear();
        Assert.Equal(CoreText.FrameUnavailable, failure.Reason);
        Assert.True(scope.Capture().IsEmpty);
    }

    [Fact]
    public async Task 停止中に区間を失っても次の移動で画像を表示し回収する()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 4096);
        var backend = new RestartBackend();
        var controller = new PreviewController(scope, backend, backend, cache, debounce: TimeSpan.Zero);
        var latest = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.OwnedFrameReady += envelope =>
        {
            var frame = envelope.FrameReady!;
            if (frame.Request.TargetSeconds == 2) latest.TrySetResult(frame.Frame.RawPixels.ToArray());
        };
        controller.Failed += failure => latest.TrySetException(new InvalidOperationException(failure.Reason, failure.Exception));
        var request = new PreviewRequest
        {
            EncoderPath = "fake", InputFile = "input", TargetSeconds = 1, Fps = 30, Continuous = true,
            Capabilities = new(TimestampPassthrough: true), TimestampOriginSeconds = 1000, DependsOnHistory = false,
        };
        controller.Submit(request);
        await backend.FirstPaused.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        cache.Clear();
        controller.Submit(request with { TargetSeconds = 2, CorrelationId = 2 }, navigation: true);
        var image = await latest.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        await controller.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        cache.Clear();
        Assert.Equal(new byte[] { 80, 80, 80, 80, 80, 80 }, image);
        Assert.True(scope.Capture().IsEmpty);
    }

    private sealed class RestartBackend : IPreviewBackend, IRawOwnedBackend
    {
        private int _runs;
        internal TaskCompletionSource FirstPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("所有権を持つ経路が必要です。");
        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink,
            RawOwnershipScope scope, CancellationToken cancellationToken = default)
        {
            int run = Interlocked.Increment(ref _runs);
            var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
            sink.OnHeader(header);
            try
            {
                for (int index = 0; ; index++)
                {
                    var permission = sink.BeforeReadAsync(cancellationToken);
                    if (!permission.IsCompleted)
                    {
                        sink.OnReadStateChanged(false);
                        if (run == 1) FirstPaused.TrySetResult();
                    }
                    await permission;
                    cancellationToken.ThrowIfCancellationRequested();
                    sink.OnReadStateChanged(true);
                    double time = (run == 1 ? 1 : 2) + index / 30d;
                    using var frame = scope.Create(header, 1000 + time, 1d / 30,
                        Enumerable.Repeat(run == 1 ? (byte)40 : (byte)80, 6).ToArray(), scope.NewRunId(), "試験の読取");
                    sink.OnOutput();
                    sink.OnFrame(frame.Frame);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new PreviewRunResult(-12, "", true);
            }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StreamBackend(bool failAfterEviction = false) : IPreviewBackend, IRawOwnedBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("所有権を持つ経路が必要です。");
        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink,
            RawOwnershipScope scope, CancellationToken cancellationToken = default)
        {
            var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
            sink.OnHeader(header);
            using (var frame = scope.Create(header, 1001, 1d / 30, Enumerable.Repeat((byte)42, 6).ToArray(), scope.NewRunId(), "試験の読取"))
            {
                sink.OnOutput();
                sink.OnFrame(frame.Frame);
            }
            if (failAfterEviction) sink.OnReadStateChanged(false);
            try
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            return new PreviewRunResult(-12, "", true);
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("準備したキャッシュから表示できませんでした。");
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

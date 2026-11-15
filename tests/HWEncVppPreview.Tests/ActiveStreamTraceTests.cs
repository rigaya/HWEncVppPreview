using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class ActiveStreamTraceTests
{
    [Fact]
    public async Task 重なる背景取得の後も連続読取を進め最新の画像を返す()
    {
        var stream = new SparseStream();
        var finite = new FiniteWindow();
        var cache = new FrameCache();
        var controller = new PreviewController(stream, cache, debounce: TimeSpan.Zero,
            backgroundBackend: finite);
        var backgroundDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Completed += result => { if (result.Request.CorrelationId == 2) backgroundDone.TrySetResult(); };
        controller.FrameReady += result =>
        {
            if (result.Request.CorrelationId == 3) latest.TrySetResult(result.Frame.Data.ToArray());
        };
        controller.Failed += failure => latest.TrySetException(new InvalidOperationException(failure.Reason, failure.Exception));
        var request = new PreviewRequest
        {
            EncoderPath = "fake", InputFile = "input", Fps = 10, TargetSeconds = 10,
            Continuous = true, Capabilities = new(TimestampPassthrough: true),
            TimestampOriginSeconds = 1000, DependsOnHistory = false,
        };
        byte[] image;
        try
        {
            controller.Submit(request);
            await stream.Paused.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            // 取得中の区間を保ったまま、重なりのある別の有限区間を受け取る。
            controller.Submit(request with { CorrelationId = 2, Continuous = false }, bypassCache: true, background: true);
            await backgroundDone.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            controller.Submit(request with { CorrelationId = 3, TargetSeconds = 12.2 }, navigation: true);
            image = await latest.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        }
        finally
        {
            await controller.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            cache.Clear();
        }
        Assert.All(image, value => Assert.Equal((byte)80, value));
        Assert.False(controller.ShutdownIncomplete);
    }

    private sealed class SparseStream : IPreviewBackend
    {
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 10, FpsDen = 1 };
            sink.OnHeader(header);
            try
            {
                for (int i = 0; ; i++)
                {
                    var permission = sink.BeforeReadAsync(cancellationToken);
                    if (!permission.IsCompleted) { sink.OnReadStateChanged(false); Paused.TrySetResult(); }
                    await permission;
                    cancellationToken.ThrowIfCancellationRequested();
                    sink.OnReadStateChanged(true);
                    double time = i == 0 ? 10 : 12.1 + (i - 1) * .1;
                    byte pixel = i < 2 ? (byte)40 : (byte)80;
                    sink.OnOutput();
                    sink.OnFrame(new(header, 1000 + time, .3, Enumerable.Repeat(pixel, 6).ToArray()));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(-12, "", true); }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FiniteWindow : IPreviewBackend
    {
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 10, FpsDen = 1 };
            sink.OnHeader(header);
            for (int i = 0; i < 3; i++)
            {
                sink.OnOutput();
                sink.OnFrame(new(header, 1010 + i * .1, .1, Enumerable.Repeat((byte)40, 6).ToArray()));
            }
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

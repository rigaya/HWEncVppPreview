using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class StreamMetadataReadTests
{
    private static PreviewRequest Request => new()
    {
        EncoderPath = "fake", InputFile = "input", Fps = 30, TargetSeconds = 0,
        Capabilities = new(TimestampPassthrough: true), TimestampOriginSeconds = 1000,
        Continuous = true, DependsOnHistory = false,
    };

    [Fact]
    public async Task byte予算で読取が止まっても終了時に全ての画像を回収する()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 96);
        var backend = new Backend(false);
        var controller = new PreviewController(scope, backend, cache, debounce: TimeSpan.Zero);
        controller.Submit(Request);
        await backend.Paused.Task;
        await controller.DisposeAsync().AsTask();
        cache.Clear();
        Assert.False(controller.ShutdownIncomplete);
        Assert.True(scope.Capture().IsEmpty);
        Assert.InRange(cache.SizeBytes, 0, cache.CapacityBytes);
    }

    [Fact]
    public async Task VFRの安全な候補がない端では永久停止せず失敗を返す()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 4096);
        var backend = new Backend(true);
        var failed = new TaskCompletionSource<PreviewFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new PreviewController(scope, backend, cache, debounce: TimeSpan.Zero);
        controller.Failed += value => failed.TrySetResult(value);
        controller.Submit(Request);
        var failure = await failed.Task;
        await controller.DisposeAsync().AsTask();
        cache.Clear();
        Assert.Equal(CoreText.FrameUnavailable, failure.Reason);
        Assert.False(controller.ShutdownIncomplete);
        Assert.True(scope.Capture().IsEmpty);
    }

    private sealed class Backend(bool sparse) : IPreviewBackend, IRawOwnedBackend
    {
        private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("所有権を持つ経路が必要です。");
        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink, RawOwnershipScope scope,
            CancellationToken cancellationToken = default)
        {
            sink.OnHeader(Header);
            long run = scope.NewRunId();
            try
            {
                for (int index = 0; index < 100; index++)
                {
                    var permission = sink.BeforeReadAsync(cancellationToken);
                    if (!permission.IsCompleted)
                    {
                        sink.OnReadStateChanged(false);
                        Paused.TrySetResult();
                    }
                    await permission;
                    sink.OnReadStateChanged(true);
                    double time = sparse ? (index + 1) * .5 : index / 30d;
                    using var frame = scope.Create(Header, 1000 + time, 1d / 30, new byte[6], run, "試験の読取");
                    sink.OnOutput();
                    sink.OnFrame(frame.Frame);
                }
                return new PreviewRunResult(0, "");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new PreviewRunResult(-12, "", true);
            }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

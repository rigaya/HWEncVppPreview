using HWEncParamViewer.Core;
namespace HWEncParamViewer.Tests;

public sealed class PreparationTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InputProbeBorrowsBackendWithoutNotificationsCacheOrDuplicateDispose()
    {
        var backend = new CountingBackend();
        var cache = new FrameCache();
        var controller = new PreviewController(backend, backend, cache);
        var probe = new InputProbe();
        int notifications = 0;
        controller.FrameReady += _ => notifications++;
        controller.Completed += _ => notifications++;
        controller.Failed += _ => notifications++;
        var info = await controller.ProbeInputAsync(probe, "encoder", "input.ts", ["--avhw"], new()).WaitAsync(Safety);
        Assert.Equal(30, info.Fps);
        Assert.Equal(1, backend.Calls);
        Assert.Equal(0, notifications);
        Assert.Equal(0, cache.Count);
        await probe.DisposeAsync();
        Assert.Equal(0, backend.Disposals);
        await controller.DisposeAsync();
        Assert.Equal(1, backend.Disposals);
        Assert.False(controller.ShutdownIncomplete);
    }

    [Fact]
    public async Task UnsupportedServerFallsBackAndRemembersTheEncoder()
    {
        var server = new CountingBackend(unsupported: true);
        var fallback = new CountingBackend();
        await using var controller = new PreviewController(server, fallback);
        await using var probe = new InputProbe();
        await controller.ProbeInputAsync(probe, "encoder", "a.ts", [], new()).WaitAsync(Safety);
        await controller.ProbeInputAsync(probe, "encoder", "b.ts", [], new()).WaitAsync(Safety);
        Assert.Equal(1, server.Calls);
        Assert.Equal(2, fallback.Calls);
    }

    [Fact]
    public async Task DisposeCancelsAndTracksBorrowedProbe()
    {
        var backend = new CountingBackend(hang: true);
        var controller = new PreviewController(backend, backend);
        await using var probe = new InputProbe();
        Task<InputInfo> pending = controller.ProbeInputAsync(probe, "encoder", "input.ts", [], new());
        await backend.Started.Task.WaitAsync(Safety);
        await controller.DisposeAsync().AsTask().WaitAsync(Safety);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, backend.Disposals);
        Assert.False(controller.ShutdownIncomplete);
    }

    private sealed class CountingBackend(bool unsupported = false, bool hang = false) : IPreviewBackend
    {
        public int Calls, Disposals;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Calls++;
            if (unsupported) throw new PreviewServerNotSupportedException("未対応のテストbackend");
            Started.TrySetResult();
            if (hang) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            var header = Y4mHeader.Parse("YUV4MPEG2 W4 H4 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            sink.OnFrame(new(header, 0, null, new byte[header.FrameBytes]));
            return new(0, "");
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}

using System.Runtime.CompilerServices;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class PrefetchSnapshotReleaseTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedPrefetchDoesNotKeepRawAfterCacheClear(bool dispose)
    {
        var (controller, weak) = await CreateCompletedPrefetch(dispose);
        try
        {
            // 強参照を持つ呼出元frameやbackendのlocalが戻ってから回収を確認する。
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(IsAlive(weak));
            GC.KeepAlive(controller);
        }
        finally { await controller.DisposeAsync(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<byte[]> weak) => weak.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(PreviewController Controller, WeakReference<byte[]> Weak)> CreateCompletedPrefetch(bool dispose)
    {
        var backend = new Backend();
        var cache = new FrameCache();
        var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        var request = new PreviewRequest
        {
            EncoderPath = "fake", InputFile = "input", Fps = 30, TargetSeconds = 10,
            Capabilities = new(true), TimestampOriginSeconds = 1000, Continuous = true,
            VppArgs = new[] { "--vpp-kfm" },
        };
        try
        {
            controller.Submit(request);
            await controller.WaitForIdleAsync().WaitAsync(Safety);
            for (int i = 1; i <= 5; i++) controller.NotifyNavigation(request with { TargetSeconds = 10 - i / 30d }, -1);
            await controller.WaitForIdleAsync().WaitAsync(Safety);
            var weak = backend.PrefetchedRaw;
            Assert.NotNull(weak);
            Assert.True(IsAlive(weak));
            cache.Clear();
            if (dispose) await controller.DisposeAsync();
            // 経過時間を待たず、TrackAsyncのfinallyが完了する機会だけを与える。
            await Task.Yield();
            return (controller, weak);
        }
        catch { await controller.DisposeAsync(); throw; }
    }

    private sealed class Backend : IPreviewBackend
    {
        private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
        public WeakReference<byte[]>? PrefetchedRaw;
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default)
            => Task.FromResult<bool?>(true);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => request.Continuous ? Stream(request, sink, token) : Task.FromResult(Finite(request, sink, token));
        private static async Task<PreviewRunResult> Stream(PreviewRequest request, IPreviewSink sink, CancellationToken token)
        {
            double start = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            try
            {
                for (int i = 0; ; i++)
                {
                    var gate = sink.BeforeReadAsync(token);
                    if (!gate.IsCompleted) sink.OnReadStateChanged(false);
                    await gate;
                    sink.OnReadStateChanged(true);
                    token.ThrowIfCancellationRequested();
                    sink.OnFrame(Frame(start + i / 30d));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(-12, "", true); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private PreviewRunResult Finite(PreviewRequest request, IPreviewSink sink, CancellationToken token)
        {
            var plan = CommandBuilder.Plan(request);
            for (int i = 0; i < plan.FrameCount; i++)
            {
                token.ThrowIfCancellationRequested();
                var frame = Frame((plan.SeekSeconds ?? 0) + i / 30d);
                if (i == 70) PrefetchedRaw = new(frame.Data);
                sink.OnFrame(frame);
            }
            return new(0, "");
        }
        private static Y4mFrame Frame(double time) => new(Header, 1000 + time, 1d / 30,
            Enumerable.Repeat((byte)7, Header.FrameBytes).ToArray());
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

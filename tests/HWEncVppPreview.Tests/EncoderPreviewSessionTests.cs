using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class EncoderPreviewSessionTests
{
    [Fact]
    public async Task 画面なしで実出力の隣へ進み選択時刻と画像を取り出せる()
    {
        var backend = new VariableRateBackend();
        var probe = new InputProbe(PreviewTimeouts.Infinite, TimeProvider.System, backend: new VariableRateBackend());
        await using var session = new EncoderPreviewSession(TimeProvider.System, PreviewTimeouts.Infinite,
            backend, probe, TimeSpan.Zero, (_, _, _) => Task.FromResult("QSVEnc 8.38"));

        var configured = await session.ConfigureAsync(new("fake", "source.avs", [], []));
        Assert.Equal(30, configured.Input?.FpsNum);
        await session.WaitForIdleAsync();

        var start = await session.GetStateAsync();
        using (var view = await session.AcquireViewAsync(start.ViewRevision))
        {
            Assert.NotNull(view?.After);
            Assert.Equal(0, view.After.Timing.InputSeconds, 8);
            Assert.Equal((byte)1, view.After.Frame.Data[0]);
        }

        var step = await session.StepAsync(1);
        await session.WaitForIdleAsync();
        Assert.Null(await session.AcquireViewAsync(start.ViewRevision));
        var comparison = await session.SetDisplayAsync(EncoderPreviewDisplayMode.SideBySide);
        Assert.Equal(step.Generation, comparison.Generation);
        using var frames = await session.AcquireViewAsync();
        Assert.NotNull(frames?.After);
        Assert.Equal(step.Generation, frames.After.Generation);
        Assert.Equal(0.1, frames.State.CurrentSeconds, 8);
        Assert.Equal(0.1, frames.After.Timing.InputSeconds, 8);
        Assert.Equal(0.1, frames.After.Timing.Xts);
        Assert.Equal(0.175, frames.After.Timing.Xdur);
        Assert.Equal((byte)2, frames.After.Frame.Data[0]);
        Assert.NotNull(frames.Before);
        Assert.Same(frames.After.Frame, frames.Before.Frame);
    }

    private sealed class VariableRateBackend : IPreviewBackend
    {
        private static readonly Y4mHeader Format = new()
        {
            Width = 4, Height = 4, FpsNum = 30, FpsDen = 1, SarNum = 1, SarDen = 1,
        };

        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink,
            CancellationToken cancellationToken = default)
        {
            sink.OnHeader(Format);
            var frames = new[] { (0.0, 0.1), (0.1, 0.175), (0.275, 0.05) };
            for (int i = 0; i < frames.Length && !cancellationToken.IsCancellationRequested; ++i)
            {
                var (time, duration) = frames[i];
                var pixels = new byte[Format.FrameBytes];
                Array.Fill(pixels, (byte)(i + 1));
                sink.OnFrame(new(Format, time, duration, pixels));
            }
            return Task.FromResult(new PreviewRunResult(0, "  Duration: 00:00:03.00, start: 0"));
        }

        public ValueTask AbortAsync() => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class FrameRateSelectionTests
{
    [Fact]
    public async Task SixtyFpsWithoutDurationSelectsExactTargetInsteadOfNextField()
    {
        var frames = Frames(60, 0, 1.0 / 60, 2.0 / 60);
        var (selected, _) = await SelectAsync(frames, 0);
        Assert.Same(frames[0], selected.Frame);
    }

    [Fact]
    public async Task TargetNotReachedUsesOutputRateWithoutDuration()
    {
        var frames = Frames(60, 0, 1.0 / 60);
        var (_, completed) = await SelectAsync(frames, 0.03);
        Assert.True(completed.TargetNotReached);
    }

    [Fact]
    public async Task MissingTimestampsUseOutputRateForFrameIndex()
    {
        var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 60, FpsDen = 1 };
        var frames = Enumerable.Range(0, 4).Select(_ => new Y4mFrame(header, null, null, new byte[6])).ToArray();
        var (selected, _) = await SelectAsync(frames, 2.0 / 60);
        Assert.Same(frames[2], selected.Frame);
    }

    [Theory]
    [InlineData(0.1, 60, 30, 0.1)]
    [InlineData(null, 60, 30, 1.0 / 60)]
    [InlineData(null, 24, 30, 1.0 / 24)]
    [InlineData(null, 0, 30, 1.0 / 30)]
    [InlineData(null, 0, 0, 0)]
    [InlineData(0.0, 60, 30, 1.0 / 60)]
    [InlineData(-1.0, 60, 30, 1.0 / 60)]
    public void DurationUsesFrameMetadataBeforeRequestedRate(double? duration, int headerFps, double requestFps, double expected)
    {
        var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = headerFps, FpsDen = 1 };
        var frame = new Y4mFrame(header, 0, duration, new byte[6]);
        Assert.Equal(expected, FrameSelector.Duration(frame, requestFps), 12);
    }

    [Fact]
    public async Task CompletionCarriesReadOnlyFramesEvenWhenLruIsDisabled()
    {
        var frames = Frames(60, 0, 1.0 / 60, 2.0 / 60);
        var (_, completion) = await SelectAsync(frames, 0);
        Assert.Equal(frames, completion.Frames);
        Assert.True(Assert.IsAssignableFrom<IList<Y4mFrame>>(completion.Frames).IsReadOnly);
    }

    [Fact]
    public void EachFrameUsesItsOwnOutputHeaderWhenDurationIsMissing()
    {
        var frames = Frames(60, 0).Concat(Frames(120, 1.0 / 120)).ToArray();
        Assert.Equal(0, FrameSelector.Select(frames, 0, 30));
    }

    private static Y4mFrame[] Frames(int fps, params double[] timestamps)
    {
        var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = fps, FpsDen = 1 };
        return timestamps.Select(time => new Y4mFrame(header, time, null, new byte[6])).ToArray();
    }

    private static async Task<(PreviewFrameResult Frame, PreviewCompletion Completion)> SelectAsync(Y4mFrame[] frames, double target)
    {
        await using var controller = new PreviewController(new Backend(frames), cache: new FrameCache(0), debounce: TimeSpan.Zero);
        PreviewFrameResult? frame = null;
        PreviewCompletion? completion = null;
        controller.FrameReady += value => frame = value;
        controller.Completed += value => completion = value;
        controller.Submit(new PreviewRequest { EncoderPath = "fake", InputFile = "input.ts", Fps = 30, TargetSeconds = target });
        await controller.WaitForIdleAsync();
        return (Assert.IsType<PreviewFrameResult>(frame), Assert.IsType<PreviewCompletion>(completion));
    }

    private sealed class Backend(Y4mFrame[] frames) : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            sink.OnHeader(frames[0].Header);
            foreach (var frame in frames) sink.OnFrame(frame);
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

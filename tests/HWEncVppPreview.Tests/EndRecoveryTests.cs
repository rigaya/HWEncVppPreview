using HWEncVppPreview.Core;
namespace HWEncVppPreview.Tests;

public sealed class EndRecoveryTests
{
    private static PreviewRequest Request(double target, bool continuous = false) => new()
    {
        EncoderPath = "encoder", InputFile = "video", Fps = 10, TargetSeconds = target,
        InputDurationSeconds = 20, TimestampOriginSeconds = 1000, TimestampOriginTrusted = true,
        Capabilities = new(true, true), Continuous = continuous,
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NaturalEndUsesLastFrameDespiteLongerContainerDuration(bool continuous, bool background)
    {
        var backend = new EndBackend(10);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        PreviewCompletion? completed = null;
        PreviewFrameResult? image = null;
        controller.Failed += value => failure = value;
        controller.Completed += value => completed = value;
        controller.FrameReady += value => image = value;
        controller.Submit(Request(10.5, continuous), background: background);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.NotNull(image);
        Assert.Equal(1009.9, image.Frame.Xts!.Value, 6);
        Assert.True(completed!.Segment!.ReachedInputEnd);
        int runs = backend.Requests.Count;
        controller.Submit(Request(20, continuous), background: background);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.Equal(runs, backend.Requests.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task EmptySeekBacktracksWithABoundedBudget(bool continuous, bool background)
    {
        var backend = new EndBackend(10);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        PreviewFrameResult? image = null;
        controller.Failed += value => failure = value;
        controller.FrameReady += value => image = value;
        controller.Submit(Request(20, continuous), background: background);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.NotNull(image);
        Assert.Equal(1009.9, image.Frame.Xts!.Value, 6);
        Assert.InRange(backend.Requests.Count, 2, 7);
        var seeks = backend.Requests.Select(request => CommandBuilder.Plan(request).SeekSeconds ?? 0).ToArray();
        Assert.True(seeks[^1] < seeks[0]);
    }

    [Fact]
    public async Task ExhaustedEmptyInputFailsWithoutLooping()
    {
        var backend = new EndBackend(0);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        controller.Failed += value => failure = value;
        controller.Submit(Request(100));
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.NotNull(failure);
        Assert.InRange(backend.Requests.Count, 2, 7);
    }

    [Fact]
    public async Task FrameLimitNearUnderstatedDurationDoesNotBecomeAnInputEnd()
    {
        var backend = new EndBackend(20);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        PreviewCompletion? completion = null;
        PreviewFrameResult? image = null;
        controller.Completed += value => completion = value;
        controller.FrameReady += value => image = value;
        controller.Submit(Request(9.5) with { InputDurationSeconds = 10 });
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.False(completion!.Segment!.ReachedInputEnd);
        controller.Submit(Request(15) with { InputDurationSeconds = 10 });
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(1015, image!.Frame.Xts);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task EndConfirmationStopsAsSoonAsTheTargetIsSafelyAvailable()
    {
        var backend = new EndBackend(2000, firstLimit: 3);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        PreviewCompletion? completion = null;
        controller.Failed += value => failure = value;
        controller.Completed += value => completion = value;
        controller.Submit(Request(10));
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.Equal(2, backend.Requests.Count);
        Assert.False(completion!.Segment!.ReachedInputEnd);
        Assert.InRange(backend.FramesRead, 4, 30);
    }

    [Fact]
    public void RescuedMiddleFrameDoesNotClaimToBeTheNaturalEnd()
    {
        var request = Request(9);
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F10:1 C420");
        var segment = new PreviewSegmentSnapshot(0, request, new(1000, 0, true),
            new[] { new Y4mFrame(header, 1009, 0.1, new byte[6]), new Y4mFrame(header, 1009.9, 0.1, new byte[6]) },
            9, 10, false, true);
        var rescued = new PreviewFrameReference(segment, 0).RetainFrameOnly().Segment;
        Assert.False(PreviewSafeRange.IsKnownInputEnd(request, rescued, 20));
        Assert.True(PreviewSafeRange.IsKnownInputEnd(request, segment, 20));
    }

    private sealed class EndBackend(double end, int? firstLimit = null) : IPreviewBackend
    {
        public List<PreviewRequest> Requests { get; } = new();
        public int FramesRead { get; private set; }
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default)
            => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Add(request);
            var plan = CommandBuilder.Plan(request);
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F10:1 C420");
            sink.OnHeader(header);
            int first = (int)Math.Ceiling((plan.SeekSeconds ?? 0) * 10 - 1e-8);
            int count = Requests.Count == 1 && firstLimit.HasValue ? firstLimit.Value : request.Continuous ? int.MaxValue : plan.FrameCount;
            for (int i = first; i < end * 10 && i - first < count; i++)
            {
                await sink.BeforeReadAsync(token);
                token.ThrowIfCancellationRequested();
                ++FramesRead;
                sink.OnFrame(new(header, 1000 + i / 10.0, 0.1, new byte[6]));
            }
            return new(0, "");
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

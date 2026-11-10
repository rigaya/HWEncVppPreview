using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class TimestampReplayTests
{
    private static PreviewRequest Request(string fault, bool continuous) => new()
    {
        EncoderPath = BackendTests.FakeEncoderPath(), InputFile = "wrap fake.ts", Fps = 30,
        TargetSeconds = 10, PreMarginSeconds = 2, PostMarginSeconds = 0.5,
        Capabilities = new(true), TimestampOriginSeconds = 1000, Continuous = continuous,
        CorrelationId = 73, InputArgs = new[] { fault, "4" },
    };

    [Theory]
    [InlineData(false, false, "--fake-clock-backstep-at")]
    [InlineData(false, true, "--fake-clock-jump-at")]
    [InlineData(true, false, "--fake-clock-jump-at")]
    [InlineData(true, true, "--fake-clock-backstep-at")]
    public async Task ImmediateIdleWaitIncludesAutomaticLegacyReplay(bool resident, bool continuous, string fault)
    {
        var backend = new RecordingBackend(resident ? new ServerBackend() : new OneShotBackend());
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var images = new List<PreviewFrameResult>();
        var completions = new List<PreviewCompletion>();
        var failures = new List<PreviewFailure>();
        controller.FrameReady += images.Add;
        controller.Completed += completions.Add;
        controller.Failed += failures.Add;
        var request = Request(fault, continuous);
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Empty(failures);
        var image = Assert.Single(images);
        var completed = Assert.Single(completions);
        Assert.True(image.IsReplay);
        Assert.True(completed.IsReplay);
        Assert.Equal(request.CorrelationId, image.Request.CorrelationId);
        Assert.True(image.Request.Capabilities.TimestampPassthrough);
        Assert.False(completed.Segment!.PhysicalRequest.Capabilities.TimestampPassthrough);
        Assert.False(completed.Segment.PhysicalRequest.Continuous);
        Assert.False(completed.Segment.Clock.TrustedAbsolute);
        Assert.InRange(completed.Segment.Clock.ToInputSeconds(image.Frame, 0, 30), 9.98, 10.02);
        Assert.Contains("[preview] timestamp fallback:", completed.Result.StandardError);
        Assert.True(controller.IsLegacySource(request));
        Assert.Equal(2, backend.Requests.Count);
        controller.Submit(request with { TargetSeconds = 11 }, navigation: true);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(3, backend.Requests.Count);
        Assert.False(backend.Requests[^1].Capabilities.TimestampPassthrough);
    }

    [Fact]
    public async Task PrefetchAnomalyAfterTargetDisplayStillFinishesReplayBeforeIdleReturns()
    {
        var backend = new RecordingBackend(new ServerBackend());
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var completions = new List<PreviewCompletion>();
        var images = new List<PreviewFrameResult>();
        controller.Completed += completions.Add;
        controller.FrameReady += images.Add;
        var request = Request("--fake-clock-jump-at", true) with { InputArgs = new[] { "--fake-clock-jump-at", "100" } };
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.True(Assert.Single(completions).IsReplay);
        Assert.Contains(images, image => !image.IsReplay);
        Assert.True(images[^1].IsReplay);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task ReplayReplansShortWindowAtLatestTargetInsteadOfKeepingStreamSeek()
    {
        var backend = new RecordingBackend(new ServerBackend());
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var request = Request("--fake-clock-jump-at", true) with
            { TargetSeconds = 0, InputArgs = new[] { "--fake-clock-jump-at", "1850" } };
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        for (int seconds = 2; seconds <= 60; seconds += 2)
        {
            controller.Submit(request with
            {
                TargetSeconds = seconds,
                SeekSecondsOverride = seconds == 60 ? 0 : null,
                PreMarginSeconds = seconds == 60 ? 60 : 2,
            }, navigation: true);
            await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        }
        Assert.Equal(2, backend.Requests.Count);
        var physical = backend.Requests[^1];
        Assert.False(physical.Capabilities.TimestampPassthrough);
        Assert.Null(physical.SeekSecondsOverride);
        Assert.Equal(57, CommandBuilder.Plan(physical).SeekSeconds);
        Assert.InRange(CommandBuilder.Plan(physical).FrameCount, 1, 200);
        Assert.Equal(60, physical.TargetSeconds);
        Assert.Equal(73, physical.CorrelationId);
    }

    [Theory]
    [InlineData(95443.7, true)]
    [InlineData(-95443.7, true)]
    [InlineData(95443.717688889 - 10, true)]
    [InlineData(95443.717688889 + 11, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void WrapRiskUsesNonzeroBoundaryAndNamedGuard(double origin, bool risky)
        => Assert.Equal(risky, PreviewClockMap.IsNearTimestampWrap(origin));

    [Fact]
    public async Task NearWrapOriginStartsWithPassOffAndKeepsNumericOrigin()
    {
        var backend = new RecordingBackend(new OneShotBackend());
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewCompletion? complete = null;
        controller.Completed += value => complete = value;
        var request = Request("--fake-clock-jump-at", true) with { TimestampOriginSeconds = 95443.7, TimestampOriginTrusted = false };
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Single(backend.Requests);
        Assert.False(backend.Requests[0].Capabilities.TimestampPassthrough);
        Assert.NotNull(complete);
        Assert.False(complete.IsReplay);
        Assert.Equal(95443.7, complete.Request.TimestampOriginSeconds);
        Assert.Contains("33bit/90k", complete.Result.StandardError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundSourceSwitchReevaluatesAtoBtoA(bool changeVpp)
    {
        var backend = new RecordingBackend(new ServerBackend());
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var request = Request("--fake-clock-jump-at", false);
        foreach (int variant in new[] { 0, 1, 0 })
        {
            controller.Submit(request with
            {
                InputFile = changeVpp ? "same.ts" : variant == 0 ? "a.ts" : "b.ts",
                VppArgs = changeVpp && variant == 1 ? new[] { "--fake-origin-seconds", "1000" } : Array.Empty<string>(),
            });
            await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        }
        Assert.Equal(6, backend.Requests.Count);
        Assert.Equal(new[] { true, false, true, false, true, false }, backend.Requests.Select(value => value.Capabilities.TimestampPassthrough));
    }

    [Fact]
    public async Task BackgroundBeforeDoesNotResetForegroundBadState()
    {
        var backend = new RecordingBackend(new ServerBackend());
        var background = new RecordingBackend(new ServerBackend());
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero, backgroundBackend: background);
        var request = Request("--fake-clock-jump-at", false);
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        controller.Submit(request with { VppArgs = new[] { "--fake-origin-seconds", "1000" } }, background: true);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.True(controller.IsLegacySource(request));
        controller.Submit(request with { TargetSeconds = 11 }, navigation: true);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(3, backend.Requests.Count);
        Assert.False(backend.Requests[^1].Capabilities.TimestampPassthrough);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedLegacySelectionUsesLogicalTargetAndSavedPhysicalClock(bool explicitTimestamp)
    {
        var backend = new RecordingBackend(new ServerBackend());
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var images = new List<PreviewFrameResult>();
        var completions = new List<PreviewCompletion>();
        controller.FrameReady += images.Add;
        controller.Completed += completions.Add;
        var logical = Request("--fake-clock-jump-at", false);
        if (explicitTimestamp) logical = logical with
        {
            InputArgs = logical.InputArgs.Concat(new[] { "--timestamp-passthrough" }).ToArray(),
            VppArgs = new[] { "--timestamp-passthrough" },
        };
        controller.Submit(logical);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        var physical = Assert.Single(completions).Segment!.PhysicalRequest;
        var plan = CommandBuilder.Plan(physical);
        double target = 10 + 1.0 / 30;
        double pre = target - (plan.SeekSeconds ?? 0);
        var retargeted = physical with
        {
            Capabilities = logical.Capabilities, InputArgs = logical.InputArgs, VppArgs = logical.VppArgs,
            TargetSeconds = target, PreMarginSeconds = pre,
            PostMarginSeconds = (plan.FrameCount - 1) / logical.Fps - pre,
        };
        controller.Submit(retargeted, navigation: true);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(2, backend.Requests.Count);
        var cached = completions[^1];
        Assert.True(cached.FromCache);
        Assert.Equal(7, cached.Segment!.Clock.PhysicalSeekSeconds);
        Assert.Equal(3 + 1.0 / 30, images[^1].Frame.Xts!.Value, 6);
        Assert.Equal(target, cached.Segment.Clock.ToInputSeconds(images[^1].Frame, 0, 30), 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedLegacyRemovesExplicitPassthroughTokensOnlyFromPhysicalArgs(bool nearWrap)
    {
        var backend = new RecordingBackend(new ServerBackend());
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewCompletion? completed = null;
        controller.Completed += value => completed = value;
        var request = Request("--fake-clock-jump-at", false) with
        {
            TimestampOriginSeconds = nearWrap ? 95443.7 : 1000,
            InputArgs = new[] { "--fake-clock-jump-at", "4", "--timestamp-passthrough" },
            VppArgs = new[] { "--timestamp-passthrough" },
        };
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        var physical = backend.Requests[^1];
        Assert.DoesNotContain("--timestamp-passthrough", CommandBuilder.BuildOneShot(physical));
        Assert.Contains("--timestamp-passthrough", request.InputArgs);
        Assert.Contains("--timestamp-passthrough", request.VppArgs);
        Assert.NotNull(completed);
        Assert.False(completed.Segment!.PhysicalRequest.Capabilities.TimestampPassthrough);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackendThrowingCancellationStillReplaysGuardFailure(bool continuous)
    {
        var backend = new ThrowingCancellationBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewCompletion? complete = null;
        PreviewFailure? failure = null;
        var order = new List<string>();
        controller.ReplayStarted += _ => order.Add("started");
        controller.FrameReady += value => { if (value.IsReplay) order.Add("frame"); };
        controller.Completed += value => { complete = value; order.Add("completed"); };
        controller.Failed += value => failure = value;
        controller.Submit(Request("--fake-clock-jump-at", continuous));
        await controller.WaitForIdleAsync();
        Assert.Null(failure);
        Assert.NotNull(complete);
        Assert.True(complete.IsReplay);
        Assert.Equal(2, backend.Calls);
        Assert.Equal(new[] { "started", "frame", "completed" }, order);
    }

    private sealed class ThrowingCancellationBackend : IPreviewBackend
    {
        public int Calls { get; private set; }
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            ++Calls;
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 C420");
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            int count = request.Continuous ? 200 : CommandBuilder.Plan(request).FrameCount;
            for (int i = 0; i < count; ++i)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double time = i / 30d;
                if (request.Capabilities.TimestampPassthrough) time = 1000 + seek + (i == 3 ? 0 : time);
                sink.OnFrame(new(header, time, 1.0 / 30, new byte[6]));
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingBackend(IPreviewBackend backend) : IPreviewBackend
    {
        public List<PreviewRequest> Requests { get; } = new();
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        { Requests.Add(request); return backend.RunAsync(request, sink, cancellationToken); }
        public ValueTask AbortAsync() => backend.AbortAsync();
        public ValueTask DisposeAsync() => backend.DisposeAsync();
    }
}

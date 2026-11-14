using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class SafeRangeQuantizationTests
{
    [Fact]
    public async Task RoundedDecimateTailKeepsExactTargetSelectable()
    {
        var backend = new QuantizedBackend();
        await using var controller = new PreviewController(backend, backend, cache: new FrameCache(0), debounce: TimeSpan.Zero);
        PreviewFrameResult? selected = null;
        PreviewCompletion? completed = null;
        PreviewFailure? failed = null;
        controller.FrameReady += value => selected = value;
        controller.Completed += value => completed = value;
        controller.Failed += value => failed = value;
        controller.Submit(new PreviewRequest
        {
            EncoderPath = "encoder", InputFile = "decimate.ts", Fps = 30, TargetSeconds = 5,
            InputDurationSeconds = 20, Capabilities = new(true), TimestampOriginSeconds = 0,
            TimestampOriginTrusted = true, VppArgs = new[] { "--vpp-decimate" },
        });
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.NotNull(selected);
        Assert.Null(failed);
        Assert.NotNull(completed);
        Assert.Same(backend.Frames[72], selected.Frame);
        Assert.Equal(5, selected.Frame.Xts);
        Assert.True(selected.Segment!.Clock.TrustedAbsolute);
        Assert.Equal(2, CommandBuilder.Plan(selected.Segment.PhysicalRequest).SeekSeconds);
        Assert.Equal(4, selected.Segment.SafeStartSeconds);
        // 生成値は補正せず、受信した6桁Xtsと24fpsから得る境界を保持する。
        Assert.Equal(5.458333 + 1.0 / 24 - 0.5, completed.Segment!.SafeEndSeconds);
    }

    [Theory]
    [InlineData(true, 0.0000015, true)]
    [InlineData(true, 0.000003, false)]
    [InlineData(false, 0.0000015, true)]
    [InlineData(false, 0.000003, false)]
    public void SafeConsumersAgreeWithoutChangingRawBounds(bool upper, double distance, bool accepted)
    {
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F24:1 C420");
        var wanted = new Y4mFrame(header, 5, null, new byte[6]);
        var frames = upper ? new[] { new Y4mFrame(header, 4, null, new byte[6]), wanted }
            : new[] { wanted, new Y4mFrame(header, 5.5, null, new byte[6]) };
        double start = upper ? 4 : 5 + distance;
        double end = upper ? 5 - distance : 6;
        var cache = new FrameCache();
        var request = Request();
        var segment = cache.StoreSegment(request, frames, new(0, 2, true), start, end)!;
        int index = upper ? 1 : 0;
        Assert.Equal(accepted, new PreviewFrameReference(segment, index).IsSafe);
        Assert.Equal(accepted, FrameSelector.Select(segment, 5, 30) == index);
        Assert.Equal(accepted, cache.TryGetAt(request, 5, out _));
        Assert.Equal(accepted, cache.GetConnectedSafeRange(request).HasValue);
        Assert.Equal(start, segment.SafeStartSeconds);
        Assert.Equal(end, segment.SafeEndSeconds);
        Assert.Same(wanted, segment.Frames[index]);
    }

    [Fact]
    public void HighPrecisionTimesAndMetadataAreNotRounded()
    {
        const double timestamp = 5.000000123456789;
        const double start = 5.000000654321098;
        const double end = 5.600000987654321;
        var frame = new Y4mFrame(Y4mHeader.Parse("YUV4MPEG2 W2 H2 F24:1 C420"), timestamp, 0.041666123456, new byte[6]);
        var cache = new FrameCache();
        var segment = cache.StoreSegment(Request(), new[] { frame }, new(0, 2, true), start, end)!;
        Assert.True(new PreviewFrameReference(segment, 0).IsSafe);
        Assert.True(cache.TryGetAt(Request(), 5, out _));
        Assert.Equal(timestamp, frame.Xts);
        Assert.Equal(0.041666123456, frame.Xdur);
        Assert.Equal(start, segment.SafeStartSeconds);
        Assert.Equal(end, segment.SafeEndSeconds);
    }

    [Fact]
    public void HalfDurationSelectionDoesNotUseSafeBoundaryTolerance()
    {
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F25:1 C420");
        var current = new Y4mFrame(header, 5, 0.04, new byte[6]);
        var future = new Y4mFrame(header, 5.0200015, 0.04, new byte[6]);
        var segment = new PreviewSegmentSnapshot(1, Request(), new(0, 2, true), new[] { current, future }, 4, 6, false, false);
        Assert.Equal(0, FrameSelector.Select(segment, 5, 30));
        Assert.True(new PreviewFrameReference(segment, 1).IsSafe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuantizedOverlapStillRequiresMatchingPixels(bool mismatch)
    {
        var cache = new FrameCache();
        var request = Request();
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F2:1 C420");
        Y4mFrame[] Frames(bool change) => Enumerable.Range(0, 3).Select(index =>
            new Y4mFrame(header, 5 + index * 0.5, 0.5,
                Enumerable.Repeat((byte)(change && index == 2 ? 1 : 0), 6).ToArray())).ToArray();
        Assert.NotNull(cache.StoreSegment(request, Frames(false), new(0, 2, true), 5, 6));
        Assert.NotNull(cache.StoreSegment(request, Frames(mismatch), new(0, 2, true), 5.0000015, 5.9999985));
        Assert.Equal(!mismatch, cache.CanStitch(request));
        Assert.Equal(mismatch ? 1 : 2, cache.Count);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(4.999999)]
    public void StructurallyInvalidConnectedRangeIsStillRejected(double end)
    {
        var frame = new Y4mFrame(Y4mHeader.Parse("YUV4MPEG2 W2 H2 F24:1 C420"), 5, null, new byte[6]);
        var cache = new FrameCache();
        Assert.NotNull(cache.StoreSegment(Request(), new[] { frame }, new(0, 2, true), 5, end));
        Assert.Null(cache.GetConnectedSafeRange(Request()));
    }

    [Theory]
    [InlineData(24, 1, 0, false)]
    [InlineData(24, 1, 90000, true)]
    [InlineData(30, 1, 0, false)]
    [InlineData(30, 1, 90000, true)]
    [InlineData(30000, 1001, 0, false)]
    [InlineData(30000, 1001, 90000, true)]
    public async Task FrameRatesAndNonzeroOriginsKeepTheirRawClock(int numerator, int denominator, double origin, bool durations)
    {
        var header = Y4mHeader.Parse($"YUV4MPEG2 W2 H2 F{numerator}:{denominator} C420");
        double fps = (double)numerator / denominator;
        var frames = Enumerable.Range(0, (int)Math.Ceiling(4 * fps) + 1).Select(index =>
            new Y4mFrame(header, Math.Round(origin + 2 + index / fps, 6, MidpointRounding.AwayFromZero),
                durations ? Math.Round(1 / fps, 6, MidpointRounding.AwayFromZero) : null, new byte[6])).ToArray();
        var backend = new QuantizedBackend(frames);
        await using var controller = new PreviewController(backend, backend, cache: new FrameCache(0), debounce: TimeSpan.Zero);
        PreviewFrameResult? selected = null;
        PreviewFailure? failed = null;
        controller.FrameReady += value => selected = value;
        controller.Failed += value => failed = value;
        controller.Submit(Request() with { TimestampOriginSeconds = origin });
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.NotNull(selected);
        Assert.Null(failed);
        Assert.True(selected.Segment!.Clock.TrustedAbsolute);
        Assert.InRange(Math.Abs(selected.Frame.Xts!.Value - origin - 5), 0, FrameSelector.Duration(selected.Frame, 30) / 2 + 1e-6);
        Assert.Contains(selected.Frame, frames);
        Assert.Equal(origin, selected.Segment.Clock.InputOriginSeconds);
    }

    [Fact]
    public async Task TailShortfallBeyondToleranceRequiresNaturalEndConfirmation()
    {
        var frames = new QuantizedBackend().Frames.ToArray();
        var last = frames[^1];
        frames[^1] = new(last.Header, 5.45833, null, last.Data);
        var backend = new QuantizedBackend(frames);
        await using var controller = new PreviewController(backend, backend, cache: new FrameCache(0), debounce: TimeSpan.Zero);
        PreviewFrameResult? selected = null;
        PreviewFailure? failed = null;
        controller.FrameReady += value => selected = value;
        controller.Failed += value => failed = value;
        controller.Submit(Request());
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.NotNull(selected);
        Assert.Null(failed);
        Assert.True(selected.Segment!.ReachedInputEnd);
        // 許容幅は広げない。有限窓のままなら従来どおり不足と判定する。
        var finite = selected.Segment with { ReachedInputEnd = false,
            SafeEndSeconds = selected.Segment.SafeEndSeconds - PreviewByteWindow.TailGuardSeconds };
        Assert.True(PreviewWindowShortfall.FromSegment(Request(), finite)!.MissingTail);
    }

    [Fact]
    public async Task IndependentlyRoundedTimestampAndDurationShareTheSameSafeBoundary()
    {
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F24:1 C420");
        double[] timestamps = { 2, 2.13, 3.4, 4.2, 5.000001, 5.458333 };
        double[] durations = { 0.130001, 1.269999, 0.800001, 0.799999, 0.458332, 0.041667 };
        var frames = timestamps.Select((timestamp, index) => new Y4mFrame(header, timestamp,
            durations[index], Enumerable.Repeat((byte)index, 6).ToArray())).ToArray();
        var backend = new QuantizedBackend(frames);
        await using var controller = new PreviewController(backend, backend, cache: new FrameCache(0), debounce: TimeSpan.Zero);
        PreviewFrameResult? selected = null;
        controller.FrameReady += value => selected = value;
        controller.Submit(Request());
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.NotNull(selected);
        Assert.Same(frames[4], selected.Frame);
        Assert.Equal(5.000001, selected.Frame.Xts);
        Assert.Equal(0.458332, selected.Frame.Xdur);
        Assert.Equal(0.458332, FrameSelector.Duration(selected.Frame, 30));
        Assert.Equal(0.041667, frames[^1].Xdur);
        Assert.Equal(5, selected.Segment!.SafeEndSeconds);
    }

    private static PreviewRequest Request() => new()
    {
        EncoderPath = "encoder", InputFile = "decimate.ts", Fps = 30, TargetSeconds = 5,
        InputDurationSeconds = 20, Capabilities = new(true), TimestampOriginSeconds = 0,
        TimestampOriginTrusted = true, VppArgs = new[] { "--vpp-decimate" },
    };

    private sealed class QuantizedBackend(IReadOnlyList<Y4mFrame>? frames = null) : IPreviewBackend
    {
        public IReadOnlyList<Y4mFrame> Frames { get; } = frames ?? Enumerable.Range(0, 84).Select(index =>
            new Y4mFrame(Y4mHeader.Parse("YUV4MPEG2 W2 H2 F24:1 C420"),
                Math.Round(2 + index / 24.0, 6, MidpointRounding.AwayFromZero), null,
                Enumerable.Repeat((byte)index, 6).ToArray())).ToArray();
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<bool?>(true);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            sink.OnHeader(Frames[0].Header);
            foreach (var frame in Frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sink.OnFrame(frame);
            }
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

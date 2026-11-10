using HWEncVppPreview.Core;
namespace HWEncVppPreview.Tests;

public sealed class PreviewWarmupPolicyTests
{
    private static PreviewRequest Request(double target = 10) => new()
    {
        EncoderPath = "encoder", InputFile = "input", Fps = 2, TargetSeconds = target, Continuous = true,
        Capabilities = new(true), TimestampOriginSeconds = 1000, DependsOnHistory = false,
    };

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    [InlineData(null, 2)]
    public void OnlyActualProbeResultControlsWarmup(bool? history, double expected)
    {
        // 未知CLIも既定値もViewerで再解釈せず、Nativeの引数解析結果だけを採用する。
        foreach (string[] filters in new[] { Array.Empty<string>(), new[] { "--vpp-unknown" }, new[] { "--vpp-nlmeans", "d=1" } })
            Assert.Equal(expected, PreviewWarmupPolicy.RequiredWarmupSeconds(Request() with
            { DependsOnHistory = history, VppArgs = filters, InputArgs = new[] { "--unknown-input" } }));
    }

    [Fact]
    public void WarmupTwoExactlyPreservesOldPlanForArbitraryMargins()
    {
        foreach (double target in new[] { 0, 0.3, 2, 3, 10.1234, 3600 })
        foreach (double pre in new[] { 0, 2, 3.1234, 5 })
        {
            var logical = Request(target) with { PreMarginSeconds = pre, VppArgs = new[] { "--vpp-kfm" }, DependsOnHistory = true };
            var physical = PreviewWarmupPolicy.PreparePhysical(logical, false);
            var old = logical with { SeekSecondsOverride = Math.Max(0, target - Math.Max(3, pre)) };
            Assert.Equal(CommandBuilder.Plan(old), CommandBuilder.Plan(physical));
            Assert.Equal(pre, physical.PreMarginSeconds);
            Assert.Equal(2, physical.WarmupSeconds);
            double seek = CommandBuilder.Plan(old).SeekSeconds ?? 0;
            Assert.Equal(seek > 0 ? seek + 2 : 0, PreviewWarmupPolicy.SafeStartSeconds(physical, false));
        }
    }

    [Fact]
    public void WarmupZeroUsesOneSecondForContinuousFiniteAndBefore()
    {
        foreach (double target in new[] { 0, 0.3, 1, 1.2345, 10 })
        foreach (bool continuous in new[] { false, true })
        foreach (bool background in new[] { false, true })
        {
            var physical = PreviewWarmupPolicy.PreparePhysical(Request(target) with { Continuous = continuous }, background);
            double seek = Math.Round(Math.Max(0, target - 1) * 1000, MidpointRounding.AwayFromZero) / 1000;
            Assert.Equal(seek, CommandBuilder.Plan(physical).SeekSeconds ?? 0);
            Assert.Equal(1, physical.PreMarginSeconds);
            Assert.Equal(0, physical.WarmupSeconds);
            Assert.Equal(seek, PreviewWarmupPolicy.SafeStartSeconds(physical, background));
        }
    }

    [Fact]
    public void ReverseExplicitSeekLegacyAndUnknownBackgroundStayCompatible()
    {
        foreach (var logical in new[] { Request() with { Continuous = false, PreMarginSeconds = 5 }, Request() with { Continuous = false, SeekSecondsOverride = 5 } })
        {
            var physical = PreviewWarmupPolicy.PreparePhysical(logical, false);
            Assert.Equal(2, physical.WarmupSeconds);
            Assert.Equal(CommandBuilder.Plan(logical with { SeekSecondsOverride = Math.Max(0, logical.TargetSeconds - Math.Max(3, logical.PreMarginSeconds)) }), CommandBuilder.Plan(physical));
        }
        var reverse = PreviewWarmupPolicy.PreparePhysical(Request() with { Continuous = false, PreMarginSeconds = 5 }, false);
        Assert.Equal(5, CommandBuilder.Plan(reverse).SeekSeconds);
        Assert.Equal(7, PreviewWarmupPolicy.SafeStartSeconds(reverse, false));
        foreach (var req in new[] { Request() with { Capabilities = new() }, Request() with { TimestampOriginSeconds = null } })
            Assert.Equal(CommandBuilder.Plan(req), CommandBuilder.Plan(PreviewWarmupPolicy.PreparePhysical(req, false)));
        var unknown = Request() with { VppArgs = new[] { "unknown" }, DependsOnHistory = true, Continuous = false };
        var background = PreviewWarmupPolicy.PreparePhysical(unknown, true);
        Assert.Equal(CommandBuilder.Plan(unknown), CommandBuilder.Plan(background));
        Assert.Equal(8, PreviewWarmupPolicy.SafeStartSeconds(background, true));
        Assert.Equal(2, background.WarmupSeconds);
    }

    [Fact]
    public void WarmupMetadataDoesNotChangeCacheIdentity()
    {
        var request = Request();
        var other = request with { WarmupSeconds = 0 };
        Assert.True(FrameCache.SameContent(request, other));
        var cache = new FrameCache();
        cache.Store(request, new[] { Frame(10) });
        Assert.True(cache.TryGet(other, out _));
    }

    private static Y4mFrame Frame(double time, byte value = 0) => new(new Y4mHeader { Width = 2, Height = 2 }, 1000 + time, 0.5, Enumerable.Repeat(value, 6).ToArray());

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ZeroWarmupExposesSeekPixelsAndKeepsTargetXts(bool continuous, bool background)
    {
        var backend = new PixelBackend();
        var cache = new FrameCache();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero, backgroundBackend: backend);
        PreviewFrameResult? image = null;
        PreviewCompletion? done = null;
        controller.FrameReady += value => image = value;
        controller.Completed += value => done = value;
        controller.Submit(Request() with { Continuous = continuous }, background: background);
        if (continuous) await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        var physical = Assert.Single(backend.Requests);
        Assert.Equal(9, CommandBuilder.Plan(physical).SeekSeconds);
        Assert.Equal(0, physical.WarmupSeconds);
        Assert.NotNull(image);
        Assert.Equal(1010, image.Frame.Xts);
        Assert.Equal(PixelFrame(10).Data, image.Frame.Data);
        var segment = continuous ? image.Segment! : done!.Segment!;
        Assert.Equal(9, segment.SafeStartSeconds);
        Assert.Equal(1009, segment.Frames[0].Xts);
        Assert.Equal(PixelFrame(9).Data, segment.Frames[0].Data);
        Assert.True(cache.TryGetAt(Request(), 9, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroWarmupSeekAdjacentOverlapChecksPixelsAndAbsoluteClock(bool mismatch)
    {
        var backend = new PixelBackend { MismatchSecond = mismatch };
        var cache = new FrameCache();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        var images = new List<PreviewFrameResult>();
        controller.FrameReady += images.Add;
        controller.Submit(Request());
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        long old = images[^1].Segment!.SegmentId;
        controller.Submit(Request(10.5), bypassCache: true);
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        var current = cache.GetSegment(images[^1].Segment!.SegmentId)!;
        Assert.Equal(9.5, current.SafeStartSeconds);
        Assert.Equal(1010.5, images[^1].Frame.Xts);
        Assert.True(current.Clock.TrustedAbsolute);
        Assert.Equal(!mismatch, current.CanStitch);
        Assert.Equal(!mismatch, cache.CanStitch(Request()));
        if (mismatch) Assert.Null(cache.GetSegment(old));
        else
        {
            Assert.NotNull(cache.GetSegment(old));
            Assert.Equal(PixelFrame(10.5).Data, images[^1].Frame.Data);
        }
    }

    private static Y4mFrame PixelFrame(double time, bool mismatch = false) => Frame(time, (byte)((int)Math.Round(time * 2) % 251 + (mismatch ? 1 : 0)));

    private sealed class PixelBackend : IPreviewBackend
    {
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<bool?>(false);

        public System.Threading.Channels.Channel<double> Pauses { get; } = System.Threading.Channels.Channel.CreateUnbounded<double>();
        public List<PreviewRequest> Requests { get; } = new();
        public bool MismatchSecond { get; init; }
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            bool mismatch = MismatchSecond && Requests.Count > 1;
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            double last = double.NaN;
            try
            {
                for (int index = 0; request.Continuous || index < CommandBuilder.Plan(request).FrameCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var permission = sink.BeforeReadAsync(cancellationToken);
                    if (!permission.IsCompleted)
                    {
                        sink.OnReadStateChanged(false);
                        Pauses.Writer.TryWrite(last);
                    }
                    await permission;
                    sink.OnReadStateChanged(true);
                    sink.OnOutput();
                    last = seek + index * 0.5;
                    sink.OnFrame(PixelFrame(last, mismatch));
                }
                return new(0, "");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(-12, "", true); }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

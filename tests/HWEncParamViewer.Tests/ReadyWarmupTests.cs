using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class ReadyWarmupTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static PreviewRequest Request() => new()
    {
        EncoderPath = BackendTests.FakeEncoderPath(), InputFile = "input.ts", Fps = 30,
        TargetSeconds = 10, Capabilities = new(true), TimestampOriginSeconds = 1000,
    };

    [Theory]
    [InlineData("QSVEnc", 0)]
    [InlineData("VCEEnc", 0)]
    [InlineData("rkmppenc", 0)]
    [InlineData("NVEnc", 2)]
    [InlineData("fake", 2)]
    [InlineData("", 2)]
    [InlineData("qsvenc", 2)]
    public async Task ActualReadyControlsFiniteStreamAndBefore(string encoder, double warmup)
    {
        // パス名とhelpではなく、実際の常駐プロセスのREADYだけを物理計画へ渡す。
        foreach (var mode in new (bool Continuous, bool Background)[] { (false, false), (true, false), (false, true) })
        {
            await using var backend = new ServerBackend(configureStartInfo: start => start.Environment["FAKE_ENCODER_READY_ENCODER"] = encoder);
            await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero, backgroundBackend: backend);
            PreviewSegmentSnapshot? segment = null;
            controller.SegmentUpdated += value => segment = value.Segment;
            controller.Completed += value => segment = value.Segment ?? segment;
            Exception? failed = null;
            controller.Failed += value => failed = value.Exception ?? new InvalidOperationException(value.Reason);
            controller.Submit(Request() with { Continuous = mode.Continuous }, background: mode.Background);
            await controller.WaitForIdleAsync().WaitAsync(Timeout);
            Assert.Null(failed);
            Assert.NotNull(segment);
            Assert.Equal(warmup, segment.PhysicalRequest.WarmupSeconds);
            double seek = warmup == 0 ? 9 : mode.Background ? 8 : 7;
            Assert.Equal(seek, CommandBuilder.Plan(segment.PhysicalRequest).SeekSeconds);
            Assert.Equal(mode.Background ? seek : 9, segment.SafeStartSeconds, 5);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("NVEnc")]
    [InlineData("unknown")]
    public void UnknownMetadataCannotBeReplacedByPathOrHelp(string? encoder)
    {
        var request = Request() with { EncoderPath = "QSVEncC64.exe", ReadyEncoder = encoder,
            HelpSchema = new HelpSchema("QSVEnc --vpp-tweak"), VppArgs = new[] { "--vpp-tweak", "brightness=0.1" } };
        Assert.Equal(2, PreviewWarmupPolicy.RequiredWarmupSeconds(request));
    }

    [Fact]
    public async Task OneShotHasNoReadyAndKeepsTwoSeconds()
    {
        await using var backend = new OneShotBackend();
        Assert.Null(await ((IPreviewBackend)backend).GetReadyEncoderAsync(Request().EncoderPath));
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewCompletion? completion = null;
        controller.Completed += value => completion = value;
        controller.Submit(Request());
        await controller.WaitForIdleAsync().WaitAsync(Timeout);
        Assert.NotNull(completion?.Segment);
        Assert.Equal(2, completion.Segment.PhysicalRequest.WarmupSeconds);
        Assert.Equal(7, CommandBuilder.Plan(completion.Segment.PhysicalRequest).SeekSeconds);
    }

    [Fact]
    public async Task RestartAndPathChangeReadNewIdentity()
    {
        string encoder = "QSVEnc";
        await using var backend = new ServerBackend(configureStartInfo: start => start.Environment["FAKE_ENCODER_READY_ENCODER"] = encoder);
        string path = Request().EncoderPath;
        Assert.Equal("QSVEnc", await backend.GetReadyEncoderAsync(path).WaitAsync(Timeout));
        encoder = "NVEnc";
        await backend.AbortAsync();
        Assert.Equal("NVEnc", await backend.GetReadyEncoderAsync(path).WaitAsync(Timeout));
        encoder = "rkmppenc";
        string alternate = Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path));
        Assert.Equal("rkmppenc", await backend.GetReadyEncoderAsync(alternate).WaitAsync(Timeout));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedReadyAndStartFailureKeepAutoFallback(bool startFailure)
    {
        await using var backend = new ServerBackend(readyTimeout: TimeSpan.FromMilliseconds(100), configureStartInfo: start =>
        {
            if (startFailure) start.FileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            else start.Environment["FAKE_ENCODER_READY_NONE"] = "1";
        });
        await using var controller = new PreviewController(backend, new OneShotBackend(), debounce: TimeSpan.Zero);
        PreviewCompletion? completion = null;
        PreviewFailure? failure = null;
        controller.Completed += value => completion = value;
        controller.Failed += value => failure = value;
        controller.Submit(Request());
        await controller.WaitForIdleAsync().WaitAsync(Timeout);
        Assert.Null(failure);
        Assert.NotNull(completion?.Segment);
        Assert.Equal(2, completion.Segment.PhysicalRequest.WarmupSeconds);
        Assert.Equal(7, CommandBuilder.Plan(completion.Segment.PhysicalRequest).SeekSeconds);
    }

    [Fact]
    public async Task RestartBetweenPlanAndRunRejectsUnapprovedZeroWarmup()
    {
        string encoder = "QSVEnc";
        await using var backend = new ServerBackend(configureStartInfo: start => start.Environment["FAKE_ENCODER_READY_ENCODER"] = encoder);
        Assert.Equal("QSVEnc", await backend.GetReadyEncoderAsync(Request().EncoderPath));
        encoder = "NVEnc";
        await backend.AbortAsync();
        await Assert.ThrowsAsync<PreviewServerNotSupportedException>(() => backend.RunAsync(
            Request() with { WarmupSeconds = 0 }, new EmptySink()));
    }

    private sealed class EmptySink : IPreviewSink
    {
        public void OnOutput() { }
        public void OnHeader(Y4mHeader header) { }
        public void OnFrame(Y4mFrame frame) { }
    }

    [Fact]
    public async Task ReadyCancellationIsNotConvertedToUnknownIdentity()
    {
        await using var backend = new ServerBackend(configureStartInfo: start => start.Environment["FAKE_ENCODER_READY_NONE"] = "1");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.GetReadyEncoderAsync(Request().EncoderPath, cancellation.Token));
    }
}

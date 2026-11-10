using HWEncVppPreview.Testing;
using System.Diagnostics;
using System.Text.Json;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class HistoryProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static PreviewRequest Request() => new()
    {
        EncoderPath = BackendTests.FakeEncoderPath(), InputFile = "input.ts", Fps = 30,
        TargetSeconds = 10, Capabilities = new(true), TimestampOriginSeconds = 1000,
    };

    private static ServerBackend Backend(Action<ProcessStartInfo>? configure = null, TimeSpan? probeTimeout = null)
        => new(probeTimeout: probeTimeout, configureStartInfo: start =>
        {
            start.Environment["FAKE_ENCODER_PROBE_SUPPORTED"] = "1";
            configure?.Invoke(start);
        });

    private static async Task<PreviewSegmentSnapshot> RunAsync(IPreviewBackend backend, bool continuous = false,
        bool background = false, bool enableFallback = true, PreviewRequest? request = null)
    {
        await using var controller = new PreviewController(backend, new OneShotBackend(), debounce: TimeSpan.Zero,
            backgroundBackend: backend, enableFallback: enableFallback);
        PreviewSegmentSnapshot? segment = null;
        controller.SegmentUpdated += value => segment = value.Segment;
        controller.Completed += value => segment = value.Segment ?? segment;
        PreviewFailure? failed = null;
        controller.Failed += value => failed = value;
        controller.Submit((request ?? Request()) with { Continuous = continuous }, background: background);
        await controller.WaitForIdleAsync().WaitAsync(Timeout);
        Assert.Null(failed);
        return Assert.IsType<PreviewSegmentSnapshot>(segment);
    }

    [Theory]
    [InlineData("QSVEnc")]
    [InlineData("VCEEnc")]
    [InlineData("rkmppenc")]
    [InlineData("NVEnc")]
    [InlineData("fake")]
    [InlineData("")]
    public async Task ActualProbeReplacesEncoderNameForFiniteStreamAndBefore(string encoder)
    {
        // CUDAを含め、実装名による推測ではなく当該引数で実受信したhistoryだけを使う。
        foreach (var mode in new (bool Continuous, bool Background)[] { (false, false), (true, false), (false, true) })
        {
            var backend = Backend(start => start.Environment["FAKE_ENCODER_READY_ENCODER"] = encoder);
            var segment = await RunAsync(backend, mode.Continuous, mode.Background);
            Assert.Equal(0, segment.PhysicalRequest.WarmupSeconds);
            Assert.Equal(9, CommandBuilder.Plan(segment.PhysicalRequest).SeekSeconds);
            Assert.Equal(9, segment.SafeStartSeconds, 5);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task UnsupportedHistoryAndFailedParseKeepTwoSeconds(bool unsupported, bool history, bool failedParse)
    {
        var backend = Backend(start =>
        {
            start.Environment["FAKE_ENCODER_READY_ENCODER"] = "QSVEnc";
            start.Environment["FAKE_ENCODER_PROBE_SUPPORTED"] = unsupported ? "0" : "1";
            start.Environment["FAKE_ENCODER_PROBE_HISTORY"] = history ? "1" : "0";
            start.Environment["FAKE_ENCODER_PROBE_RC"] = failedParse ? "-5" : "0";
        });
        var segment = await RunAsync(backend);
        Assert.Equal(unsupported || history || failedParse ? 2 : 0, segment.PhysicalRequest.WarmupSeconds);
        Assert.Equal(unsupported || history || failedParse ? 7 : 9, CommandBuilder.Plan(segment.PhysicalRequest).SeekSeconds);
    }

    [Fact]
    public async Task InputSideTemporalFilterIsIncludedInProbe()
    {
        var segment = await RunAsync(Backend(), background: true, request: Request() with
        { InputArgs = new[] { "--avhw", "--vpp-kfm" }, VppArgs = Array.Empty<string>() });
        Assert.Equal(2, segment.PhysicalRequest.WarmupSeconds);
        Assert.Equal(8, CommandBuilder.Plan(segment.PhysicalRequest).SeekSeconds);
    }

    [Fact]
    public async Task OneShotHasNoProbeAndKeepsTwoSeconds()
    {
        var backend = new OneShotBackend();
        Assert.Null(await ((IPreviewBackend)backend).GetHistoryRequirementAsync(Request()));
        var segment = await RunAsync(backend);
        Assert.Equal(2, segment.PhysicalRequest.WarmupSeconds);
        Assert.Equal(7, CommandBuilder.Plan(segment.PhysicalRequest).SeekSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheNormalizesWindowButRetainsOrderedArguments(bool history)
    {
        string log = Path.GetTempFileName();
        try
        {
            await using var backend = Backend(start =>
            {
                start.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = log;
                start.Environment["FAKE_ENCODER_PROBE_HISTORY"] = history ? "1" : "0";
            });
            var request = Request();
            Assert.Equal(history, await backend.GetHistoryRequirementAsync(request));
            Assert.Equal(history, await backend.GetHistoryRequirementAsync(request with
            { TargetSeconds = 20, PreMarginSeconds = 5, PostMarginSeconds = 3, SeekSecondsOverride = 4, Continuous = true }));
            foreach (var changed in new[]
            {
                request with { VppArgs = new[] { "--vpp-tweak", "brightness=0.1" } },
                request with { VppArgs = new[] { "--vpp-tweak", "brightness=0.2" } },
                request with { InputArgs = new[] { "--avsw" } },
                request with { VppArgs = new[] { "--vpp-tweak", "brightness=0.1", "--vpp-unsharp" } },
                request with { VppArgs = new[] { "--vpp-unsharp", "--vpp-tweak", "brightness=0.1" } },
                request with { InputFile = "other.ts" },
                request with { Capabilities = request.Capabilities with { OpenClTaskThreads = true } },
            }) Assert.Equal(history, await backend.GetHistoryRequirementAsync(changed));
            Assert.Equal(8, Commands(log).Count(item => item.GetProperty("cmd").GetString() == "probe"));
            if (!history)
            {
                Assert.Equal(0, (await backend.RunAsync(request with { WarmupSeconds = 0 }, new EmptySink())).ExitCode);
                Assert.Equal(8, Commands(log).Count(item => item.GetProperty("cmd").GetString() == "probe"));
            }
        }
        finally { FakeLogFiles.Delete(log); }
    }

    [Fact]
    public async Task FailedParseIsNotCachedAndSessionCanStillRun()
    {
        string log = Path.GetTempFileName();
        try
        {
            await using var backend = Backend(start =>
            {
                start.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = log;
                start.Environment["FAKE_ENCODER_PROBE_RC"] = "-5";
            });
            Assert.Null(await backend.GetHistoryRequirementAsync(Request()));
            Assert.Null(await backend.GetHistoryRequirementAsync(Request()));
            Assert.Equal(2, Commands(log).Count(item => item.GetProperty("cmd").GetString() == "probe"));
            Assert.Equal(0, (await backend.RunAsync(Request(), new EmptySink())).ExitCode);
        }
        finally { FakeLogFiles.Delete(log); }
    }

    [Fact]
    public async Task RestartAndPathChangeCannotReusePreviousProbe()
    {
        bool history = false;
        await using var backend = Backend(start => start.Environment["FAKE_ENCODER_PROBE_HISTORY"] = history ? "1" : "0");
        var request = Request();
        Assert.False(await backend.GetHistoryRequirementAsync(request));
        history = true;
        await backend.AbortAsync();
        Assert.True(await backend.GetHistoryRequirementAsync(request));
        history = false;
        string path = Path.Combine(Path.GetDirectoryName(request.EncoderPath)!, ".", Path.GetFileName(request.EncoderPath));
        Assert.False(await backend.GetHistoryRequirementAsync(request with { EncoderPath = path }));
    }

    [Fact]
    public async Task RestartBetweenPlanAndRunRechecksHistory()
    {
        bool history = false;
        await using var backend = Backend(start => start.Environment["FAKE_ENCODER_PROBE_HISTORY"] = history ? "1" : "0");
        Assert.False(await backend.GetHistoryRequirementAsync(Request()));
        history = true;
        await backend.AbortAsync();
        await Assert.ThrowsAsync<PreviewServerNotSupportedException>(() => backend.RunAsync(
            Request() with { WarmupSeconds = 0 }, new EmptySink()));
    }

    [Fact]
    public async Task QueryWaitsForCancelledRunEnd()
    {
        string log = Path.GetTempFileName();
        try
        {
            await using var backend = Backend(start => start.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = log);
            using var cancellation = new CancellationTokenSource();
            var sink = new EmptySink();
            var run = backend.RunAsync(Request() with { Continuous = true, TargetSeconds = 0, PreMarginSeconds = 0,
                InputArgs = new[] { "--avhw", "--fake-hang-after", "1" } }, sink, cancellation.Token);
            await sink.FirstFrame.Task.WaitAsync(Timeout);
            var probe = backend.GetHistoryRequirementAsync(Request());
            Assert.False(probe.IsCompleted);
            Assert.DoesNotContain(Commands(log), item => item.GetProperty("cmd").GetString() == "probe");
            cancellation.Cancel();
            Assert.True((await run.WaitAsync(Timeout)).IsCancelled);
            Assert.False(await probe.WaitAsync(Timeout));
            string[] commands = Commands(log).Select(item => item.GetProperty("cmd").GetString()!).ToArray();
            Assert.Equal(new[] { "run", "cancel", "probe" }, commands);
        }
        finally { FakeLogFiles.Delete(log); }
    }

    [Theory]
    [InlineData("RGYPREVIEW PROBE id={id} history=2 rc=0")]
    [InlineData("RGYPREVIEW PROBE id={id} history=+0 rc=0")]
    [InlineData("RGYPREVIEW PROBE id={id} history=0")]
    [InlineData("RGYPREVIEW PROBE id=99999999 history=0 rc=0")]
    [InlineData("RGYPREVIEW PROBE id={id} history=1 history=0 rc=0")]
    [InlineData("RGYPREVIEW BEGIN id={id}")]
    public async Task MalformedProbeDropsSessionAndUsesAutoFallback(string reply)
    {
        var backend = Backend(start => start.Environment["FAKE_ENCODER_PROBE_REPLY"] = reply);
        var segment = await RunAsync(backend);
        Assert.Equal(2, segment.PhysicalRequest.WarmupSeconds);
        Assert.Equal(7, CommandBuilder.Plan(segment.PhysicalRequest).SeekSeconds);
    }

    [Fact]
    public async Task ProbeTimeoutDropsSessionAndUsesAutoFallback()
    {
        var backend = Backend(start => start.Environment["FAKE_ENCODER_PROBE_NONE"] = "1", TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, (await RunAsync(backend)).PhysicalRequest.WarmupSeconds);
    }

    [Fact]
    public async Task ProbeFailureWithoutFallbackIsReported()
    {
        await using var backend = Backend(start => start.Environment["FAKE_ENCODER_PROBE_REPLY"] = "broken");
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero, enableFallback: false);
        PreviewFailure? failure = null;
        controller.Failed += value => failure = value;
        controller.Submit(Request());
        await controller.WaitForIdleAsync().WaitAsync(Timeout);
        Assert.IsType<PreviewServerNotSupportedException>(failure?.Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedReadyAndStartFailureKeepAutoFallback(bool startFailure)
    {
        var backend = new ServerBackend(readyTimeout: TimeSpan.FromMilliseconds(100), configureStartInfo: start =>
        {
            if (startFailure) start.FileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            else start.Environment["FAKE_ENCODER_READY_NONE"] = "1";
        });
        Assert.Equal(2, (await RunAsync(backend)).PhysicalRequest.WarmupSeconds);
    }

    [Fact]
    public async Task CancellationDuringProbeDropsSessionAndDoesNotBecomeHistory()
    {
        string log = Path.GetTempFileName();
        bool delay = true;
        try
        {
            await using var backend = Backend(start =>
            {
                start.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = log;
                start.Environment["FAKE_ENCODER_PROBE_DELAY_MS"] = delay ? "5000" : "0";
            });
            using var cancellation = new CancellationTokenSource();
            var query = backend.GetHistoryRequirementAsync(Request(), cancellation.Token);
            using var deadline = new CancellationTokenSource(Timeout);
            while (!Commands(log).Any(item => item.GetProperty("cmd").GetString() == "probe"))
                await Task.Delay(10, deadline.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
            delay = false;
            Assert.False(await backend.GetHistoryRequirementAsync(Request()));
        }
        finally { FakeLogFiles.Delete(log); }
    }

    [Fact]
    public async Task CancellationDuringReadyRemainsCancellation()
    {
        await using var backend = Backend(start => start.Environment["FAKE_ENCODER_READY_NONE"] = "1");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.GetHistoryRequirementAsync(Request(), cancellation.Token));
    }

    private static JsonElement[] Commands(string log)
    {
        // PID別ログを受信順に集約し、完了したJSON行だけを読む。
        return FakeLogFiles.ReadAllLines(log).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
    }

    private sealed class EmptySink : IPreviewSink
    {
        public TaskCompletionSource FirstFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnOutput() { }
        public void OnHeader(Y4mHeader header) { }
        public void OnFrame(Y4mFrame frame) => FirstFrame.TrySetResult();
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;
using CliProgram = HWEncParamViewer.Cli.Program;

namespace HWEncParamViewer.Tests;

public sealed class CliTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("oneshot", "oneshot")]
    [InlineData("server", "server")]
    [InlineData("auto", "server")]
    public async Task ModesProduceSingleJsonAndMatchingDumps(string mode, string actual)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"viewer-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string raw = Path.Combine(directory, "selected.raw");
        string y4m = Path.Combine(directory, "interval.y4m");
        try
        {
            var run = await RunProcessAsync(Args("--mode", mode, "--dump", raw, "--dump-y4m", y4m,
                "--vpp", "--vpp-nlmeans sigma=0.1"));
            Assert.Equal(0, run.ExitCode);
            using var json = ParseSingleLine(run.Output);
            var root = json.RootElement;
            Assert.Equal(0, root.GetProperty("rc").GetInt32());
            Assert.Equal(actual, root.GetProperty("mode").GetString());
            Assert.Equal(3, root.GetProperty("selectedIndex").GetInt32());
            Assert.Equal(0.1, root.GetProperty("selectedXts").GetDouble(), 7);
            Assert.False(root.GetProperty("targetNotReached").GetBoolean());
            Assert.Equal(7, root.GetProperty("frameCount").GetInt32());
            Assert.Equal(7, root.GetProperty("frames").GetInt32());
            Assert.Equal(0.2, root.GetProperty("seekSeconds").GetDouble(), 7);
            Assert.Equal(64, root.GetProperty("width").GetInt32());
            Assert.Equal(36, root.GetProperty("height").GetInt32());
            Assert.Equal("420", root.GetProperty("chroma").GetString());
            Assert.Equal(8, root.GetProperty("bitDepth").GetInt32());
            Assert.True(root.GetProperty("elapsedMs").GetDouble() >= 0);
            Assert.False(root.GetProperty("timedOut").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
            byte[] selected = await File.ReadAllBytesAsync(raw);
            Assert.Equal(64 * 36 * 3 / 2, selected.Length);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(selected)), root.GetProperty("sha256").GetString());
            using var stream = File.OpenRead(y4m);
            var reader = new Y4mReader(stream);
            var frames = new List<Y4mFrame>();
            while (reader.ReadFrame() is { } frame) frames.Add(frame);
            Assert.Equal(7, frames.Count);
            Assert.Equal(selected, frames[3].Data);
            Assert.All(frames, frame => { Assert.NotNull(frame.Xts); Assert.NotNull(frame.Xdur); });
            Assert.Equal("0.2", reader.Header.ExtTags["XSEEK"]);
        }
        finally
        {
            File.Delete(raw);
            File.Delete(y4m);
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("0:60:00")]
    [InlineData("0:00:60")]
    [InlineData("invalid")]
    public async Task InvalidTimeReturnsArgumentExitAndJson(string time)
    {
        var args = Args();
        args[Array.IndexOf(args, "--time") + 1] = time;
        var run = await RunProcessAsync(args);
        Assert.Equal(2, run.ExitCode);
        using var json = ParseSingleLine(run.Output);
        Assert.Equal(2, json.RootElement.GetProperty("rc").GetInt32());
        Assert.NotNull(json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnknownOptionAndMissingRequiredArgumentsReturnArgumentExit()
    {
        foreach (var args in new[] { Array.Empty<string>(), Args("--invalid", "value"), Args("--mode", "invalid") })
        {
            var run = await RunProcessAsync(args);
            Assert.Equal(2, run.ExitCode);
            using var json = ParseSingleLine(run.Output);
            Assert.Equal(2, json.RootElement.GetProperty("rc").GetInt32());
        }
    }

    [Fact]
    public async Task ProbeEncoderErrorReturnsExitOneWithDiagnostics()
    {
        var run = await RunProcessAsync(Args("--input-args", "--frames 0 --fake-rc 23"));
        Assert.Equal(1, run.ExitCode);
        using var json = ParseSingleLine(run.Output);
        Assert.Equal(23, json.RootElement.GetProperty("rc").GetInt32());
        Assert.Contains("FAKE requested rc=23", json.RootElement.GetProperty("stderrTail").GetString());
    }

    [Fact]
    public async Task PreviewEncoderErrorReturnsExitOneWithDiagnostics()
    {
        var run = await RunProcessAsync(Args("--mode", "oneshot", "--vpp", "--fake-rc 19"));
        Assert.Equal(1, run.ExitCode);
        using var json = ParseSingleLine(run.Output);
        Assert.Equal(19, json.RootElement.GetProperty("rc").GetInt32());
        Assert.Equal("oneshot", json.RootElement.GetProperty("mode").GetString());
        Assert.Contains("FAKE requested rc=19", json.RootElement.GetProperty("stderrTail").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("frameCount").GetInt32());
    }

    [Fact]
    public async Task ProbeTimeoutUsesFakeClockAndReturnsExitThree()
    {
        var clock = new ProbeStartClock();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var run = CliProgram.RunAsync(Args("--mode", "oneshot", "--input-args", "--fake-hang-after 0"), output, clock);
        // help取得後に入力検査のタイマーが登録されるまで待ち、登録前Advanceとの競合を避ける。
        await clock.ProbeTimerRegistered.Task.WaitAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(3, await run.WaitAsync(SafetyTimeout));
        using var json = ParseSingleLine(output.ToString());
        Assert.Equal(3, json.RootElement.GetProperty("rc").GetInt32());
        Assert.True(json.RootElement.GetProperty("timedOut").GetBoolean());
        Assert.Contains("タイムアウト", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ServerOnlyControllerDoesNotUseFallback()
    {
        var unsupported = new UnsupportedBackend();
        var fallback = new UnsupportedBackend();
        await using var controller = new PreviewController(unsupported, fallback, debounce: TimeSpan.Zero, enableFallback: false);
        PreviewFailure? failure = null;
        controller.Failed += value => failure = value;
        controller.Submit(new PreviewRequest { EncoderPath = "fake", InputFile = "input", Fps = 30 });
        await controller.WaitForIdleAsync();
        Assert.IsType<PreviewServerNotSupportedException>(failure!.Exception);
        Assert.Equal(1, unsupported.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Theory]
    [InlineData("", 7, 121)]
    [InlineData("--vpp-kfm", 7, 121)]
    public async Task AbsoluteClockReportsActualPhysicalWindowAndDumpsRawTimestamps(string filter, double seek, int frames)
    {
        string path = Path.Combine(Path.GetTempPath(), $"viewer-cli-physical-{Guid.NewGuid():N}.y4m");
        try
        {
            var run = await RunProcessAsync(Args("--mode", "oneshot", "--time", "10", "--pre-margin", "2",
                "--post-margin", "1", "--dump-y4m", path, "--vpp", filter), capabilities: true);
            Assert.Equal(0, run.ExitCode);
            using var json = ParseSingleLine(run.Output);
            Assert.Equal(seek, json.RootElement.GetProperty("seekSeconds").GetDouble());
            Assert.Equal(frames, json.RootElement.GetProperty("frames").GetInt32());
            Assert.Equal(1010, json.RootElement.GetProperty("selectedXts").GetDouble(), 6);
            using var stream = File.OpenRead(path);
            var reader = new Y4mReader(stream);
            var first = reader.ReadFrame();
            Assert.NotNull(first);
            Assert.Equal(seek.ToString(CultureInfo.InvariantCulture), first.Header.ExtTags["XSEEK"]);
            Assert.Equal(1000 + seek, first.Xts!.Value, 6);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("--fake-clock-backstep-at", 1000)]
    [InlineData("--fake-clock-jump-at", 1000)]
    [InlineData("--fake-clock-backstep-at", 0)]
    public async Task ReplayReportsPhysicalClockAndDumpsOnlyTheWinningFiniteFrames(string anomaly, double origin)
    {
        string path = Path.Combine(Path.GetTempPath(), $"viewer-cli-replay-{Guid.NewGuid():N}.y4m");
        try
        {
            var run = await RunProcessAsync(Args("--mode", "oneshot", "--time", "10", "--pre-margin", "2",
                "--post-margin", "1", "--input-args", "--avhw " + anomaly + " 95 --fake-origin-seconds "
                    + origin.ToString(CultureInfo.InvariantCulture), "--dump-y4m", path), capabilities: true);
            Assert.Equal(0, run.ExitCode);
            using var json = ParseSingleLine(run.Output);
            Assert.False(json.RootElement.GetProperty("timestampPassthrough").GetBoolean());
            Assert.False(json.RootElement.GetProperty("trustedAbsolute").GetBoolean());
            Assert.Equal(origin, json.RootElement.GetProperty("timestampOriginSeconds").GetDouble());
            using var stream = File.OpenRead(path);
            var reader = new Y4mReader(stream);
            int count = 0;
            while (reader.ReadFrame() is { } frame)
            {
                Assert.InRange(frame.Xts!.Value, 0, 5);
                ++count;
            }
            Assert.Equal(json.RootElement.GetProperty("frameCount").GetInt32(), count);
            Assert.Equal(json.RootElement.GetProperty("frames").GetInt32(), count);
            Assert.InRange(json.RootElement.GetProperty("selectedXts").GetDouble(), 0, 3);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("oneshot", "", 2, 7, "QSVEnc")]
    [InlineData("server", "", 0, 9, "QSVEnc")]
    [InlineData("oneshot", "--vpp-tweak brightness=0.1", 2, 7, "QSVEnc")]
    [InlineData("server", "--vpp-tweak brightness=0.1", 0, 9, "QSVEnc")]
    [InlineData("oneshot", "--vpp-kfm", 2, 7, "QSVEnc")]
    [InlineData("server", "--vpp-kfm", 2, 7, "QSVEnc")]
    [InlineData("server", "", 0, 9, "VCEEnc")]
    [InlineData("server", "", 0, 9, "rkmppenc")]
    [InlineData("server", "", 0, 9, "NVEnc")]
    [InlineData("server", "", 0, 9, "")]
    public async Task JsonReportsActualWarmupAndSafeStartForSpatialAndTemporalRequests(string mode, string filter, double warmup, double seek, string encoder)
    {
        var args = Args("--mode", mode, "--time", "10", "--pre-margin", "2", "--post-margin", "1",
            "--input-args", "--avhw");
        if (filter.Length > 0) args = args.Concat(new[] { "--vpp", filter }).ToArray();
        var run = await RunProcessAsync(args, capabilities: true, readyEncoder: encoder);
        Assert.True(run.ExitCode == 0,
            $"CLI終了コードが0ではありません: rc={run.ExitCode}, mode={mode}, encoder={encoder}, filter={filter}\nstdout:\n{run.Output}\nstderr:\n{run.StandardError}");
        using var json = ParseSingleLine(run.Output);
        var root = json.RootElement;
        Assert.True(root.GetProperty("trustedAbsolute").GetBoolean());
        Assert.Equal(warmup, root.GetProperty("warmupSeconds").GetDouble());
        Assert.Equal(seek, root.GetProperty("seekSeconds").GetDouble());
        // 2秒側の物理位置を保ちながら、どちらもGOPの1秒前余白から表示可能にする。
        Assert.Equal(9, root.GetProperty("safeStartSeconds").GetDouble());
        Assert.Equal(1010, root.GetProperty("selectedXts").GetDouble(), 5);
    }

    private static string[] Args(params string[] extra) => new[]
    {
        "--enc", BackendTests.FakeEncoderPath(), "--input", "fake input.ts", "--time", "0:00:00.300",
        "--pre-margin", "0.1", "--post-margin", "0.1",
    }.Concat(extra).ToArray();

    private static JsonDocument ParseSingleLine(string output)
    {
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        return JsonDocument.Parse(lines[0]);
    }

    private static async Task<ProcessResult> RunProcessAsync(string[] args, bool capabilities = false, string? readyEncoder = null)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "HWEncParamViewer.Cli"))) directory = directory.Parent;
        Assert.NotNull(directory);
        string executable = Path.Combine(directory.FullName, "src", "HWEncParamViewer.Cli", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "HWEncParamViewer.Cli.exe" : "HWEncParamViewer.Cli");
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (capabilities) start.Environment["FAKE_ENCODER_PREVIEW_CAPABILITIES"] = "1";
        if (readyEncoder is not null)
        {
            start.Environment["FAKE_ENCODER_READY_ENCODER"] = readyEncoder;
            start.Environment["FAKE_ENCODER_PROBE_SUPPORTED"] = "1";
        }
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(SafetyTimeout);
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string StandardError);
    private sealed class UnsupportedBackend : IPreviewBackend
    {
        public int Calls { get; private set; }
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            ++Calls;
            throw new PreviewServerNotSupportedException("非対応");
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ProbeStartClock : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        public TaskCompletionSource ProbeTimerRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(30)) ProbeTimerRegistered.TrySetResult();
            return timer;
        }
        public void Advance(TimeSpan delta) => _inner.Advance(delta);
    }

}

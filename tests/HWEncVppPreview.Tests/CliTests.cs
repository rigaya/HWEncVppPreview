using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HWEncVppPreview.Core;
using CliProgram = HWEncVppPreview.Cli.Program;

namespace HWEncVppPreview.Tests;

public sealed class CliTests
{
    [Fact]
    public async Task ServerProducesSingleJsonAndMatchingDumps()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"viewer-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string raw = Path.Combine(directory, "selected.raw");
        string y4m = Path.Combine(directory, "interval.y4m");
        try
        {
            var run = await RunPreviewAsync(Args("--dump", raw, "--dump-y4m", y4m,
                "--vpp", "--vpp-nlmeans sigma=0.1"));
            Assert.Equal(0, run.ExitCode);
            using var json = ParseSingleLine(run.Output);
            var root = json.RootElement;
            Assert.Equal(0, root.GetProperty("rc").GetInt32());
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
        var args = ArgumentErrorArgs();
        args[Array.IndexOf(args, "--time") + 1] = time;
        var run = await RunArgumentErrorProcessAsync(args);
        Assert.Equal(2, run.ExitCode);
        using var json = ParseSingleLine(run.Output);
        Assert.Equal(2, json.RootElement.GetProperty("rc").GetInt32());
        Assert.NotNull(json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnknownOptionAndMissingRequiredArgumentsReturnArgumentExit()
    {
        foreach (var args in new[] { Array.Empty<string>(), ArgumentErrorArgs("--invalid", "value"), ArgumentErrorArgs("--mode", "invalid") })
        {
            var run = await RunArgumentErrorProcessAsync(args);
            Assert.Equal(2, run.ExitCode);
            using var json = ParseSingleLine(run.Output);
            Assert.Equal(2, json.RootElement.GetProperty("rc").GetInt32());
        }
    }

    [Theory]
    [InlineData("-0.001")]
    [InlineData("-1")]
    [InlineData("0")]
    public async Task PublicCliRejectsNonPositiveWatchdog(string seconds)
    {
        var run = await RunArgumentErrorProcessAsync(ArgumentErrorArgs("--watchdog-seconds", seconds));
        Assert.Equal(2, run.ExitCode);
        using var json = ParseSingleLine(run.Output);
        Assert.Equal(2, json.RootElement.GetProperty("rc").GetInt32());
        Assert.NotEmpty(json.RootElement.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task ProbeEncoderErrorReturnsExitOneWithDiagnostics()
    {
        var run = await RunPreviewAsync(Args("--input-args", "--frames 0 --fake-rc 23"));
        Assert.Equal(1, run.ExitCode);
        using var json = ParseSingleLine(run.Output);
        Assert.Equal(23, json.RootElement.GetProperty("rc").GetInt32());
        Assert.Contains("FAKE requested rc=23", json.RootElement.GetProperty("stderrTail").GetString());
    }

    [Fact]
    public async Task PreviewEncoderErrorReturnsExitOneWithDiagnostics()
    {
        var run = await RunPreviewAsync(Args("--vpp", "--fake-rc 19"));
        Assert.Equal(1, run.ExitCode);
        using var json = ParseSingleLine(run.Output);
        Assert.Equal(19, json.RootElement.GetProperty("rc").GetInt32());
        Assert.Contains("FAKE requested rc=19", json.RootElement.GetProperty("stderrTail").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("frameCount").GetInt32());
    }

    [Fact]
    public void ProbeTimeoutIsReportedAsExitThree()
    {
        var result = new CliProgram.CliResult();
        CliProgram.ApplyUnexpectedFailure(result, new InputProbeTimeoutException("タイムアウト", "入力診断"));
        string text = JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var json = ParseSingleLine(text);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(3, json.RootElement.GetProperty("rc").GetInt32());
        Assert.True(json.RootElement.GetProperty("timedOut").GetBoolean());
        Assert.Contains("タイムアウト", json.RootElement.GetProperty("error").GetString());
        Assert.Equal("入力診断", json.RootElement.GetProperty("stderrTail").GetString());
    }

    [Fact]
    public async Task UnsupportedServerReportsFailure()
    {
        var unsupported = new UnsupportedBackend();
        await using var controller = new PreviewController(unsupported, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        controller.Failed += value => failure = value;
        controller.Submit(new PreviewRequest { EncoderPath = "fake", InputFile = "input", Fps = 30 });
        await controller.WaitForIdleAsync();
        Assert.IsType<PreviewServerNotSupportedException>(failure!.Exception);
        Assert.Equal(1, unsupported.Calls);
    }

    [Theory]
    [InlineData("", 7, 121)]
    [InlineData("--vpp-kfm", 7, 121)]
    public async Task AbsoluteClockReportsActualPhysicalWindowAndDumpsRawTimestamps(string filter, double seek, int frames)
    {
        string path = Path.Combine(Path.GetTempPath(), $"viewer-cli-physical-{Guid.NewGuid():N}.y4m");
        try
        {
            var run = await RunPreviewAsync(Args("--time", "10", "--pre-margin", "2",
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
            var run = await RunPreviewAsync(Args("--time", "10", "--pre-margin", "2",
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
    [InlineData("", 0, 9, "QSVEnc")]
    [InlineData("--vpp-tweak brightness=0.1", 0, 9, "QSVEnc")]
    [InlineData("--vpp-kfm", 2, 7, "QSVEnc")]
    [InlineData("", 0, 9, "VCEEnc")]
    [InlineData("", 0, 9, "rkmppenc")]
    [InlineData("", 0, 9, "NVEnc")]
    [InlineData("", 0, 9, "")]
    public async Task JsonReportsActualWarmupAndSafeStartForSpatialAndTemporalRequests(string filter, double warmup, double seek, string encoder)
    {
        var args = Args("--time", "10", "--pre-margin", "2", "--post-margin", "1",
            "--input-args", "--avhw");
        if (filter.Length > 0) args = args.Concat(new[] { "--vpp", filter }).ToArray();
        var run = await RunPreviewAsync(args, capabilities: true, readyEncoder: encoder);
        Assert.True(run.ExitCode == 0,
            $"CLI終了コードが0ではありません: rc={run.ExitCode}, encoder={encoder}, filter={filter}\nJSON:\n{run.Output}");
        using var json = ParseSingleLine(run.Output);
        var root = json.RootElement;
        Assert.True(root.GetProperty("trustedAbsolute").GetBoolean());
        Assert.Equal(warmup, root.GetProperty("warmupSeconds").GetDouble());
        Assert.Equal(seek, root.GetProperty("seekSeconds").GetDouble());
        // 2秒側の物理位置を保ちながら、どちらもGOPの1秒前余白から表示可能にする。
        Assert.Equal(9, root.GetProperty("safeStartSeconds").GetDouble());
        Assert.Equal(1010, root.GetProperty("selectedXts").GetDouble(), 5);
    }

    private static string[] Args(params string[] extra) => ArgsWithEncoder(BackendTests.FakeEncoderPath(), extra);

    private static string[] ArgumentErrorArgs(params string[] extra)
        => ArgsWithEncoder(Path.Combine(Path.GetTempPath(), $"存在しない-encoder-{Guid.NewGuid():N}"), extra);

    private static string[] ArgsWithEncoder(string encoder, string[] extra) => new[]
    {
        "--enc", encoder, "--input", "fake input.ts", "--time", "0:00:00.300",
        "--pre-margin", "0.1", "--post-margin", "0.1",
    }.Concat(extra).ToArray();

    private static JsonDocument ParseSingleLine(string output)
    {
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        return JsonDocument.Parse(lines[0]);
    }

    private static async Task<RunResult> RunPreviewAsync(string[] args, bool capabilities = false, string? readyEncoder = null)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        int code = await CliProgram.RunAsync(args, output, PreviewTimeouts.Infinite, configureEncoderStartInfo: start =>
        {
            if (capabilities) start.Environment["FAKE_ENCODER_PREVIEW_CAPABILITIES"] = "1";
            if (readyEncoder is not null)
            {
                start.Environment["FAKE_ENCODER_READY_ENCODER"] = readyEncoder;
                start.Environment["FAKE_ENCODER_PROBE_SUPPORTED"] = "1";
            }
        });
        return new(code, output.ToString());
    }

    private static async Task<RunResult> RunArgumentErrorProcessAsync(string[] args)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "HWEncVppPreview.Cli"))) directory = directory.Parent;
        Assert.NotNull(directory);
        string executable = Path.Combine(directory.FullName, "src", "HWEncVppPreview.Cli", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "HWEncVppPreview.Cli.exe" : "HWEncVppPreview.Cli");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync());
            return new(process.ExitCode, await output);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private sealed record RunResult(int ExitCode, string Output);
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


}

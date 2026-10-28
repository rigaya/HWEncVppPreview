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
        var clock = new FakeTimeProvider();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var run = CliProgram.RunAsync(Args("--mode", "oneshot", "--input-args", "--fake-hang-after 0"), output, clock);
        // RunAsyncが戻った時点で、終わらない入力検査のタイマーが登録されている。
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

    private static async Task<ProcessResult> RunProcessAsync(string[] args)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "HWEncParamViewer.Cli"))) directory = directory.Parent;
        Assert.NotNull(directory);
        string executable = Path.Combine(directory.FullName, "src", "HWEncParamViewer.Cli", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "HWEncParamViewer.Cli.exe" : "HWEncParamViewer.Cli");
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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
}

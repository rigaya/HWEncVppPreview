using System.Diagnostics;
using System.IO.Pipes;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HWEncVppPreview.Cli;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;
using CliProgram = HWEncVppPreview.Cli.Program;

namespace HWEncVppPreview.Tests;

public sealed class CliHelpCheckTests
{
    private const string Prefix = "VPP Options:\n\n  [denoise] Denoise\n";
    private const string Valid = Prefix + "   --vpp-example [<param1>=<value>][,<param2>=<value>][...]\n    params\n"
        + "      amount=<float> Amount. (default: 0.25, range: 0..1)\n"
        + "      dimensions=<int>x<int> Dimensions. (default: derived)\n";

    // 更新: <encoder> --help > tests/Data/help/current/<name>.txt
    // 検査: <cli> --check-help --help-file tests/Data/help/current/<name>.txt
    [Theory]
    [InlineData("qsvenc", 81, 606)]
    [InlineData("nvenc", 80, 671)]
    [InlineData("vceenc", 68, 565)]
    public async Task CurrentHelpIsFullyUsable(string encoder, int options, int parameters)
    {
        using var run = await RunAsync("--check-help", "--help-file", Data("current", encoder + ".txt"));
        Assert.Equal(0, run.ExitCode);
        Assert.True(run.Json.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(options, run.Json.RootElement.GetProperty("optionCount").GetInt32());
        Assert.Equal(parameters, run.Json.RootElement.GetProperty("parameterCount").GetInt32());
        Assert.Empty(run.Json.RootElement.GetProperty("degradedOptions").EnumerateArray());
        Assert.Empty(run.Json.RootElement.GetProperty("degradedParameters").EnumerateArray());
    }

    [Theory]
    [InlineData("qsvenc", 85, 638)]
    [InlineData("nvenc", 80, 635)]
    [InlineData("vceenc", 79, 637)]
    public async Task LegacyHelpListsEveryDegradedOptionAndParameter(string encoder, int options, int parameters)
    {
        string file = Data(encoder + ".txt");
        using var run = await RunAsync("--help-file", file, "--check-help");
        var json = run.Json.RootElement;
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(1, json.GetProperty("exitCode").GetInt32());
        Assert.False(json.GetProperty("valid").GetBoolean());
        Assert.True(json.GetProperty("strict").GetBoolean());
        Assert.Empty(json.GetProperty("missingIndex").EnumerateArray());
        Assert.Empty(json.GetProperty("emptyLookup").EnumerateArray());
        Assert.Empty(json.GetProperty("missingSchema").EnumerateArray());
        string text = await File.ReadAllTextAsync(file);
        string[] expected = Regex.Matches(text, @"--vpp-[a-z0-9-]+").Select(match => match.Value)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(options, expected.Length);
        Assert.Equal(expected, json.GetProperty("degradedOptions").EnumerateArray().Select(value => value.GetString()).ToArray());
        var names = json.GetProperty("degradedParameters").EnumerateArray()
            .Select(value => (value.GetProperty("option").GetString(), value.GetProperty("parameter").GetString())).ToArray();
        Assert.Equal(parameters, names.Length);
        Assert.Equal(parameters, names.Distinct().Count());
        Assert.Contains(("--vpp-nlmeans", "sigma"), names);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("error").ValueKind);
    }

    [Theory]
    [InlineData("   --vpp-switch Enable filter.\n")]
    [InlineData("   --vpp-value <integer> Structured string.\n")]
    [InlineData("   --vpp-value <from>:<to> Mapping. (default: derived)\n")]
    [InlineData("   --vpp-example [<param1>=<value>][,<param2>=<value>][...]\n    params\n      path=<path> Optional path.\n")]
    public async Task SwitchStructuredValuesAndOptionalDefaultsAreValid(string body)
    {
        using var input = new TextFile(Prefix + body);
        using var run = await RunAsync("--check-help", "--help-file", input.Path);
        Assert.Equal(0, run.ExitCode);
        Assert.True(run.Json.RootElement.GetProperty("valid").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not help")]
    [InlineData("VPP Options:\n   --vpp-value <int> Value. (default: 1)\n")]
    [InlineData("VPP Options:\n  [unknown] Unknown\n   --vpp-value <int> Value. (default: 1)\n")]
    [InlineData("VPP Options:\n  [denoise] Denoise\n      --vpp-example is an example\n")]
    public async Task EmptyMissingCategoryAndUnindexedNamesAreNotSuccess(string text)
    {
        using var input = new TextFile(text);
        using var run = await RunAsync("--check-help", "--help-file", input.Path);
        Assert.Equal(1, run.ExitCode);
        Assert.False(run.Json.RootElement.GetProperty("valid").GetBoolean());
    }

    [Theory]
    [InlineData("      invalid=int Invalid. (default: 1)\n")]
    [InlineData("      invalid=<int|auto> Invalid. (default: 1)\n")]
    public async Task InvalidParameterListsItsNameWithoutRejectingValidNeighbors(string declaration)
    {
        using var input = new TextFile(Prefix + "   --vpp-example [<param1>=<value>][,<param2>=<value>][...]\n    params\n"
            + declaration + "      valid=<int> Valid. (default: 2)\n");
        using var run = await RunAsync("--check-help", "--help-file", input.Path);
        Assert.Equal(1, run.ExitCode);
        var bad = Assert.Single(run.Json.RootElement.GetProperty("degradedParameters").EnumerateArray());
        Assert.Equal("--vpp-example", bad.GetProperty("option").GetString());
        Assert.Equal("invalid", bad.GetProperty("parameter").GetString());
        Assert.Empty(run.Json.RootElement.GetProperty("degradedOptions").EnumerateArray());
    }

    public static IEnumerable<object[]> InvalidArguments()
    {
        yield return new object[] { new[] { "--check-help" } };
        yield return new object[] { new[] { "--check-help", "--enc", "a", "--help-file", "b" } };
        yield return new object[] { new[] { "--check-help", "--enc" } };
        yield return new object[] { new[] { "--check-help", "--help-file", "" } };
        yield return new object[] { new[] { "--check-help", "--check-help", "--help-file", "b" } };
        yield return new object[] { new[] { "--check-help", "--help-file", "a", "--help-file", "b" } };
        yield return new object[] { new[] { "--check-help", "--help-file", "a", "--mode", "server" } };
        foreach (string value in new[] { "0", "-0.001", "-1", "NaN", "Infinity", "1e100" })
            yield return new object[] { new[] { "--check-help", "--help-file", "a", "--watchdog-seconds", value } };
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task UsageErrorsReturnTwoAndOneJsonLine(string[] args)
    {
        using var run = await RunAsync(args);
        Assert.Equal(2, run.ExitCode);
        Assert.Equal(2, run.Json.RootElement.GetProperty("exitCode").GetInt32());
        Assert.NotEmpty(run.Json.RootElement.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task FileAndStartErrorsReturnThreeWithoutLookingLikeLegacyHelp()
    {
        string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        foreach (string[] args in new[] {
            new[] { "--check-help", "--help-file", missing },
            new[] { "--check-help", "--help-file", System.IO.Path.GetTempPath() },
            new[] { "--check-help", "--help-file", "invalid\0path" },
            new[] { "--check-help", "--enc", missing } })
        {
            using var run = await RunAsync(args);
            Assert.Equal(3, run.ExitCode);
            Assert.False(run.Json.RootElement.GetProperty("valid").GetBoolean());
            Assert.NotEmpty(run.Json.RootElement.GetProperty("error").GetString()!);
        }
    }

    [Fact]
    public async Task ModeTextInsideAnImageValueDoesNotSelectHelpValidation()
    {
        using var run = await RunAsync("--vpp", "--check-help");
        Assert.Equal(2, run.ExitCode);
        Assert.False(run.Json.RootElement.TryGetProperty("strict", out _));
    }

    [Fact]
    public void EncoderCapturePassesOnlyTheHelpArgument()
    {
        var info = HelpProcessCapture.StartInfo("指定されたexe");
        Assert.Equal("指定されたexe", info.FileName);
        Assert.Equal(new[] { "--help" }, info.ArgumentList);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureDrainsBothStreamsIncludingStderrOnlyHelp(bool stderrOnly)
    {
        await using var child = new FakeCapture(stderrOnly ? "stderr" : "stdout");
        Task<HelpProcessResult> capture = CaptureUntilExitAsync(child.StartInfo());
        int pid = await child.ReadPidAsync(capture);
        var result = await capture;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Valid, stderrOnly ? result.StandardError : result.StandardOutput);
        if (!stderrOnly) Assert.Equal(131072, result.StandardError.Length);
        AssertStopped(pid);
    }

    [Fact]
    public async Task CaptureRetainsNonzeroExitAndDiagnostics()
    {
        await using var child = new FakeCapture("error");
        Task<HelpProcessResult> capture = CaptureUntilExitAsync(child.StartInfo());
        int pid = await child.ReadPidAsync(capture);
        var result = await capture;
        Assert.Equal(23, result.ExitCode);
        Assert.Contains("child failed", result.StandardError);
        AssertStopped(pid);
    }

    [Fact]
    public async Task CaptureTimeoutRequestsKillAndReportsCompletedCleanup()
    {
        var clock = new FakeTimeProvider();
        var timers = clock.ObserveTimers();
        var captureTimeout = TimeSpan.FromSeconds(2);
        var cleanupTimeout = TimeSpan.FromSeconds(5);
        var child = new ControlledHelpProcess();
        Task<HelpProcessResult> capture = HelpProcessCapture.CaptureAsync(
            () => child, clock, captureTimeout, cleanupTimeout);

        Assert.Equal(captureTimeout, await timers.ReadAsync());
        clock.Advance(captureTimeout);
        await child.KillRequested.Task;
        Assert.Equal(cleanupTimeout, await timers.ReadAsync());
        child.Exit(23);

        var error = await Assert.ThrowsAsync<HelpCaptureException>(() => capture);
        Assert.True(error.TimedOut);
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Contains("help", error.Message, StringComparison.Ordinal);
        Assert.Equal(23, error.EncoderExitCode);
        Assert.Equal(ControlledHelpProcess.ErrorText, error.StandardError);
        Assert.Null(error.CleanupError);
        Assert.False(child.PipesClosed);
        await child.Disposed.Task;
    }

    [Fact]
    public async Task CleanupTimeoutClosesPipesAndDisposesAfterLateExit()
    {
        var clock = new FakeTimeProvider();
        var timers = clock.ObserveTimers();
        var captureTimeout = TimeSpan.FromSeconds(2);
        var cleanupTimeout = TimeSpan.FromSeconds(5);
        var child = new ControlledHelpProcess();
        Task<HelpProcessResult> capture = HelpProcessCapture.CaptureAsync(
            () => child, clock, captureTimeout, cleanupTimeout);

        try
        {
            Assert.Equal(captureTimeout, await timers.ReadAsync());
            clock.Advance(captureTimeout);
            await child.KillRequested.Task;
            Assert.Equal(cleanupTimeout, await timers.ReadAsync());
            clock.Advance(cleanupTimeout);

            var error = await Assert.ThrowsAsync<HelpCaptureException>(() => capture);
            Assert.True(error.TimedOut);
            Assert.NotNull(error.CleanupError);
            Assert.Null(error.EncoderExitCode);
            Assert.True(child.PipesClosed);
            // 終了を通知するまでは、回収が期限切れでも所有プロセスを破棄できない。
            Assert.False(child.Disposed.Task.IsCompleted);
        }
        finally
        {
            child.Exit(23);
        }

        await child.Disposed.Task;
    }

    [Fact]
    public async Task ExplicitCancellationForciblyReapsEncoder()
    {
        await using var child = new FakeCapture("hang");
        using var cancellation = new CancellationTokenSource();
        Task<HelpProcessResult> capture = CaptureUntilExitAsync(child.StartInfo(), cancellation.Token);
        try
        {
            int pid = await child.ReadPidAsync(capture);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
            AssertStopped(pid);
        }
        finally
        {
            child.ClosePidPipe();
            cancellation.Cancel();
            await ((Task)capture).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        await using var next = new FakeCapture("stdout");
        Task<HelpProcessResult> recovery = CaptureUntilExitAsync(next.StartInfo());
        int nextPid = await next.ReadPidAsync(recovery);
        Assert.Equal(0, (await recovery).ExitCode);
        AssertStopped(nextPid);
    }

    [Fact]
    public async Task CaptureCompletesAfterInheritedPipesAreReleased()
    {
        string name = "help-child-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var parent = new FakeCapture("inherited");
        ProcessStartInfo info = parent.StartInfo();
        info.Environment["FAKE_ENCODER_HOLD_OUTPUT_PIPE"] = name;
        Task<HelpProcessResult> capture = CaptureUntilExitAsync(info);
        try
        {
            int parentPid = await parent.ReadPidAsync(capture);
            await PipeWait.UntilConnectedAsync(pipe, name, capture);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            string? pid = await reader.ReadLineAsync();
            Assert.True(int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out int childPid) && childPid > 0);
            await pipe.WriteAsync("release\n"u8.ToArray());
            Assert.Equal("0", await reader.ReadLineAsync());
            Assert.Null(await reader.ReadLineAsync());
            Assert.Equal(0, (await capture).ExitCode);
            AssertStopped(parentPid);
        }
        finally
        {
            parent.ClosePidPipe();
            pipe.Dispose();
            await ((Task)capture).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task CancelledCaptureDoesNotStartAChild()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HelpProcessCapture.CaptureAsync(
            () => throw new InvalidOperationException("取消後にプロセスが作成された"),
            TimeProvider.System, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan, stop.Token));
    }

    private static Task<HelpProcessResult> CaptureUntilExitAsync(ProcessStartInfo start, CancellationToken token = default)
        => HelpProcessCapture.CaptureAsync(start, TimeProvider.System,
            PreviewTimeouts.Infinite.CliHelp, PreviewTimeouts.Infinite.HelpCleanup, token);

    private static string Data(params string[] names) => System.IO.Path.Combine(new[] { AppContext.BaseDirectory, "Data", "help" }.Concat(names).ToArray());
    private static async Task<Run> RunAsync(params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        int code = await CliProgram.RunAsync(args, output, PreviewTimeouts.Infinite);
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        return new(code, JsonDocument.Parse(lines[0]));
    }
    private sealed record Run(int ExitCode, JsonDocument Json) : IDisposable
    { public void Dispose() => Json.Dispose(); }

    private static void AssertStopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited, "所有した子が残っている: " + pid); }
        catch (ArgumentException) { }
    }

    private sealed class TextFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "viewer-help-" + Guid.NewGuid().ToString("N") + ".txt");
        public TextFile(string text) => File.WriteAllText(Path, text);
        public void Dispose() => File.Delete(Path);
    }

    private sealed class ControlledHelpProcess : HelpProcessCapture.IHelpProcess
    {
        public const string ErrorText = "偽プロセスの標準エラー";
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _output = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource KillRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PipesClosed { get; private set; }
        public bool HasExited => _exit.Task.IsCompletedSuccessfully;
        public int ExitCode => _exit.Task.GetAwaiter().GetResult();

        public bool Start() => true;
        public Task<string> ReadStandardOutputAsync() => _output.Task;
        public Task<string> ReadStandardErrorAsync() => Task.FromResult(ErrorText);
        public Task WaitForExitAsync() => _exit.Task;
        public void KillTree() => KillRequested.SetResult();

        public void Exit(int code)
        {
            _output.TrySetResult("");
            _exit.SetResult(code);
        }

        public void ClosePipes()
        {
            PipesClosed = true;
            _output.TrySetResult("");
        }

        public void Dispose() => Disposed.SetResult();
    }

    private sealed class FakeCapture : IAsyncDisposable
    {
        private readonly string _name = "help-pid-" + Guid.NewGuid().ToString("N");
        private readonly string _path = Path.Combine(Path.GetTempPath(), "help-input-" + Guid.NewGuid().ToString("N"));
        private readonly string _mode;
        private readonly NamedPipeServerStream _pipe;

        public FakeCapture(string mode)
        {
            _mode = mode;
            _pipe = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            File.WriteAllText(_path, Valid);
        }

        public ProcessStartInfo StartInfo()
        {
            var info = HelpProcessCapture.StartInfo(BackendTests.FakeEncoderPath());
            info.Environment["FAKE_ENCODER_PID_PIPE"] = _name;
            info.Environment["FAKE_ENCODER_HELP_MODE"] = _mode;
            info.Environment["FAKE_ENCODER_HELP_FILE"] = _path;
            return info;
        }

        public async Task<int> ReadPidAsync(Task capture)
        {
            await PipeWait.UntilConnectedAsync(_pipe, _name, capture);
            using var reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
            string value = await reader.ReadLineAsync() ?? throw new EndOfStreamException();
            return int.Parse(value, CultureInfo.InvariantCulture);
        }

        public void ClosePidPipe() => _pipe.Dispose();

        public ValueTask DisposeAsync()
        {
            _pipe.Dispose();
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }

}

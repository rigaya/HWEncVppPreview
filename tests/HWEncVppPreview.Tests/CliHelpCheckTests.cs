using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using HWEncVppPreview.Cli;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;
using CliProgram = HWEncVppPreview.Cli.Program;

namespace HWEncVppPreview.Tests;

public sealed class CliHelpCheckTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
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
        yield return new object[] { new[] { "--check-help", "--help-file", "a", "--mode", "oneshot" } };
        foreach (string value in new[] { "0", "-1", "NaN", "Infinity", "1e100" })
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
        await using var child = new ShellChild(stderrOnly ? "stderr" : "stdout");
        var result = await HelpProcessCapture.CaptureAsync(child.StartInfo(), TimeProvider.System, TestTimeouts.Hang).WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Valid, stderrOnly ? result.StandardError : result.StandardOutput);
        if (!stderrOnly) Assert.Equal(131072, result.StandardError.Length);
        AssertStopped(child.Pid);
    }

    [Fact]
    public async Task CaptureRetainsNonzeroExitAndDiagnostics()
    {
        await using var child = new ShellChild("error");
        var result = await HelpProcessCapture.CaptureAsync(child.StartInfo(), TimeProvider.System, TestTimeouts.Hang).WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(23, result.ExitCode);
        Assert.Contains("child failed", result.StandardError);
        AssertStopped(child.Pid);
    }

    [Fact]
    public async Task CaptureTimeoutUsesInjectedClockAndReapsItsParent()
    {
        await using var child = new ShellChild("hang");
        var clock = new CaptureClock();
        var running = HelpProcessCapture.CaptureAsync(child.StartInfo(), clock, TimeSpan.FromSeconds(3));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(3));
        await child.WaitUntilReadyAsync();
        clock.Advance(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<HelpCaptureException>(() => running.WaitWithDiagnosticsAsync(Safety));
        Assert.True(error.TimedOut);
        AssertStopped(child.Pid);
    }

    [Fact]
    public async Task ExitedParentWithInheritedPipesCannotWaitForever()
    {
        await using var child = new ShellChild("inherited");
        var clock = new CaptureClock();
        var running = HelpProcessCapture.CaptureAsync(child.StartInfo(), clock, TimeSpan.FromSeconds(3));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(3));
        await child.WaitUntilReadyAsync();
        // Readyはexit直前なので、親の実終了を確認してからpipeだけが残る期限を進める。
        await child.WaitForParentExitAsync();
        clock.Advance(TimeSpan.FromSeconds(3));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<HelpCaptureException>(() => running.WaitWithDiagnosticsAsync(Safety));
        Assert.True(error.TimedOut);
        Assert.Equal(0, error.EncoderExitCode);
        Assert.NotNull(error.CleanupError);
        // 孫のPIDはこの検査だけが所有し、helperへ新しいPID探索の製品機構は入れない。
    }

    [Fact]
    public async Task CancelledCaptureDoesNotStartAChild()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HelpProcessCapture.CaptureAsync(
            HelpProcessCapture.StartInfo("存在しないexe"), TimeProvider.System, TimeSpan.FromSeconds(5), stop.Token));
    }

    private static string Data(params string[] names) => System.IO.Path.Combine(new[] { AppContext.BaseDirectory, "Data", "help" }.Concat(names).ToArray());
    private static async Task<Run> RunAsync(params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        int code = await CliProgram.RunAsync(args, output).WaitWithDiagnosticsAsync(Safety);
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

    private sealed class CaptureClock : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();
        private readonly Channel<TimeSpan> _armed = Channel.CreateUnbounded<TimeSpan>();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public void Advance(TimeSpan delta) => _inner.Advance(delta);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            _armed.Writer.TryWrite(dueTime);
            return timer;
        }
        public async Task WaitForTimerAsync(TimeSpan expected)
        {
            TimeSpan actual = await _armed.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
            Assert.Equal(expected, actual);
        }
    }

    private sealed class ShellChild : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "viewer help child " + Guid.NewGuid().ToString("N"));
        private readonly string _script, _ready, _help, _pid;
        private int _parentPid;
        public int Pid => int.Parse(File.ReadAllText(_pid), CultureInfo.InvariantCulture);
        public ShellChild(string mode)
        {
            Directory.CreateDirectory(_directory);
            _script = System.IO.Path.Combine(_directory, OperatingSystem.IsWindows() ? "child.ps1" : "child.sh");
            _ready = System.IO.Path.Combine(_directory, "ready");
            _help = System.IO.Path.Combine(_directory, "help.txt");
            _pid = System.IO.Path.Combine(_directory, "pid");
            File.WriteAllText(_help, Valid);
            string script;
            if (OperatingSystem.IsWindows())
            {
                string body = mode switch
                {
                    "stdout" => "[Console]::Out.Write([IO.File]::ReadAllText($Help)); [Console]::Error.Write(('x' * 131072))",
                    "stderr" => "[Console]::Error.Write([IO.File]::ReadAllText($Help))",
                    "error" => "[Console]::Error.Write('child failed'); exit 23",
                    "hang" => "Start-Sleep -Seconds 600",
                    "inherited" => "$start = New-Object Diagnostics.ProcessStartInfo; $start.FileName = (Join-Path $PSHOME 'powershell.exe'); $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.Arguments = '-NoLogo -NoProfile -Command Start-Sleep -Seconds 600'; $owned = [Diagnostics.Process]::Start($start); [IO.File]::WriteAllText($PidPath, [string]$owned.Id)",
                    _ => throw new ArgumentOutOfRangeException(nameof(mode)),
                };
                script = "param([string]$Ready, [string]$Help, [string]$PidPath)\n[IO.File]::WriteAllText($PidPath, [string]$PID)\n"
                    + (mode == "inherited" ? body + "\n[IO.File]::WriteAllText($Ready, [string]$PID)\nexit 0\n"
                        : "[IO.File]::WriteAllText($Ready, [string]$PID)\n" + body + "\n");
            }
            else
            {
                string body = mode switch
                {
                    "stdout" => "cat \"$2\"; head -c 131072 /dev/zero | tr '\\0' x >&2",
                    "stderr" => "cat \"$2\" >&2",
                    "error" => "printf 'child failed' >&2; exit 23",
                    "hang" => "exec sleep 600",
                    "inherited" => "sleep 600 &\nprintf '%s' \"$!\" > \"$3\"\nprintf '%s' \"$$\" > \"$1\"\nexit 0",
                    _ => throw new ArgumentOutOfRangeException(nameof(mode)),
                };
                script = "printf '%s' \"$$\" > \"$3\"\n"
                    + (mode == "inherited" ? body + "\n" : "printf '%s' \"$$\" > \"$1\"\n" + body + "\n");
            }
            File.WriteAllText(_script, script, new UTF8Encoding(OperatingSystem.IsWindows()));
        }
        public ProcessStartInfo StartInfo()
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows()
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "/bin/sh");
            if (OperatingSystem.IsWindows())
                foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" }) start.ArgumentList.Add(argument);
            foreach (string argument in new[] { _script, _ready, _help, _pid }) start.ArgumentList.Add(argument);
            return start;
        }
        public async Task WaitUntilReadyAsync()
        {
            using var stop = new CancellationTokenSource(Safety);
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(_ready) && int.TryParse(File.ReadAllText(_ready), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out _parentPid) && _parentPid > 0) return;
                }
                catch (IOException) { }
                await Task.Yield();
            }
        }
        public async Task WaitForParentExitAsync()
        {
            try
            {
                using var process = Process.GetProcessById(_parentPid);
                await process.WaitForExitAsync().WaitWithDiagnosticsAsync(Safety);
            }
            catch (ArgumentException) { }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (File.Exists(_pid))
                {
                    try
                    {
                        using var process = Process.GetProcessById(Pid);
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitWithDiagnosticsAsync(Safety);
                    }
                    catch (ArgumentException) { }
                }
            }
            finally { Directory.Delete(_directory, recursive: true); }
        }
    }
}

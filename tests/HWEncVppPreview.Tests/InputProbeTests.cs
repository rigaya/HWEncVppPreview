using System.IO.Pipes;
using System.Text;
using HWEncVppPreview.Testing;
using System.Diagnostics;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class InputProbeTests
{
    [Fact]
    public async Task ReadsInputFormatWithFrameLimitAndCachesByArgumentValues()
    {
        var backend = new RecordingProbeBackend();
        var commands = backend.Commands;
        await using var probe = TestProcesses.CreateInputProbe(backend, TimeProvider.System);
        InputInfo first = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts", new[] { "--avhw" });
        InputInfo cached = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts", new List<string> { "--avhw" });
        Assert.Same(first, cached);
        Assert.Equal(64, first.Width);
        Assert.Equal(36, first.Height);
        Assert.Equal(30, first.Fps);
        Assert.Equal(30, first.FpsNum);
        Assert.Equal(1, first.FpsDen);
        Assert.Equal(1, first.SarNum);
        Assert.Equal(1, first.SarDen);
        Assert.Equal(Y4mChroma.C420, first.Chroma);
        Assert.Equal(8, first.BitDepth);
        Assert.Equal("420jpeg", first.ColorSpaceTag);
        var command = Assert.Single(commands);
        Assert.Equal(new[] { "--avhw", "--log-level", "input=debug", "-i", "input.ts", "--frames", "300", "--log-opt", "color=false" }, command);
    }

    [Fact]
    public async Task CacheKeyIncludesEncoderInputAndAdditionalArgumentsAndCopiesLists()
    {
        var backend = new RecordingProbeBackend();
        await using var probe = TestProcesses.CreateInputProbe(backend, TimeProvider.System);
        string encoder = BackendTests.FakeEncoderPath();
        var arguments = new List<string> { "--avhw" };
        await probe.ProbeAsync(encoder, "first.ts", arguments);
        arguments[0] = "--avsw";
        await probe.ProbeAsync(encoder, "first.ts", new[] { "--avhw" });
        Assert.Single(backend.Commands);
        await probe.ProbeAsync(encoder, "first.ts", arguments);
        await probe.ProbeAsync(encoder, "other.ts", arguments);
        string alternate = Path.GetDirectoryName(encoder) + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar + Path.GetFileName(encoder);
        await probe.ProbeAsync(alternate, "other.ts", arguments);
        Assert.Equal(4, backend.Commands.Count);
    }

    [Fact]
    public async Task ConcurrentSameRequestsShareSuccessfulCacheEntry()
    {
        int starts = 0;
        await using var probe = TestProcesses.CreateInputProbe(configureStartInfo: _ => ++starts);
        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts")));
        Assert.Equal(1, starts);
        Assert.All(results, item => Assert.Same(results[0], item));
    }

    [Fact]
    public async Task EncoderErrorContainsExitCodeAndDiagnosticsAndIsNotCached()
    {
        var backend = new RecordingProbeBackend();
        await using var probe = TestProcesses.CreateInputProbe(backend, TimeProvider.System);
        for (int i = 0; i < 2; ++i)
        {
            var error = await Assert.ThrowsAsync<InputProbeException>(() =>
                probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts", new[] { "--fake-rc", "23", "--fake-input-end", "0" }));
            Assert.Equal(23, error.ExitCode);
            Assert.Contains("FAKE requested rc=23", error.StandardError);
        }
        Assert.Equal(2, backend.Commands.Count);
    }

    [Fact]
    public async Task FirstFrameStopsAndReapsProcessWithoutWaitingForTheLimit()
    {
        string pipeName = "probe-first-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var probe = TestProcesses.CreateInputProbe(configureStartInfo: info => info.Environment["FAKE_ENCODER_PID_PIPE"] = pipeName);
        // 2枚目以降が応答しなくても、最初の1枚を受け取ったら取得と子の回収が終わる。
        Task<InputInfo> operation = probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts", new[] { "--fake-hang-after", "1" });
        int pid = await ReadPidFromPipeAsync(pipe, pipeName, operation);
        InputInfo input = await operation;
        Assert.Equal(30, input.Fps);
        await probe.DisposeAsync();
        AssertStopped(pid);
    }

    [Fact]
    public async Task FirstFrameMakesLaterEncoderExitCodeIrrelevant()
    {
        await using var probe = TestProcesses.CreateInputProbe();
        InputInfo input = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts",
            new[] { "--fake-rc", "23" });
        Assert.Equal(30, input.Fps);
    }

    [Fact]
    public async Task NoFrameCannotPopulateTheCacheEvenWithSuccessfulExit()
    {
        var backend = new RecordingProbeBackend();
        await using var probe = TestProcesses.CreateInputProbe(backend, TimeProvider.System);
        for (int i = 0; i < 2; ++i)
        {
            var error = await Assert.ThrowsAsync<InputProbeException>(() =>
                probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts", ["--fake-input-end", "0"]));
            Assert.Equal(-1, error.ExitCode);
        }
        Assert.Equal(2, backend.Commands.Count);
    }

    [Fact]
    public async Task TimeoutUsesInjectedClockAndDoesNotCacheFailure()
    {
        var clock = new FakeTimeProvider();
        await using var probe = TestProcesses.CreateProbeWithSimulatedDeadlines(new TimeoutProbeBackend(), clock);
        Task<InputInfo> run = probe.ProbeAsync("fake", "in.ts");
        // 要求を開始した時点で期限が時計に登録されている。
        clock.Advance(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<InputProbeTimeoutException>(() => run);
        Assert.Equal("fake diagnostics", error.StandardError);
        InputInfo recovered = await probe.ProbeAsync("fake", "in.ts");
        Assert.Equal(30, recovered.Fps);
    }

    [Fact]
    public async Task CancellationOfWaitingRequestDoesNotKillOtherProbe()
    {
        string name = "probe-frame-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var probe = TestProcesses.CreateInputProbe();
        using var waitingCancellation = new CancellationTokenSource();
        Task<InputInfo> active = probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts", new[] { "--fake-frame-pipe", name });
        await PipeWait.UntilConnectedAsync(pipe, name, active);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        Assert.Equal("ready", await reader.ReadLineAsync());
        Task<InputInfo> waiting = probe.ProbeAsync(BackendTests.FakeEncoderPath(), "next.ts", cancellationToken: waitingCancellation.Token);
        waitingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await writer.WriteLineAsync("release");
        InputInfo result = await active;
        Assert.Equal(64, result.Width);
    }

    [Fact]
    public async Task DisposeCancelsActiveProbeReapsProcessAndRejectsNewRequests()
    {
        string pipeName = "probe-pid-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var probe = TestProcesses.CreateInputProbe(configureStartInfo: info => info.Environment["FAKE_ENCODER_PID_PIPE"] = pipeName);
        try
        {
            Task<InputInfo> run = probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts", new[] { "--fake-hang-after", "0" });
            int pid = await ReadPidFromPipeAsync(pipe, pipeName, run);
            await probe.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            AssertStopped(pid);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts"));
            await probe.DisposeAsync();
        }
        finally
        {
            await probe.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("  Duration: 00:00:10.00, start: 0", 10)]
    [InlineData("  Duration: 01:52:30.25, start: 0", 6750.25)]
    [InlineData("  Duration: 100:00:00.1234567, start: 0", 360000.1234567)]
    [InlineData("  Duration: 00:00:00, start: 0", 0)]
    [InlineData("\u001b[31m  Duration: \u001b[39m00:00:10.50\u001b[0m, start: 0", 10.5)]
    public void DurationParserReadsInvariantTimeAndAnsiLogs(string log, double seconds)
    {
        TimeSpan? actual = InputProbe.ParseDuration(log);
        Assert.NotNull(actual);
        Assert.Equal(seconds, actual.Value.TotalSeconds, 7);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fps: 30")]
    [InlineData("  Duration: N/A, start: 0")]
    [InlineData("  Duration: -01:00:00, start: 0")]
    [InlineData("  Duration: 00:60:00, start: 0")]
    [InlineData("  Duration: 00:00:60, start: 0")]
    [InlineData("  Duration: 00:00:xx, start: 0")]
    [InlineData("  Duration: 00:00:00.12345678, start: 0")]
    [InlineData("  Duration: 999999999999999999999999:00:00, start: 0")]
    [InlineData("  Duration: 999999999999:00:00, start: 0")]
    public void DurationParserLeavesUnavailableOrInvalidDurationUnknown(string log)
    {
        Assert.Null(InputProbe.ParseDuration(log));
    }

    [Theory]
    [InlineData("00:23:39.98", 1419.98)]
    [InlineData("N/A", null)]
    [InlineData("invalid", null)]
    [InlineData("", null)]
    public void DurationParserIgnoresDelayedLogsFromPreviousInputs(string currentDuration, double? seconds)
    {
        string current = @"Z:\dvd\魔女 '06'.mkv";
        string log = "Input #0, mpegts, from 'Y:\\キャプチャ\\魔女.ts':\r\n"
            + "  Duration: 00:30:08.11, start: 51839.846433\r\n"
            + $"Input #0, matroska,webm, from '{current}':\r\n"
            + "  Metadata:\r\n    title: example\r\n"
            + (currentDuration.Length > 0 ? $"  Duration: {currentDuration}, start: 0\r\n" : "")
            + "Input #1, wav, from 'audio.wav':\r\n  Duration: 01:00:00.00, start: 0\r\n";
        var actual = InputProbe.ParseDuration(log, current);
        if (seconds.HasValue) Assert.Equal(seconds.Value, actual!.Value.TotalSeconds, 7);
        else Assert.Null(actual);
        Assert.Null(InputProbe.ParseDuration(log, "absent.mkv"));
    }

    [Fact]
    public void DurationParserDoesNotReuseEarlierDurationWhenLatestMatchingInputIsUnknown()
    {
        string log = "Input #0, matroska, from 'current.mkv':\n  Duration: 00:30:08.11, start: 0\n"
            + "Input #0, matroska, from 'current.mkv':\n  Duration: N/A, start: 0\n";
        Assert.Null(InputProbe.ParseDuration(log, "current.mkv"));
    }

    [Theory]
    [InlineData("00:00:10.25", 10.25)]
    [InlineData("100:00:00.00", 360000.0)]
    [InlineData("N/A", null)]
    [InlineData("invalid", null)]
    public async Task ReadsDurationFromDrainedStandardError(string value, double? seconds)
    {
        await using var probe = TestProcesses.CreateInputProbe();
        var info = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts",
            new[] { "--avhw", "--fake-duration", value, "--fake-hang-after", "1" });
        if (seconds.HasValue) Assert.Equal(TimeSpan.FromSeconds(seconds.Value), info.Duration);
        else Assert.Null(info.Duration);
    }

    [Fact]
    public void DurationParserDoesNotUseCurrentCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(TimeSpan.FromMilliseconds(1250), InputProbe.ParseDuration("  Duration: 00:00:01.25, start: 0"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task DurationAtExitIsReadAfterFormatProbeStoppedAndThenCached()
    {
        var commands = new List<string[]>();
        await using var probe = TestProcesses.CreateInputProbe(configureStartInfo: info => commands.Add(info.ArgumentList.ToArray()));
        string[] args = ["--avhw", "--fake-duration", "00:30:07.56", "--fake-duration-at-exit"];
        var input = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts", args);
        Assert.Null(input.Duration);
        var duration = await probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts", args);
        Assert.Equal(TimeSpan.FromSeconds(1807.56), duration);
        Assert.Equal(duration, await probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts", args));
        Assert.Equal(duration, (await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts", args)).Duration);
        Assert.Equal(2, commands.Count);
        Assert.Equal(args.Concat(new[] { "-i", "input.ts", "--log-level", "input=debug", "--frames", "1", "-c", "raw", "-o", "-" }), commands[1]);
    }

    [Fact]
    public async Task KnownDurationFromFormatProbeAvoidsAnotherProcess()
    {
        int starts = 0;
        await using var probe = TestProcesses.CreateInputProbe(configureStartInfo: _ => ++starts);
        string[] args = ["--fake-duration", "00:00:10.00", "--fake-hang-after", "1"];
        var input = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts", args);
        Assert.Equal(TimeSpan.FromSeconds(10), input.Duration);
        Assert.Equal(input.Duration, await probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts", args));
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData("00:30:07.56", 1807.56)]
    [InlineData("N/A", null)]
    public async Task DurationIgnoresErrorExitAndCachesUnknownResults(string value, double? expected)
    {
        int starts = 0;
        await using var probe = TestProcesses.CreateInputProbe(configureStartInfo: info =>
        {
            ++starts;
            info.ArgumentList[info.ArgumentList.IndexOf("--frames") + 1] = "0";
        });
        string[] args = ["--fake-duration", value, "--fake-duration-at-exit", "--fake-rc", "23"];
        for (int i = 0; i < 2; ++i)
        {
            var result = await probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts", args);
            Assert.Equal(expected.HasValue ? TimeSpan.FromSeconds(expected.Value) : null, result);
        }
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task DurationCacheIncludesEncoderInputAndArgumentValues()
    {
        int starts = 0;
        await using var probe = TestProcesses.CreateInputProbe(configureStartInfo: _ => ++starts);
        string encoder = BackendTests.FakeEncoderPath();
        var args = new List<string> { "--fake-duration", "00:00:10.00", "--fake-duration-at-exit" };
        Assert.Equal(TimeSpan.FromSeconds(10), await probe.ProbeDurationAsync(encoder, "a.ts", args));
        args[1] = "00:00:20.00";
        Assert.Equal(TimeSpan.FromSeconds(10), await probe.ProbeDurationAsync(encoder, "a.ts",
            new[] { "--fake-duration", "00:00:10.00", "--fake-duration-at-exit" }));
        Assert.Equal(TimeSpan.FromSeconds(20), await probe.ProbeDurationAsync(encoder, "a.ts", args));
        await probe.ProbeDurationAsync(encoder, "b.ts", args);
        string alternate = Path.GetDirectoryName(encoder) + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar + Path.GetFileName(encoder);
        await probe.ProbeDurationAsync(alternate, "b.ts", args);
        Assert.Equal(4, starts);
    }

    [Fact]
    public async Task MissingDurationAndNonVideoStdoutAreDrainedAndCached()
    {
        int starts = 0;
        await using var probe = TestProcesses.CreateInputProbe(configureStartInfo: _ => ++starts);
        for (int i = 0; i < 2; ++i)
            Assert.Null(await probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "a.ts", new[] { "--version" }));
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task DurationDrainsLargeStdoutAndStderrWithoutDeadlock()
    {
        await using var probe = TestProcesses.CreateInputProbe(configureStartInfo: info =>
            info.ArgumentList[info.ArgumentList.IndexOf("--frames") + 1] = "1000");
        Assert.Equal(TimeSpan.FromSeconds(10), await probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts",
            new[] { "--fake-duration", "00:00:10.00", "--fake-duration-at-exit", "--fake-stderr-bytes", "131072" }));
    }

    [Fact]
    public async Task WaitingForExitDurationDoesNotBlockFormatProbe()
    {
        string name = "duration-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var probe = TestProcesses.CreateInputProbe();
        string[] durationArgs = ["--fake-duration", "00:00:10.00", "--fake-duration-at-exit", "--fake-duration-exit-pipe", name];
        var duration = probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts", durationArgs);
        await PipeWait.UntilConnectedAsync(pipe, name, duration);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        Assert.Equal("ready", await reader.ReadLineAsync());
        var input = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts");
        await writer.WriteLineAsync("release");
        Assert.Equal(TimeSpan.FromSeconds(10), await duration);
        Assert.Equal(64, input.Width);
        Assert.Null(input.Duration);
    }

    [Fact]
    public async Task CancelledDurationReapsProcessAndDoesNotCacheFailure()
    {
        string pipeName = "duration-pid-" + Guid.NewGuid().ToString("N");
        int starts = 0;
        await using var probe = TestProcesses.CreateInputProbe(configureStartInfo: info =>
        {
            if (Interlocked.Increment(ref starts) != 1) return;
            info.Environment["FAKE_ENCODER_PID_PIPE"] = pipeName;
            info.ArgumentList.Add("--fake-hang-after");
            info.ArgumentList.Add("0");
        });
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource();
        string[] args = ["--fake-duration", "00:00:10.00", "--fake-duration-at-exit"];
        Task<TimeSpan?> run = probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts", args, cancellation.Token);
        await PipeWait.UntilConnectedAsync(pipe, pipeName, run);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        string line = await reader.ReadLineAsync() ?? throw new EndOfStreamException();
        int pid = int.Parse(line, System.Globalization.CultureInfo.InvariantCulture);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        AssertStopped(pid);
        Assert.Equal(TimeSpan.FromSeconds(10), await probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts", args));
    }

    [Fact]
    public async Task DisposeCancelsActiveDurationAndRejectsNewRequests()
    {
        string pipeName = "probe-pid-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var probe = TestProcesses.CreateInputProbe(configureStartInfo: info => info.Environment["FAKE_ENCODER_PID_PIPE"] = pipeName);
        using var waitingCancellation = new CancellationTokenSource();
        try
        {
            var active = probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts", new[] { "--fake-hang-after", "0" });
            int pid = await ReadPidFromPipeAsync(pipe, pipeName, active);
            var waiting = probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "other.ts", cancellationToken: waitingCancellation.Token);
            waitingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            await probe.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            AssertStopped(pid);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts"));
        }
        finally { await probe.DisposeAsync(); }
    }

    [Fact]
    public async Task CancelledDurationWaiterDoesNotPreventActiveQueryFromCompleting()
    {
        string name = "duration-waiter-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var probe = TestProcesses.CreateInputProbe();
        using var waitingCancellation = new CancellationTokenSource();
        var active = probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "input.ts",
            new[] { "--fake-duration", "00:00:10.00", "--fake-duration-at-exit", "--fake-duration-exit-pipe", name });
        await PipeWait.UntilConnectedAsync(pipe, name, active);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        Assert.Equal("ready", await reader.ReadLineAsync());
        var waiting = probe.ProbeDurationAsync(BackendTests.FakeEncoderPath(), "other.ts", cancellationToken: waitingCancellation.Token);
        waitingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await writer.WriteLineAsync("release");
        Assert.Equal(TimeSpan.FromSeconds(10), await active);
    }

    [Fact]
    public async Task DurationTimeoutRunsCleanupBeforeReturningDiagnosticException()
    {
        var clock = new FakeTimeProvider();
        var timers = clock.ObserveTimers();
        var timeout = TimeSpan.FromSeconds(2);
        var first = new FakeDurationProcess(id: 1);
        var next = new FakeDurationProcess(id: 2);
        var processes = new Queue<FakeDurationProcess>([first, next]);
        await using var probe = TestProcesses.CreateDurationProbe(PreviewTimeouts.Infinite with { Input = timeout }, clock,
            new TimeoutProbeBackend(), (_, _) => processes.Dequeue());

        try
        {
            const string diagnostic = "Duration: 00:00:10.00, start: 0.0";
            Task<TimeSpan?> run = probe.ProbeDurationAsync("fake", "in.ts");
            Assert.Equal(timeout, await timers.ReadAsync());
            clock.Advance(timeout);
            await first.KillRequested.Task;
            first.Complete(diagnostic);

            var error = await Assert.ThrowsAsync<InputProbeTimeoutException>(() => run);
            Assert.IsType<TimeoutException>(error.InnerException);
            Assert.Equal(diagnostic, error.StandardError);
            Assert.True(first.Disposed);

            next.Complete("Duration: 00:00:20.00, start: 0.0");
            Assert.Equal(TimeSpan.FromSeconds(20), await probe.ProbeDurationAsync("fake", "in.ts"));
            Assert.True(next.Disposed);
            Assert.Empty(processes);
        }
        finally
        {
            first.Complete("");
            next.Complete("");
        }
    }

    private sealed class FakeDurationProcess(int id) : InputProbe.IDurationProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stdoutEof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _stderrEof = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => id;
        public bool Disposed { get; private set; }
        public TaskCompletionSource KillRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForExitAsync() => _exit.Task;
        public Task DrainStandardOutputAsync() => _stdoutEof.Task;
        public Task<string> ReadStandardErrorAsync() => _stderrEof.Task;
        public void Kill() => KillRequested.TrySetResult();
        public void Dispose()
        {
            Assert.True(_exit.Task.IsCompletedSuccessfully);
            Assert.True(_stdoutEof.Task.IsCompletedSuccessfully);
            Assert.True(_stderrEof.Task.IsCompletedSuccessfully);
            Disposed = true;
        }

        public void Complete(string stderr)
        {
            _exit.TrySetResult();
            _stdoutEof.TrySetResult();
            _stderrEof.TrySetResult(stderr);
        }
    }

    private sealed class TimeoutProbeBackend : IPreviewBackend
    {
        private int _requests;

        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                var completion = new TaskCompletionSource<PreviewRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => completion.TrySetResult(new PreviewRunResult(-12, "fake diagnostics", true)));
                return completion.Task;
            }
            var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
            sink.OnHeader(header);
            sink.OnFrame(new Y4mFrame(header, null, null, new byte[6]));
            return Task.FromResult(new PreviewRunResult(0, ""));
        }

        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<int> ReadPidFromPipeAsync(NamedPipeServerStream pipe, string name, Task operation)
    {
        await PipeWait.UntilConnectedAsync(pipe, name, operation);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        string line = await reader.ReadLineAsync() ?? throw new EndOfStreamException();
        return int.Parse(line, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertStopped(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, $"検査プロセス{pid}が残っています");
        }
        catch (ArgumentException) { /* 回収済み。 */ }
    }
}

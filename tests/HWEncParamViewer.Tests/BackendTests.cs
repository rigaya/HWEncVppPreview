using HWEncParamViewer.Testing;
using System.Diagnostics;
using System.Threading.Channels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.Tests;

public sealed class BackendTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task OneShotReadsFramesAndDrainsLargeStderr()
    {
        await using var backend = new OneShotBackend();
        var sink = new RecordingSink();
        var result = await backend.RunAsync(Request("--fake-stderr-bytes", "131072"), sink).WaitAsync(SafetyTimeout);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.IsCancelled);
        Assert.Equal(4, sink.Frames.Count);
        Assert.True(sink.OutputCount > 0);
        Assert.True(result.StandardError.Length > 131072);
        Assert.Equal(0, sink.Frames[0].Xts);
        Assert.Equal(64 * 36 * 3 / 2, sink.Frames[0].Data.Length);
        AssertStopped(sink.ProcessId);
    }

    [Fact]
    public async Task OneShotReturnsExitCodeAndDiagnostics()
    {
        await using var backend = new OneShotBackend();
        var result = await backend.RunAsync(Request("--fake-rc", "23"), new RecordingSink()).WaitAsync(SafetyTimeout);
        Assert.Equal(23, result.ExitCode);
        Assert.Contains("FAKE requested rc=23", result.StandardError);
    }

    [Fact]
    public async Task OneShotCancellationKillsAndReapsProcess()
    {
        await using var backend = new OneShotBackend();
        using var cancellation = new CancellationTokenSource();
        var sink = new RecordingSink();
        var running = backend.RunAsync(Request("--fake-hang-after", "1"), sink, cancellation.Token);
        await sink.FirstFrame.Task.WaitAsync(SafetyTimeout);
        cancellation.Cancel();
        var result = await running.WaitAsync(SafetyTimeout);
        Assert.True(result.IsCancelled);
        AssertStopped(sink.ProcessId);
    }

    [Fact]
    public async Task ServerReusesProcessAndKeepsRequestsIndependent()
    {
        await using var backend = new ServerBackend();
        var a1 = new RecordingSink();
        var b = new RecordingSink();
        var a2 = new RecordingSink();
        var requestA = Request() with { VppArgs = new[] { "--vpp-nlmeans", "sigma=0.1" } };
        var requestB = Request() with { VppArgs = new[] { "--vpp-nlmeans", "sigma=0.2" } };
        Assert.Equal(0, (await backend.RunAsync(requestA, a1).WaitAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(0, (await backend.RunAsync(requestB, b).WaitAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(0, (await backend.RunAsync(requestA, a2).WaitAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(a1.ProcessId, b.ProcessId);
        Assert.Equal(a1.ProcessId, a2.ProcessId);
        Assert.Equal(a1.Frames[0].Data, a2.Frames[0].Data);
        Assert.False(a1.Frames[0].Data.SequenceEqual(b.Frames[0].Data));
        await backend.DisposeAsync();
        AssertStopped(a1.ProcessId);
    }

    [Fact]
    public async Task BothBackendsProduceIdenticalFramesAndRelativeTimestamps()
    {
        await using var oneShot = new OneShotBackend();
        await using var server = new ServerBackend();
        var one = new RecordingSink();
        var resident = new RecordingSink();
        var request = Request() with
        {
            TargetSeconds = 10, PreMarginSeconds = 0.1,
            VppArgs = new[] { "--vpp-nlmeans", "sigma=0.123" },
        };
        await oneShot.RunAsync(request, one).WaitAsync(SafetyTimeout);
        await server.RunAsync(request, resident).WaitAsync(SafetyTimeout);
        Assert.Equal(one.Frames.Count, resident.Frames.Count);
        Assert.Equal(0, resident.Frames[0].Xts);
        Assert.Equal("9.9", resident.Frames[0].Header.ExtTags["XSEEK"]);
        for (int i = 0; i < one.Frames.Count; i++)
        {
            Assert.Equal(one.Frames[i].Data, resident.Frames[i].Data);
            Assert.Equal(one.Frames[i].Xts, resident.Frames[i].Xts);
        }
    }

    [Fact]
    public async Task ServerCancelRecoversWithoutRestart()
    {
        await using var backend = new ServerBackend();
        using var cancellation = new CancellationTokenSource();
        var sink = new RecordingSink();
        var run = backend.RunAsync(Request("--fake-hang-after", "1"), sink, cancellation.Token);
        await sink.FirstFrame.Task.WaitAsync(SafetyTimeout);
        cancellation.Cancel();
        var result = await run.WaitAsync(SafetyTimeout);
        Assert.True(result.IsCancelled);
        Assert.Equal(-12, result.ExitCode);
        var recovery = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), recovery).WaitAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(sink.ProcessId, recovery.ProcessId);
    }

    [Fact]
    public async Task NewServerRunCancelsPreviousRun()
    {
        await using var backend = new ServerBackend();
        var first = new RecordingSink();
        var firstRun = backend.RunAsync(Request("--fake-hang-after", "1"), first);
        await first.FirstFrame.Task.WaitAsync(SafetyTimeout);
        var second = new RecordingSink();
        var secondRun = backend.RunAsync(Request(), second);
        Assert.True((await firstRun.WaitAsync(SafetyTimeout)).IsCancelled);
        Assert.Equal(0, (await secondRun.WaitAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(first.ProcessId, second.ProcessId);
    }

    [Fact]
    public async Task IgnoredCancellationKillsThenRestartsServer()
    {
        var clock = new FakeTimeProvider();
        var observedClock = new TimerSignalClock(clock, TimeSpan.FromSeconds(2));
        await using var backend = new ServerBackend(timeProvider: observedClock, cancelTimeout: TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var sink = new RecordingSink();
        var run = backend.RunAsync(Request("--fake-hang-after", "1", "--fake-ignore-cancel"), sink, cancellation.Token);
        await sink.FirstFrame.Task.WaitAsync(SafetyTimeout);
        cancellation.Cancel();
        await observedClock.TimerArmed.Task.WaitAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromSeconds(2));
        await run.WaitAsync(SafetyTimeout);
        Assert.True((await run).IsCancelled);
        AssertStopped(sink.ProcessId);
        var next = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), next).WaitAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(sink.ProcessId, next.ProcessId);
    }

    [Fact]
    public async Task MissingReadyUsesInjectedClockAndReapsProcess()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"viewer-ready-{Guid.NewGuid():N}.txt");
        var clock = new FakeTimeProvider();
        var observedClock = new TimerSignalClock(clock, TimeSpan.FromSeconds(3));
        await using var backend = new ServerBackend(timeProvider: observedClock, configureStartInfo: info =>
        {
            info.Environment["FAKE_ENCODER_READY_NONE"] = "1";
            info.Environment["FAKE_ENCODER_PID_FILE"] = pidFile;
        });
        try
        {
            var run = backend.RunAsync(Request(), new RecordingSink());
            await observedClock.TimerArmed.Task.WaitAsync(SafetyTimeout);
            clock.Advance(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<PreviewServerNotSupportedException>(() => run.WaitAsync(SafetyTimeout));
            if (FakeLogFiles.Exists(pidFile))
            {
                foreach (string pid in FakeLogFiles.ReadAllLines(pidFile)) AssertStopped(int.Parse(pid));
            }
        }
        finally
        {
            FakeLogFiles.Delete(pidFile);
        }
    }

    [Fact]
    public async Task TruncatedFrameFailsAndNextRunRestarts()
    {
        await using var backend = new ServerBackend();
        var broken = new RecordingSink();
        var exception = await Assert.ThrowsAsync<PreviewBackendException>(() =>
            backend.RunAsync(Request("--fake-crash-at", "1"), broken).WaitAsync(SafetyTimeout));
        Assert.IsType<Y4mTruncatedException>(exception.InnerException);
        Assert.Contains("RGYPREVIEW BEGIN id=", exception.StandardError);
        Assert.Contains($"FAKEPID {broken.ProcessId}", exception.StandardError);
        Assert.Single(broken.Frames);
        AssertStopped(broken.ProcessId);
        var recovery = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), recovery).WaitAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(broken.ProcessId, recovery.ProcessId);
    }

    [Fact]
    public async Task RequestLimitRestartsServer()
    {
        await using var backend = new ServerBackend(restartPolicy: new ServerRestartPolicy(2));
        var first = new RecordingSink();
        var second = new RecordingSink();
        var third = new RecordingSink();
        await backend.RunAsync(Request(), first).WaitAsync(SafetyTimeout);
        await backend.RunAsync(Request(), second).WaitAsync(SafetyTimeout);
        await backend.RunAsync(Request(), third).WaitAsync(SafetyTimeout);
        Assert.Equal(first.ProcessId, second.ProcessId);
        Assert.NotEqual(first.ProcessId, third.ProcessId);
        AssertStopped(first.ProcessId);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-12, false)]
    [InlineData(-24, false)]
    [InlineData(-29, true)]
    [InlineData(17, true)]
    public async Task ExitCodeDeterminesServerRestart(int rc, bool restarts)
    {
        await using var backend = new ServerBackend();
        var first = new RecordingSink();
        var result = await backend.RunAsync(Request("--fake-rc", rc.ToString(System.Globalization.CultureInfo.InvariantCulture)), first).WaitAsync(SafetyTimeout);
        Assert.Equal(rc, result.ExitCode);
        if (rc != 0) Assert.Contains($"FAKE requested rc={rc}", result.StandardError);
        var next = new RecordingSink();
        await backend.RunAsync(Request(), next).WaitAsync(SafetyTimeout);
        Assert.Equal(restarts, first.ProcessId != next.ProcessId);
    }

    [Fact]
    public void RestartPolicyIncludesFaultsAndRequestLimit()
    {
        var policy = new ServerRestartPolicy(2);
        Assert.False(policy.ShouldRestart(1, 0));
        Assert.True(policy.ShouldRestart(2, 0));
        Assert.True(policy.ShouldRestart(0, 0, protocolFailure: true));
        Assert.True(policy.ShouldRestart(0, 0, processExited: true));
        Assert.True(policy.ShouldRestart(0, 0, watchdog: true));
    }

    [Fact]
    public async Task DroppedTimestampsArePreserved()
    {
        await using var backend = new OneShotBackend();
        var sink = new RecordingSink();
        var result = await backend.RunAsync(Request("--fake-step", "2"), sink).WaitAsync(SafetyTimeout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, sink.Frames.Count);
        Assert.Equal(2 / 30.0, sink.Frames[1].Xts!.Value, 7);
    }

    [Fact]
    public async Task ProtocolViolationKillsServerAndNextRequestRecovers()
    {
        await using var backend = new ServerBackend();
        var broken = new RecordingSink();
        var exception = await Assert.ThrowsAsync<PreviewBackendException>(() =>
            backend.RunAsync(Request("--fake-bad-control-at", "1"), broken).WaitAsync(SafetyTimeout));
        Assert.IsType<PreviewProtocolException>(exception.InnerException);
        Assert.Contains("RGYPREVIEW BEGIN id=", exception.StandardError);
        Assert.Contains($"FAKEPID {broken.ProcessId}", exception.StandardError);
        Assert.Single(broken.Frames);
        AssertStopped(broken.ProcessId);
        var recovery = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), recovery).WaitAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(broken.ProcessId, recovery.ProcessId);
    }

    [Fact]
    public async Task ServerThatExitsAfterRequestCanBeStartedAgain()
    {
        await using var backend = new ServerBackend(configureStartInfo: info => info.Environment["FAKE_ENCODER_DROP_AFTER"] = "1");
        var first = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), first).WaitAsync(SafetyTimeout)).ExitCode);
        await WaitUntilAsync(() => IsStopped(first.ProcessId));
        var next = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), next).WaitAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(first.ProcessId, next.ProcessId);
    }

    [Fact]
    public async Task ControllerWatchdogKillsHungProcessAndRestartsServer()
    {
        var clock = new FakeTimeProvider();
        await using var observed = new ObservedBackend(new ServerBackend(timeProvider: clock));
        await using var controller = new PreviewController(backend: observed, timeProvider: clock, debounce: TimeSpan.Zero);
        var failure = new TaskCompletionSource<PreviewFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<PreviewCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayed = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Failed += value => failure.TrySetResult(value);
        controller.Completed += value => completion.TrySetResult(value);
        controller.FrameReady += value => displayed.TrySetResult(value);
        controller.Submit(Request("--fake-hang-after", "1", "--fake-ignore-cancel"));
        var first = await observed.FirstFrame.Task.WaitAsync(SafetyTimeout);
        int firstPid = int.Parse(first.Header.ExtTags["XPID"]);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Contains("タイムアウト", (await failure.Task.WaitAsync(SafetyTimeout)).Reason);
        await controller.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        AssertStopped(firstPid);
        controller.Submit(Request());
        Assert.Equal(0, (await completion.Task.WaitAsync(SafetyTimeout)).Result.ExitCode);
        var frame = await displayed.Task.WaitAsync(SafetyTimeout);
        Assert.NotEqual(firstPid, int.Parse(frame.Frame.Header.ExtTags["XPID"]));
        await controller.WaitForIdleAsync().WaitAsync(SafetyTimeout);
    }

    [Fact]
    public async Task ControllerFallsBackOnceWhenReadyIsMissing()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"viewer-fallback-{Guid.NewGuid():N}.txt");
        var clock = new FakeTimeProvider();
        await using var preferred = new ServerBackend(timeProvider: clock, configureStartInfo: info =>
        {
            info.Environment["FAKE_ENCODER_READY_NONE"] = "1";
            info.Environment["FAKE_ENCODER_PID_FILE"] = pidFile;
        });
        await using var fallback = new OneShotBackend(info => info.Environment["FAKE_ENCODER_PID_FILE"] = pidFile);
        await using var controller = new PreviewController(backend: preferred, fallback: fallback, timeProvider: clock, debounce: TimeSpan.Zero);
        var completed = Channel.CreateUnbounded<PreviewCompletion>();
        controller.Completed += result => completed.Writer.TryWrite(result);
        try
        {
            controller.Submit(Request());
            await WaitUntilAsync(() => FakeLogFiles.Exists(pidFile) && FakeLogFiles.ReadAllLines(pidFile).Length > 0);
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.Equal(0, (await completed.Reader.ReadAsync().AsTask().WaitAsync(SafetyTimeout)).Result.ExitCode);
            await controller.WaitForIdleAsync().WaitAsync(SafetyTimeout);
            Assert.Equal(2, FakeLogFiles.ReadAllLines(pidFile).Length);
            controller.Submit(Request() with { VppArgs = new[] { "--vpp-nlmeans", "sigma=0.2" } });
            Assert.Equal(0, (await completed.Reader.ReadAsync().AsTask().WaitAsync(SafetyTimeout)).Result.ExitCode);
            await controller.WaitForIdleAsync().WaitAsync(SafetyTimeout);
            Assert.Equal(3, FakeLogFiles.ReadAllLines(pidFile).Length);
            foreach (string pid in FakeLogFiles.ReadAllLines(pidFile)) AssertStopped(int.Parse(pid));
        }
        finally
        {
            FakeLogFiles.Delete(pidFile);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        async Task WaitAsync()
        {
            while (!condition()) await Task.Yield();
        }
        await WaitAsync().WaitAsync(SafetyTimeout);
    }

    private static bool IsStopped(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    private sealed class ObservedBackend(IPreviewBackend inner) : IPreviewBackend
    {
        public TaskCompletionSource<Y4mFrame> FirstFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default) =>
            inner.RunAsync(request, new ObservedSink(sink, frame => FirstFrame.TrySetResult(frame)), cancellationToken);
        public ValueTask AbortAsync() => inner.AbortAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        private sealed class ObservedSink(IPreviewSink innerSink, Action<Y4mFrame> onFrame) : IPreviewSink
        {
            public void OnHeader(Y4mHeader header) => innerSink.OnHeader(header);
            public void OnOutput() => innerSink.OnOutput();
            public void OnFrame(Y4mFrame frame) { innerSink.OnFrame(frame); onFrame(frame); }
        }
    }

    private static PreviewRequest Request(params string[] extra) => new()
    {
        EncoderPath = FakeEncoderPath(), InputFile = "fake input.ts", Fps = 30,
        TargetSeconds = 0, PreMarginSeconds = 0, PostMarginSeconds = 0.1,
        InputArgs = new[] { "--avhw" }.Concat(extra).ToArray(),
    };

    internal static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("FakeEncoderのビルド出力が見つかりません");
        string path = Path.Combine(directory.FullName, "tests", "FakeEncoder", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
        Assert.True(File.Exists(path), $"偽エンコーダが未ビルドです: {path}");
        return path;
    }

    private sealed class TimerSignalClock(FakeTimeProvider inner, TimeSpan timeout) : TimeProvider
    {
        public TaskCompletionSource TimerArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp() => inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            // 応答待ちの期限だけを進め、続くOSのkill/pipe回収には偽時計を進めない。
            if (dueTime == timeout) TimerArmed.TrySetResult();
            return timer;
        }
    }

    private static void AssertStopped(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, $"子プロセス{pid}が残っています");
        }
        catch (ArgumentException)
        {
            // 回収済みならプロセス一覧にも残らない。
        }
    }

    private sealed class RecordingSink : IPreviewSink
    {
        public List<Y4mFrame> Frames { get; } = new();
        public TaskCompletionSource FirstFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId { get; private set; }
        public int OutputCount { get; private set; }
        public void OnHeader(Y4mHeader header) => ProcessId = int.Parse(header.ExtTags["XPID"]);
        public void OnFrame(Y4mFrame frame)
        {
            Frames.Add(frame);
            FirstFrame.TrySetResult();
        }
        public void OnOutput() => OutputCount++;
    }
}

using HWEncParamViewer.Testing;
using System.Diagnostics;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.Tests;

public sealed class StreamingBackendTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingStopsAtDemandGateAndResumesWithoutLosingFrames(bool resident)
    {
        await using IPreviewBackend backend = CreateBackend(resident);
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request(), sink);
        await sink.Paused.Task.WaitAsync(SafetyTimeout);

        Assert.Equal(1, sink.FrameCount);
        Assert.False(run.IsCompleted);
        Assert.False(sink.IsReading);

        sink.Resume();
        PreviewRunResult result = await run.WaitAsync(SafetyTimeout);
        await sink.Resumed.Task.WaitAsync(SafetyTimeout);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.IsCancelled);
        Assert.Equal(4, sink.FrameCount);
        for (int i = 0; i < sink.FrameCount; ++i) Assert.Equal(i / 30d, sink.Timestamps[i], 7);
        if (!resident) AssertStopped(sink.ProcessId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationUnblocksDemandGateEvenWhenSinkIgnoresToken(bool resident)
    {
        await using IPreviewBackend backend = CreateBackend(resident);
        using var cancellation = new CancellationTokenSource();
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request("--fake-hang-after", "1"), sink, cancellation.Token);
        await sink.Paused.Task.WaitAsync(SafetyTimeout);
        int gatedCalls = sink.BeforeReadCalls;

        cancellation.Cancel();
        PreviewRunResult result = await run.WaitAsync(SafetyTimeout);
        Assert.True(result.IsCancelled);
        Assert.Equal(1, sink.FrameCount);
        Assert.Equal(gatedCalls, sink.BeforeReadCalls);
        if (resident)
        {
            // 止まっていた読取りを再開してENDを排出できれば、同じプロセスを再利用できる。
            Assert.Equal(-12, result.ExitCode);
            var recovery = new OpenSink();
            Assert.Equal(0, (await backend.RunAsync(Request(), recovery).WaitAsync(SafetyTimeout)).ExitCode);
            Assert.Equal(sink.ProcessId, recovery.ProcessId);
            Assert.Equal(4, recovery.FrameCount);
        }
        else AssertStopped(sink.ProcessId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AbortAndDisposeReleasePausedReadTask(bool resident, bool dispose)
    {
        await using IPreviewBackend backend = CreateBackend(resident);
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request("--fake-hang-after", "1"), sink);
        await sink.Paused.Task.WaitAsync(SafetyTimeout);

        Task stopping = dispose ? backend.DisposeAsync().AsTask() : backend.AbortAsync().AsTask();
        await Task.WhenAll(run, stopping).WaitAsync(SafetyTimeout);
        Assert.True((await run).IsCancelled);
        AssertStopped(sink.ProcessId);
    }

    [Fact]
    public async Task ServerThatIgnoresCancelWhilePausedUsesFakeClockAndRecovers()
    {
        var clock = new FakeTimeProvider();
        var signalClock = new TimerSignalClock(clock, TimeSpan.FromSeconds(2));
        await using var backend = new ServerBackend(timeProvider: signalClock, cancelTimeout: TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request("--fake-hang-after", "1", "--fake-ignore-cancel"), sink, cancellation.Token);
        await sink.Paused.Task.WaitAsync(SafetyTimeout);
        cancellation.Cancel();

        // cancel応答の期限だけを進める。OSのkill/exit処理中に時計を繰り返し進めない。
        await signalClock.TimerArmed.Task.WaitAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await run.WaitAsync(SafetyTimeout)).IsCancelled);
        AssertStopped(sink.ProcessId);
        var recovery = new OpenSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), recovery).WaitAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(sink.ProcessId, recovery.ProcessId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuousOutputCanPauseResumeThenCancel(bool resident)
    {
        await using IPreviewBackend backend = CreateBackend(resident);
        using var cancellation = new CancellationTokenSource();
        var sink = new GatedSink(stopAfterFrames: 3, stop: cancellation.Cancel);
        var request = Request() with { Continuous = true };
        Task<PreviewRunResult> run = backend.RunAsync(request, sink, cancellation.Token);
        await sink.Paused.Task.WaitAsync(SafetyTimeout);
        Assert.Equal(1, sink.FrameCount);
        sink.Resume();

        PreviewRunResult result = await run.WaitAsync(SafetyTimeout);
        Assert.True(result.IsCancelled);
        Assert.Equal(3, sink.FrameCount);
        if (resident) Assert.Equal(-12, result.ExitCode);
        else AssertStopped(sink.ProcessId);
    }

    [Fact]
    public async Task IdleServerDisposalUsesQuitAndExitsNormally()
    {
        string commandLog = Path.GetTempFileName();
        var clock = new FakeTimeProvider();
        var backend = new ServerBackend(timeProvider: clock, configureStartInfo: info =>
            info.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = commandLog);
        try
        {
            var sink = new OpenSink();
            await backend.RunAsync(Request(), sink).WaitAsync(SafetyTimeout);
            using var process = Process.GetProcessById(sink.ProcessId);
            await backend.DisposeAsync().AsTask().WaitAsync(SafetyTimeout);
            await process.WaitForExitAsync().WaitAsync(SafetyTimeout);
            Assert.True(process.HasExited);
            Assert.Contains("\"cmd\":\"quit\"", string.Join("\n", FakeLogFiles.ReadAllLines(commandLog)), StringComparison.Ordinal);
            await backend.DisposeAsync();
        }
        finally
        {
            await backend.DisposeAsync();
            FakeLogFiles.Delete(commandLog);
        }
    }

    [Fact]
    public async Task CleanupDeadlineUsesInjectedClockWhenPipeNeverCompletes()
    {
        var clock = new FakeTimeProvider();
        var pipe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> joining = ProcessSupport.JoinAsync(pipe.Task, clock, TimeSpan.FromSeconds(5), "test.stderr");
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(joining.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await joining.WaitAsync(SafetyTimeout));
        Assert.False(pipe.Task.IsCompleted);
        // 期限切れ後の遅い失敗も、呼出し元を再び待たせることなく観測対象になる。
        pipe.SetException(new IOException("遅れて終了したpipe"));
    }

    [Fact]
    public async Task DisposeOfServerIgnoringCancelIsBoundedByFakeClock()
    {
        var clock = new FakeTimeProvider();
        var backend = new ServerBackend(timeProvider: clock);
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request("--fake-hang-after", "1", "--fake-ignore-cancel"), sink);
        await sink.Paused.Task.WaitAsync(SafetyTimeout);
        using var process = Process.GetProcessById(sink.ProcessId);
        Task stopping = backend.DisposeAsync().AsTask();
        async Task AdvanceAsync()
        {
            while (!stopping.IsCompleted || !run.IsCompleted)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Yield();
            }
        }
        await AdvanceAsync().WaitAsync(SafetyTimeout);
        await Assert.ThrowsAsync<TimeoutException>(() => stopping);
        Assert.True((await run).IsCancelled);
        await process.WaitForExitAsync().WaitAsync(SafetyTimeout);
    }

    [Fact]
    public async Task IgnoredQuitTimesOutThenKillsAndReportsIncomplete()
    {
        string directory = Path.Combine(Path.GetTempPath(), "viewer-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string log = Path.Combine(directory, "commands.jsonl");
        var clock = new FakeTimeProvider();
        var backend = new ServerBackend(timeProvider: clock, configureStartInfo: info =>
        {
            info.Environment["FAKE_ENCODER_IGNORE_QUIT"] = "1";
            info.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = log;
        });
        var sink = new OpenSink();
        try
        {
            await backend.RunAsync(Request(), sink).WaitAsync(SafetyTimeout);
            using var process = Process.GetProcessById(sink.ProcessId);
            Task stopping = backend.DisposeAsync().AsTask();
            await WaitUntilAsync(() => FakeLogFiles.Exists(log) && ReadSharedCommandLog(log).Contains("quit", StringComparison.Ordinal));
            Assert.False(stopping.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => stopping.WaitAsync(SafetyTimeout));
            await process.WaitForExitAsync().WaitAsync(SafetyTimeout);
        }
        finally
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await backend.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExitedServerWithInheritedPipeReportsIncompleteThenReleasesChild()
    {
        string directory = Path.Combine(Path.GetTempPath(), "viewer-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string ready = Path.Combine(directory, "ready");
        string release = Path.Combine(directory, "release");
        string exited = Path.Combine(directory, "exited");
        var clock = new FakeTimeProvider();
        var backend = new ServerBackend(timeProvider: clock, configureStartInfo: info =>
        {
            info.Environment["FAKE_ENCODER_HOLD_OUTPUT_READY_FILE"] = ready;
            info.Environment["FAKE_ENCODER_HOLD_OUTPUT_RELEASE_FILE"] = release;
            info.Environment["FAKE_ENCODER_HOLD_OUTPUT_EXIT_FILE"] = exited;
        });
        Process? child = null;
        try
        {
            var sink = new OpenSink();
            await backend.RunAsync(Request(), sink).WaitAsync(SafetyTimeout);
            using var process = Process.GetProcessById(sink.ProcessId);
            Task stopping = backend.DisposeAsync().AsTask();
            await WaitUntilAsync(() => File.Exists(ready));
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready)));
            await process.WaitForExitAsync().WaitAsync(SafetyTimeout);
            Assert.True(process.HasExited);
            Assert.False(child.HasExited);
            Assert.False(stopping.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => stopping.WaitAsync(SafetyTimeout));
            Assert.False(child.HasExited);
        }
        finally
        {
            // 親が終了した後も保持されるpipeは、このテスト所有のreleaseで必ず閉じる。
            await File.WriteAllTextAsync(release, "release");
            if (child == null && File.Exists(ready))
            {
                try { child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready))); }
                catch (ArgumentException) { }
            }
            if (child != null)
            {
                await child.WaitForExitAsync().WaitAsync(SafetyTimeout);
                child.Dispose();
            }
            clock.Advance(TimeSpan.FromSeconds(30));
            await backend.DisposeAsync();
            await WaitUntilAsync(() => File.Exists(exited));
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OneShotCleanupRemainsIncompleteUntilInheritedPipeIsReleased()
    {
        string directory = Path.Combine(Path.GetTempPath(), "viewer-oneshot-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string ready = Path.Combine(directory, "ready");
        string release = Path.Combine(directory, "release");
        string exited = Path.Combine(directory, "exited");
        var clock = new FakeTimeProvider();
        var signalClock = new TimerSignalClock(clock, TimeSpan.FromSeconds(5));
        var backend = new OneShotBackend(timeProvider: signalClock, configureStartInfo: info =>
        {
            info.Environment["FAKE_ENCODER_HOLD_OUTPUT_READY_FILE"] = ready;
            info.Environment["FAKE_ENCODER_HOLD_OUTPUT_RELEASE_FILE"] = release;
            info.Environment["FAKE_ENCODER_HOLD_OUTPUT_EXIT_FILE"] = exited;
        });
        using var cancellation = new CancellationTokenSource();
        Process? child = null;
        try
        {
            var sink = new OpenSink();
            Task<PreviewRunResult> run = backend.RunAsync(Request(), sink, cancellation.Token);
            await WaitUntilAsync(() => File.Exists(ready));
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready)));
            await WaitUntilAsync(() => sink.ProcessId != 0);
            await WaitUntilAsync(() =>
            {
                try { using var process = Process.GetProcessById(sink.ProcessId); return process.HasExited; }
                catch (ArgumentException) { return true; }
            });
            cancellation.Cancel();
            await signalClock.TimerArmed.Task.WaitAsync(SafetyTimeout);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.True((await run.WaitAsync(SafetyTimeout)).IsCancelled);
            Assert.False(child.HasExited);
            signalClock.Reset();
            Task incomplete = backend.AbortAsync().AsTask();
            if (!incomplete.IsCompleted)
            {
                await signalClock.TimerArmed.Task.WaitAsync(SafetyTimeout);
                clock.Advance(TimeSpan.FromSeconds(5));
            }
            await Assert.ThrowsAsync<TimeoutException>(() => incomplete.WaitAsync(SafetyTimeout));

            await File.WriteAllTextAsync(release, "release");
            await child.WaitForExitAsync().WaitAsync(SafetyTimeout);
            // 過去に期限切れでも、実際のexit/read/stderr回収後は正常に終了できる。
            await backend.AbortAsync().AsTask().WaitAsync(SafetyTimeout);
            await backend.DisposeAsync().AsTask().WaitAsync(SafetyTimeout);
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release");
            if (child == null && File.Exists(ready))
            {
                try { child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready))); }
                catch (ArgumentException) { }
            }
            if (child != null)
            {
                await child.WaitForExitAsync().WaitAsync(SafetyTimeout);
                child.Dispose();
            }
            await WaitUntilAsync(() => File.Exists(exited));
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string ReadSharedCommandLog(string path)
    {
        // PID別ログの完了行を集約し、コマンド受信を確認する。
        return string.Join("\n", FakeLogFiles.ReadAllLines(path));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        async Task PollAsync()
        {
            while (!condition()) await Task.Yield();
        }
        await PollAsync().WaitAsync(SafetyTimeout);
    }

    private static IPreviewBackend CreateBackend(bool resident) => resident ? new ServerBackend() : new OneShotBackend();

    private static PreviewRequest Request(params string[] extra) => new()
    {
        EncoderPath = BackendTests.FakeEncoderPath(), InputFile = "fake input.ts", Fps = 30,
        TargetSeconds = 0, PreMarginSeconds = 0, PostMarginSeconds = 0.1,
        InputArgs = new[] { "--avhw" }.Concat(extra).ToArray(),
    };

    private static void AssertStopped(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, $"子プロセス{pid}が残っています");
        }
        catch (ArgumentException) { }
    }

    private class OpenSink : IPreviewSink
    {
        protected readonly object Sync = new();
        protected readonly List<double> Times = [];
        public int ProcessId { get; private set; }
        public int FrameCount { get { lock (Sync) return Times.Count; } }
        public double[] Timestamps { get { lock (Sync) return Times.ToArray(); } }
        public void OnHeader(Y4mHeader header) => ProcessId = int.Parse(header.ExtTags["XPID"]);
        public virtual void OnFrame(Y4mFrame frame)
        {
            lock (Sync) Times.Add(frame.Xts ?? double.NaN);
        }
        public void OnOutput() { }
        public virtual ValueTask BeforeReadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public virtual void OnReadStateChanged(bool reading) { }
    }

    private sealed class TimerSignalClock(FakeTimeProvider inner, TimeSpan observedTimeout) : TimeProvider
    {
        public TaskCompletionSource TimerArmed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() => TimerArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp() => inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == observedTimeout) TimerArmed.TrySetResult();
            return timer;
        }
    }

    private sealed class GatedSink(int stopAfterFrames = int.MaxValue, Action? stop = null) : OpenSink
    {
        private readonly TaskCompletionSource _permission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _pause = true;
        private bool _reading;
        private int _beforeReadCalls;
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsReading { get { lock (Sync) return _reading; } }
        public int BeforeReadCalls { get { lock (Sync) return _beforeReadCalls; } }

        public override ValueTask BeforeReadAsync(CancellationToken cancellationToken)
        {
            lock (Sync)
            {
                _beforeReadCalls++;
                // あえてtokenを使わず、バックエンド側の中断保険も検証する。
                return _pause && Times.Count >= 1 ? new ValueTask(_permission.Task) : ValueTask.CompletedTask;
            }
        }

        public override void OnReadStateChanged(bool reading)
        {
            lock (Sync)
            {
                _reading = reading;
                if (!reading) Paused.TrySetResult();
                else if (Paused.Task.IsCompleted) Resumed.TrySetResult();
            }
        }

        public override void OnFrame(Y4mFrame frame)
        {
            base.OnFrame(frame);
            if (FrameCount == stopAfterFrames) stop?.Invoke();
        }

        public void Resume()
        {
            lock (Sync) _pause = false;
            _permission.TrySetResult();
        }
    }

}

using HWEncVppPreview.Testing;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class StreamingBackendTests
{
    private static readonly TimeSpan SafetyTimeout = TestTimeouts.Hang;

    [Fact]
    public async Task ReadingStopsAtDemandGateAndResumesWithoutLosingFrames()
    {
        await using IPreviewBackend backend = new ServerBackend();
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request(), sink);
        await sink.Paused.Task.WaitWithDiagnosticsAsync(SafetyTimeout);

        Assert.Equal(1, sink.FrameCount);
        Assert.False(run.IsCompleted);
        Assert.False(sink.IsReading);

        sink.Resume();
        PreviewRunResult result = await run.WaitWithDiagnosticsAsync(SafetyTimeout);
        await sink.Resumed.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.IsCancelled);
        Assert.Equal(4, sink.FrameCount);
        for (int i = 0; i < sink.FrameCount; ++i) Assert.Equal(i / 30d, sink.Timestamps[i], 7);
    }

    [Fact]
    public async Task CancellationUnblocksDemandGateEvenWhenSinkIgnoresToken()
    {
        await using IPreviewBackend backend = new ServerBackend();
        using var cancellation = new CancellationTokenSource();
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request("--fake-hang-after", "1"), sink, cancellation.Token);
        await sink.Paused.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        int gatedCalls = sink.BeforeReadCalls;

        cancellation.Cancel();
        PreviewRunResult result = await run.WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True(result.IsCancelled);
        Assert.Equal(1, sink.FrameCount);
        Assert.Equal(gatedCalls, sink.BeforeReadCalls);
        // 止まっていた読取りを再開してENDを排出できれば、同じプロセスを再利用できる。
        Assert.Equal(-12, result.ExitCode);
        var recovery = new OpenSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), recovery).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(sink.ProcessId, recovery.ProcessId);
        Assert.Equal(4, recovery.FrameCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortAndDisposeReleasePausedReadTask(bool dispose)
    {
        await using IPreviewBackend backend = new ServerBackend();
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request("--fake-hang-after", "1"), sink);
        await sink.Paused.Task.WaitWithDiagnosticsAsync(SafetyTimeout);

        Task stopping = dispose ? backend.DisposeAsync().AsTask() : backend.AbortAsync().AsTask();
        await Task.WhenAll(run, stopping).WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True((await run).IsCancelled);
        AssertStopped(sink.ProcessId);
    }

    [Fact]
    public async Task CancellationTimeoutDropsTheSession()
    {
        await using var backend = new ServerBackend(cancelTimeout: TimeSpan.FromMilliseconds(500));
        using var cancellation = new CancellationTokenSource();
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request("--fake-hang-after", "1", "--fake-ignore-cancel"), sink, cancellation.Token);
        await sink.Paused.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        cancellation.Cancel();
        PreviewRunResult result = await run.WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True(result.IsCancelled);
        Assert.Contains("FAKEPID", result.StandardError);
        AssertStopped(sink.ProcessId);
        var next = new OpenSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), next).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(sink.ProcessId, next.ProcessId);
    }

    [Fact]
    public async Task DisposeReportsIncompleteWhenActiveRequestMissesDeadline()
    {
        var backend = new ServerBackend(cancelTimeout: TimeSpan.FromSeconds(5), stopTimeout: TimeSpan.FromSeconds(1));
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request("--fake-hang-after", "1", "--fake-ignore-cancel"), sink);
        await sink.Paused.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        using var child = Process.GetProcessById(sink.ProcessId);
        Task disposal = backend.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() => disposal.WaitWithDiagnosticsAsync(SafetyTimeout));
        Assert.True((await run.WaitWithDiagnosticsAsync(SafetyTimeout)).IsCancelled);
        await child.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task QuitTimeoutReapsTheProcess()
    {
        var backend = new ServerBackend(stopTimeout: TimeSpan.FromSeconds(2), configureStartInfo: info =>
            info.Environment["FAKE_ENCODER_IGNORE_QUIT"] = "1");
        var sink = new OpenSink();
        await backend.RunAsync(Request(), sink).WaitWithDiagnosticsAsync(SafetyTimeout);
        using var child = Process.GetProcessById(sink.ProcessId);
        await backend.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
        await child.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True(child.HasExited);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => backend.RunAsync(Request(), new OpenSink()).WaitWithDiagnosticsAsync(SafetyTimeout));
    }

    [Fact]
    public async Task ServerIgnoringCancelCanBeAbortedAndUsedAgain()
    {
        await using var backend = new ServerBackend();
        using var cancellation = new CancellationTokenSource();
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request("--fake-hang-after", "1", "--fake-ignore-cancel"), sink, cancellation.Token);
        await sink.Paused.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        cancellation.Cancel();
        await backend.AbortAsync().AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True((await run.WaitWithDiagnosticsAsync(SafetyTimeout)).IsCancelled);
        AssertStopped(sink.ProcessId);
        var recovery = new OpenSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), recovery).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(4, recovery.FrameCount);
    }

    [Fact]
    public async Task ContinuousOutputCanPauseResumeThenCancel()
    {
        await using IPreviewBackend backend = new ServerBackend();
        using var cancellation = new CancellationTokenSource();
        var sink = new GatedSink(stopAfterFrames: 3, stop: cancellation.Cancel);
        var request = Request() with { Continuous = true };
        Task<PreviewRunResult> run = backend.RunAsync(request, sink, cancellation.Token);
        await sink.Paused.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(1, sink.FrameCount);
        sink.Resume();

        PreviewRunResult result = await run.WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True(result.IsCancelled);
        Assert.Equal(3, sink.FrameCount);
        Assert.Equal(-12, result.ExitCode);
    }

    [Fact]
    public async Task IdleServerDisposalUsesQuitAndExitsNormally()
    {
        string commandLog = Path.GetTempFileName();
        var backend = new ServerBackend(configureStartInfo: info =>
            info.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = commandLog);
        try
        {
            var sink = new OpenSink();
            await backend.RunAsync(Request(), sink).WaitWithDiagnosticsAsync(SafetyTimeout);
            using var process = Process.GetProcessById(sink.ProcessId);
            await backend.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
            await process.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
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
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(await joining.WaitWithDiagnosticsAsync(SafetyTimeout));
        // 期限切れ後の遅い失敗も、呼出し元を再び待たせることなく観測対象になる。
        pipe.SetException(new IOException("遅れて終了したpipe"));
    }

    [Fact]
    public async Task AbortOfServerIgnoringCancelReapsTheProcess()
    {
        await using var backend = new ServerBackend();
        var sink = new GatedSink();
        Task<PreviewRunResult> run = backend.RunAsync(Request("--fake-hang-after", "1", "--fake-ignore-cancel"), sink);
        await sink.Paused.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        await backend.AbortAsync().AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True((await run.WaitWithDiagnosticsAsync(SafetyTimeout)).IsCancelled);
        AssertStopped(sink.ProcessId);
    }

    [Fact]
    public async Task AbortReapsServerConfiguredToIgnoreQuit()
    {
        await using var backend = new ServerBackend(configureStartInfo: info => info.Environment["FAKE_ENCODER_IGNORE_QUIT"] = "1");
        var sink = new OpenSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), sink).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        await backend.AbortAsync().AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
        AssertStopped(sink.ProcessId);
    }

    [Fact]
    public async Task ServerDisposeReapsInheritedPipeAfterChildIsReleased()
    {
        string pipeName = "server-child-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var backend = new ServerBackend(readyTimeout: SafetyTimeout, probeTimeout: SafetyTimeout,
            cancelTimeout: SafetyTimeout, stopTimeout: SafetyTimeout,
            configureStartInfo: info => info.Environment["FAKE_ENCODER_HOLD_OUTPUT_PIPE"] = pipeName);
        try
        {
            var sink = new OpenSink();
            Assert.Equal(0, (await backend.RunAsync(Request(), sink).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
            using var parent = Process.GetProcessById(sink.ProcessId);
            _ = parent.SafeHandle;
            Task stopping = backend.DisposeAsync().AsTask();
            await PipeWait.UntilConnectedAsync(pipe, pipeName, stopping, SafetyTimeout);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            string? pid = await reader.ReadLineAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.True(int.TryParse(pid, out int childPid) && childPid > 0);

            // 親が正常終了しても、子が継承パイプを閉じるまでDisposeは完了しない。
            await parent.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.Equal(0, parent.ExitCode);
            Assert.False(stopping.IsCompleted);

            await pipe.WriteAsync("release\n"u8.ToArray()).AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.Equal("0", await reader.ReadLineAsync().WaitWithDiagnosticsAsync(SafetyTimeout));
            Assert.Null(await reader.ReadLineAsync().WaitWithDiagnosticsAsync(SafetyTimeout));
            await stopping.WaitWithDiagnosticsAsync(SafetyTimeout);
        }
        finally
        {
            pipe.Dispose();
            await backend.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
        }
    }


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

using HWEncVppPreview.Testing;
using System.Diagnostics;
using System.Threading.Channels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class BackendTests
{
    private static readonly TimeSpan SafetyTimeout = TestTimeouts.Hang;

    [Fact]
    public async Task ServerReusesProcessAndKeepsRequestsIndependent()
    {
        await using var backend = new ServerBackend();
        var a1 = new RecordingSink();
        var b = new RecordingSink();
        var a2 = new RecordingSink();
        var requestA = Request() with { VppArgs = new[] { "--vpp-nlmeans", "sigma=0.1" } };
        var requestB = Request() with { VppArgs = new[] { "--vpp-nlmeans", "sigma=0.2" } };
        Assert.Equal(0, (await backend.RunAsync(requestA, a1).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(0, (await backend.RunAsync(requestB, b).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(0, (await backend.RunAsync(requestA, a2).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(a1.ProcessId, b.ProcessId);
        Assert.Equal(a1.ProcessId, a2.ProcessId);
        Assert.Equal(a1.Frames[0].Data, a2.Frames[0].Data);
        Assert.False(a1.Frames[0].Data.SequenceEqual(b.Frames[0].Data));
        await backend.DisposeAsync();
        AssertStopped(a1.ProcessId);
    }

    [Fact]
    public async Task ServerCancelRecoversWithoutRestart()
    {
        await using var backend = new ServerBackend();
        using var cancellation = new CancellationTokenSource();
        var sink = new RecordingSink();
        var run = backend.RunAsync(Request("--fake-hang-after", "1"), sink, cancellation.Token);
        await sink.FirstFrame.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        cancellation.Cancel();
        var result = await run.WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True(result.IsCancelled);
        Assert.Equal(-12, result.ExitCode);
        var recovery = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), recovery).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(sink.ProcessId, recovery.ProcessId);
    }

    [Fact]
    public async Task NewServerRunCancelsPreviousRun()
    {
        await using var backend = new ServerBackend();
        var first = new RecordingSink();
        var firstRun = backend.RunAsync(Request("--fake-hang-after", "1"), first);
        await first.FirstFrame.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        var second = new RecordingSink();
        var secondRun = backend.RunAsync(Request(), second);
        Assert.True((await firstRun.WaitWithDiagnosticsAsync(SafetyTimeout)).IsCancelled);
        Assert.Equal(0, (await secondRun.WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.Equal(first.ProcessId, second.ProcessId);
    }

    [Fact]
    public async Task IgnoredCancellationCanBeAbortedThenServerRestarts()
    {
        await using var backend = new ServerBackend();
        using var cancellation = new CancellationTokenSource();
        var sink = new RecordingSink();
        var run = backend.RunAsync(Request("--fake-hang-after", "1", "--fake-ignore-cancel"), sink, cancellation.Token);
        await sink.FirstFrame.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        cancellation.Cancel();
        await backend.AbortAsync().AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True((await run.WaitWithDiagnosticsAsync(SafetyTimeout)).IsCancelled);
        AssertStopped(sink.ProcessId);
        var next = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), next).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(sink.ProcessId, next.ProcessId);
    }

    [Fact]
    public async Task MissingReadyTimesOutAndDropsTheSession()
    {
        string name = "missing-ready-" + Guid.NewGuid().ToString("N");
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.In, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        int starts = 0;
        await using var backend = new ServerBackend(readyTimeout: TimeSpan.FromSeconds(2), configureStartInfo: info =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                info.Environment["FAKE_ENCODER_READY_NONE"] = "1";
                info.Environment["FAKE_ENCODER_PID_PIPE"] = name;
            }
        });
        Task<PreviewRunResult> first = backend.RunAsync(Request(), new RecordingSink());
        await PipeWait.UntilConnectedAsync(pipe, name, first, SafetyTimeout);
        using var reader = new StreamReader(pipe);
        int pid = int.Parse(await reader.ReadLineAsync().WaitWithDiagnosticsAsync(SafetyTimeout) ?? throw new EndOfStreamException());
        var error = await Assert.ThrowsAsync<PreviewServerNotSupportedException>(() => first.WaitWithDiagnosticsAsync(SafetyTimeout));
        Assert.IsType<TimeoutException>(error.InnerException);
        AssertStopped(pid);
        var next = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), next).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(pid, next.ProcessId);
    }

    [Fact]
    public async Task InvalidReadyReapsTheServer()
    {
        string name = "invalid-ready-" + Guid.NewGuid().ToString("N");
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.In, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        await using var backend = new ServerBackend(configureStartInfo: info =>
        {
            info.Environment["FAKE_ENCODER_READY_VERSION"] = "0";
            info.Environment["FAKE_ENCODER_PID_PIPE"] = name;
        });
        var run = backend.RunAsync(Request(), new RecordingSink());
        await PipeWait.UntilConnectedAsync(pipe, name, run, SafetyTimeout);
        using var reader = new StreamReader(pipe);
        int pid = int.Parse(await reader.ReadLineAsync().WaitWithDiagnosticsAsync(SafetyTimeout) ?? throw new EndOfStreamException());
        await Assert.ThrowsAsync<PreviewServerNotSupportedException>(() => run.WaitWithDiagnosticsAsync(SafetyTimeout));
        AssertStopped(pid);
    }

    [Fact]
    public async Task TruncatedFrameFailsAndNextRunRestarts()
    {
        await using var backend = new ServerBackend();
        var broken = new RecordingSink();
        var exception = await Assert.ThrowsAsync<PreviewBackendException>(() =>
            backend.RunAsync(Request("--fake-crash-at", "1"), broken).WaitWithDiagnosticsAsync(SafetyTimeout));
        Assert.IsType<Y4mTruncatedException>(exception.InnerException);
        Assert.Contains("RGYPREVIEW BEGIN id=", exception.StandardError);
        Assert.Contains($"FAKEPID {broken.ProcessId}", exception.StandardError);
        Assert.Single(broken.Frames);
        AssertStopped(broken.ProcessId);
        var recovery = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), recovery).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(broken.ProcessId, recovery.ProcessId);
    }

    [Fact]
    public async Task RequestLimitRestartsServer()
    {
        await using var backend = new ServerBackend(restartPolicy: new ServerRestartPolicy(2));
        var first = new RecordingSink();
        var second = new RecordingSink();
        var third = new RecordingSink();
        await backend.RunAsync(Request(), first).WaitWithDiagnosticsAsync(SafetyTimeout);
        await backend.RunAsync(Request(), second).WaitWithDiagnosticsAsync(SafetyTimeout);
        await backend.RunAsync(Request(), third).WaitWithDiagnosticsAsync(SafetyTimeout);
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
        var result = await backend.RunAsync(Request("--fake-rc", rc.ToString(System.Globalization.CultureInfo.InvariantCulture)), first).WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(rc, result.ExitCode);
        if (rc != 0) Assert.Contains($"FAKE requested rc={rc}", result.StandardError);
        var next = new RecordingSink();
        await backend.RunAsync(Request(), next).WaitWithDiagnosticsAsync(SafetyTimeout);
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
        await using var backend = new ServerBackend();
        var sink = new RecordingSink();
        var result = await backend.RunAsync(Request("--fake-step", "2"), sink).WaitWithDiagnosticsAsync(SafetyTimeout);
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
            backend.RunAsync(Request("--fake-bad-control-at", "1"), broken).WaitWithDiagnosticsAsync(SafetyTimeout));
        Assert.IsType<PreviewProtocolException>(exception.InnerException);
        Assert.Contains("RGYPREVIEW BEGIN id=", exception.StandardError);
        Assert.Contains($"FAKEPID {broken.ProcessId}", exception.StandardError);
        Assert.Single(broken.Frames);
        AssertStopped(broken.ProcessId);
        var recovery = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), recovery).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(broken.ProcessId, recovery.ProcessId);
    }

    [Fact]
    public async Task ServerThatExitsAfterRequestCanBeStartedAgain()
    {
        await using var backend = new ServerBackend(configureStartInfo: info => info.Environment["FAKE_ENCODER_DROP_AFTER"] = "1");
        var first = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), first).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        try
        {
            using var process = Process.GetProcessById(first.ProcessId);
            await process.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        }
        catch (ArgumentException) { }
        var next = new RecordingSink();
        Assert.Equal(0, (await backend.RunAsync(Request(), next).WaitWithDiagnosticsAsync(SafetyTimeout)).ExitCode);
        Assert.NotEqual(first.ProcessId, next.ProcessId);
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

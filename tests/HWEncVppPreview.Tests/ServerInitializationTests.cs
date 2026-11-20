using HWEncVppPreview.Testing;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class ServerInitializationTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;

    [Fact]
    public async Task ActualRequestCompletesAfterInitialization()
    {
        await using var gate = new InitializationGate();
        await using var backend = TestProcesses.CreateServerBackend(configureStartInfo: gate.Configure);
        Task initialization = backend.InitializeEncoderAsync(BackendTests.FakeEncoderPath());
        await gate.WaitReadyAsync(initialization);
        var sink = new Sink();
        Task<PreviewRunResult> actual = backend.RunAsync(Request(), sink);
        await gate.ReleaseAsync();
        await initialization.WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(0, (await actual.WaitWithDiagnosticsAsync(Safety)).ExitCode);
        Assert.Equal(2, sink.Frames);
    }

    [Fact]
    public async Task DisposeDuringInitializationReapsItsProcess()
    {
        await using var gate = new InitializationGate();
        var backend = TestProcesses.CreateServerBackend(configureStartInfo: gate.Configure);
        try
        {
            Task initialization = backend.InitializeEncoderAsync(BackendTests.FakeEncoderPath());
            int pid = await gate.WaitReadyAsync(initialization);
            await backend.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
            await initialization.WaitWithDiagnosticsAsync(Safety);
            AssertStopped(pid);
        }
        finally { await backend.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety); }
    }

    [Fact]
    public async Task EncoderChangeCancelsOldInitializationAndUsesNewSession()
    {
        await using var gate = new InitializationGate();
        int starts = 0;
        await using var backend = TestProcesses.CreateServerBackend(configureStartInfo: info =>
        {
            if (Interlocked.Increment(ref starts) == 1) gate.Configure(info);
        });
        string encoder = BackendTests.FakeEncoderPath();
        Task old = backend.InitializeEncoderAsync(encoder);
        int pid = await gate.WaitReadyAsync(old);
        string alternate = Path.GetDirectoryName(encoder) + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar + Path.GetFileName(encoder);
        await backend.InitializeEncoderAsync(alternate).WaitWithDiagnosticsAsync(Safety);
        await old.WaitWithDiagnosticsAsync(Safety);
        var sink = new Sink();
        Assert.Equal(0, (await backend.RunAsync(Request() with { EncoderPath = alternate }, sink).WaitWithDiagnosticsAsync(Safety)).ExitCode);
        Assert.Equal(2, sink.Frames);
        AssertStopped(pid);
    }

    [Fact]
    public async Task InitializationFailureDoesNotFailTheActualRequest()
    {
        await using var backend = TestProcesses.CreateServerBackend(configureStartInfo: info => info.Environment["FAKE_ENCODER_INITIALIZE_RC"] = "-24");
        await backend.InitializeEncoderAsync(BackendTests.FakeEncoderPath()).WaitWithDiagnosticsAsync(Safety);
        var sink = new Sink();
        Assert.Equal(0, (await backend.RunAsync(Request(), sink).WaitWithDiagnosticsAsync(Safety)).ExitCode);
        Assert.Equal(2, sink.Frames);
    }

    [Fact]
    public async Task ConcurrentWarmupCreatorsShareOneCompleteFixedFile()
    {
        var backends = Enumerable.Range(0, 8).Select(_ => TestProcesses.CreateServerBackend()).ToArray();
        try
        {
            await Task.WhenAll(backends.Select(backend => backend.InitializeEncoderAsync(BackendTests.FakeEncoderPath()))).WaitWithDiagnosticsAsync(Safety);
            foreach (var backend in backends)
            {
                var sink = new Sink();
                Assert.Equal(0, (await backend.RunAsync(Request(), sink).WaitWithDiagnosticsAsync(Safety)).ExitCode);
                Assert.Equal(2, sink.Frames);
            }
            string path = Path.Combine(Path.GetTempPath(), "HWEncVppPreview", "warmup-64x64.y4m");
            byte[] bytes = await File.ReadAllBytesAsync(path);
            byte[] header = Encoding.ASCII.GetBytes("YUV4MPEG2 W64 H64 F30:1 Ip A1:1 C420jpeg\nFRAME\n");
            Assert.Equal(header.Length + 64 * 64 * 3 / 2, bytes.Length);
            Assert.Equal(header, bytes[..header.Length]);
            Assert.All(bytes.Skip(header.Length).Take(64 * 64), value => Assert.Equal((byte)16, value));
            Assert.All(bytes.Skip(header.Length + 64 * 64), value => Assert.Equal((byte)128, value));
        }
        finally
        {
            foreach (var backend in backends) await backend.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        }
    }

    private static PreviewRequest Request() => new()
    {
        EncoderPath = BackendTests.FakeEncoderPath(), InputFile = "input.ts", Fps = 2,
        PreMarginSeconds = 0, PostMarginSeconds = 0.5,
    };

    private static void AssertStopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }

    private sealed class Sink : IPreviewSink
    {
        public int Frames;
        public void OnHeader(Y4mHeader header) { }
        public void OnFrame(Y4mFrame frame) => Frames++;
        public void OnOutput() { }
    }

    private sealed class InitializationGate : IAsyncDisposable
    {
        private readonly string _name = "initialization-" + Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream _pipe;
        private StreamWriter? _writer;
        private bool _released;

        public InitializationGate() => _pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        public void Configure(ProcessStartInfo info) => info.Environment["FAKE_ENCODER_INITIALIZE_PIPE"] = _name;

        public async Task<int> WaitReadyAsync(Task initialization)
        {
            await PipeWait.UntilConnectedAsync(_pipe, _name, initialization, Safety);
            using var reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            string pid = await reader.ReadLineAsync().WaitWithDiagnosticsAsync(Safety) ?? throw new EndOfStreamException();
            return int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task ReleaseAsync()
        {
            if (_writer == null || _released) return;
            await _writer.WriteLineAsync("release");
            _released = true;
        }

        public ValueTask DisposeAsync()
        {
            _writer?.Dispose();
            _pipe.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

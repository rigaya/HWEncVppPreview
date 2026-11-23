using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class ServerDeadlineTests
{
    [Fact]
    public async Task MissingReadyTimesOutAndDropsTheSession()
    {
        var clock = new FakeTimeProvider();
        var timers = clock.ObserveTimers();
        TimeSpan timeout = TimeSpan.FromSeconds(3);
        await using var fixture = new Sessions(clock, PreviewTimeouts.Infinite with { Ready = timeout }, withholdFirstReady: true);

        Task<PreviewRunResult> run = fixture.RunAsync();
        Peer stalled = await fixture.NextPeerAsync();
        Assert.Equal(timeout, await timers.ReadAsync());
        clock.Advance(timeout);

        var failure = await Assert.ThrowsAsync<PreviewServerNotSupportedException>(() => run);
        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.Equal(CoreText.ServerHandshakeUnsupported, failure.Message);
        Assert.True(stalled.WasReaped);
        Peer recovery = await fixture.CompleteRunAsync();
        Assert.NotSame(stalled, recovery);
    }

    [Fact]
    public async Task ExitBeforeEndPreservesTheErrorLineReadAfterExit()
    {
        await using var fixture = new Sessions(new FakeTimeProvider(), PreviewTimeouts.Infinite);
        Task<PreviewRunResult> run = fixture.RunAsync();
        Peer peer = await fixture.NextPeerAsync();
        Assert.Equal("run", (await peer.NextCommandAsync()).Name);
        peer.ExitBeforeStderrIsDrained();
        await peer.ReapStarted.Task;
        // 終了通知とstdoutのEOFが先に届き、stderrを読む側はその後で行とEOFを受け取る。
        peer.FinishStderr("\u001b[31mError: original failure\u001b[0m", "closing resources");

        var failure = await Assert.ThrowsAsync<PreviewBackendException>(() => run);
        Assert.Equal(CoreText.ServerRequestFailed + " (Error: original failure)", failure.Message);
        Assert.Contains("closing resources", failure.StandardError);
    }

    [Fact]
    public async Task EncoderWithoutReadyExposesTheStartupErrorAndTheCompatibilityHint()
    {
        await using var fixture = new Sessions(new FakeTimeProvider(), PreviewTimeouts.Infinite, withholdFirstReady: true);
        Task<PreviewRunResult> run = fixture.RunAsync();
        Peer peer = await fixture.NextPeerAsync();
        peer.ExitBeforeStderrIsDrained();
        await peer.ReapStarted.Task;
        peer.FinishStderr("Error: Unknown option: --preview-server", "Did you mean --seek?", "QSVEncC.exe finished with error!");

        var failure = await Assert.ThrowsAsync<PreviewServerNotSupportedException>(() => run);
        Assert.Equal(CoreText.ServerHandshakeUnsupported + " (Error: Unknown option: --preview-server)", failure.Message);
        Assert.Contains("Did you mean --seek?", failure.StandardError);
    }

    [Fact]
    public async Task EncoderExitingDuringHistoryProbeExposesTheErrorReadAtEof()
    {
        await using var fixture = new Sessions(new FakeTimeProvider(), PreviewTimeouts.Infinite, supportsProbe: true);
        Task<bool?> run = fixture.Backend.GetHistoryRequirementAsync(Sessions.Request());
        Peer peer = await fixture.NextPeerAsync();
        Assert.Equal("probe", (await peer.NextCommandAsync()).Name);
        peer.ExitBeforeStderrIsDrained();
        await peer.ReapStarted.Task;
        peer.FinishStderr("Error: could not probe the filter");

        var failure = await Assert.ThrowsAsync<PreviewServerNotSupportedException>(() => run);
        Assert.Equal(CoreText.ServerHandshakeFailed + " (Error: could not probe the filter)", failure.Message);
        Assert.Contains("Error: could not probe the filter", failure.StandardError);
    }

    [Fact]
    public async Task CancellationTimeoutDropsTheSessionAndPreservesDiagnostics()
    {
        var clock = new FakeTimeProvider();
        var timers = clock.ObserveTimers();
        TimeSpan timeout = TimeSpan.FromSeconds(5);
        await using var fixture = new Sessions(clock, PreviewTimeouts.Infinite with { Cancel = timeout });
        using var cancellation = new CancellationTokenSource();

        Task<PreviewRunResult> run = fixture.RunAsync(cancellation.Token);
        Peer stalled = await fixture.NextPeerAsync();
        (string command, long id) = await stalled.NextCommandAsync();
        Assert.Equal("run", command);
        stalled.Begin(id);
        cancellation.Cancel();
        Assert.Equal(("cancel", id), await stalled.NextCommandAsync());
        Assert.Equal(timeout, await timers.ReadAsync());
        clock.Advance(timeout);

        PreviewRunResult result = await run;
        Assert.True(result.IsCancelled);
        Assert.Equal(-12, result.ExitCode);
        Assert.Contains("記録を保持", result.StandardError);
        Assert.True(stalled.WasReaped);
        Assert.NotSame(stalled, await fixture.CompleteRunAsync());
    }

    [Fact]
    public async Task DisposeReportsIncompleteWhenActiveRequestMissesDeadline()
    {
        var clock = new FakeTimeProvider();
        var timers = clock.ObserveTimers();
        TimeSpan stopTimeout = TimeSpan.FromSeconds(1);
        await using var fixture = new Sessions(clock, PreviewTimeouts.Infinite with { Stop = stopTimeout });
        using var cancellation = new CancellationTokenSource();

        Task<PreviewRunResult> run = fixture.RunAsync(cancellation.Token);
        Peer stalled = await fixture.NextPeerAsync();
        (string command, long id) = await stalled.NextCommandAsync();
        Assert.Equal("run", command);
        stalled.Begin(id);
        cancellation.Cancel();
        Assert.Equal(("cancel", id), await stalled.NextCommandAsync());

        Task disposal = fixture.Backend.DisposeAsync().AsTask();
        Assert.Equal(stopTimeout, await timers.ReadAsync());
        clock.Advance(stopTimeout);

        await Assert.ThrowsAsync<TimeoutException>(() => disposal);
        Assert.True((await run).IsCancelled);
        Assert.True(stalled.WasReaped);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.RunAsync());
    }

    [Fact]
    public async Task QuitTimeoutReapsTheSession()
    {
        var clock = new FakeTimeProvider();
        var timers = clock.ObserveTimers();
        TimeSpan stopTimeout = TimeSpan.FromSeconds(4);
        await using var fixture = new Sessions(clock, PreviewTimeouts.Infinite with { Stop = stopTimeout });
        Peer stalled = await fixture.CompleteRunAsync();

        Task disposal = fixture.Backend.DisposeAsync().AsTask();
        Assert.Equal(("quit", 0L), await stalled.NextCommandAsync());
        TimeSpan quitTimeout = CleanupDeadline.Half(stopTimeout);
        Assert.Equal(quitTimeout, await timers.ReadAsync());
        clock.Advance(quitTimeout);

        await disposal;
        Assert.True(stalled.WasReaped);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.RunAsync());
    }

    private sealed class Sessions(FakeTimeProvider clock, PreviewTimeouts timeouts, bool withholdFirstReady = false, bool supportsProbe = false) : IAsyncDisposable
    {
        private readonly List<Peer> _peers = [];
        private readonly Channel<Peer> _started = Channel.CreateUnbounded<Peer>();
        private ServerBackend? _backend;

        public ServerBackend Backend => _backend ??= new ServerBackend(timeouts, clock, startProcess: _ =>
        {
            var peer = new Peer(_peers.Count + 1);
            if (!withholdFirstReady || _peers.Count != 0) peer.SendReady(supportsProbe);
            _peers.Add(peer);
            _started.Writer.TryWrite(peer);
            return peer;
        });

        public static PreviewRequest Request() => new()
        {
            EncoderPath = "fake-encoder", InputFile = "fake.ts", Fps = 30,
            TargetSeconds = 0, PreMarginSeconds = 0, PostMarginSeconds = 0.1,
            InputArgs = ["--avhw"],
        };

        public Task<PreviewRunResult> RunAsync(CancellationToken token = default) => Backend.RunAsync(Request(), new Sink(), token);

        public ValueTask<Peer> NextPeerAsync() => _started.Reader.ReadAsync();

        public async Task<Peer> CompleteRunAsync()
        {
            Task<PreviewRunResult> run = RunAsync();
            Peer peer = await NextPeerAsync();
            (string command, long id) = await peer.NextCommandAsync();
            Assert.Equal("run", command);
            peer.Begin(id);
            peer.End(id);
            Assert.Equal(0, (await run).ExitCode);
            return peer;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (Peer peer in _peers) peer.Close();
            if (_backend != null)
                await _backend.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class Sink : IPreviewSink
    {
        public void OnFrame(Y4mFrame frame) { }
        public void OnOutput() { }
        public void OnHeader(Y4mHeader header) { }
    }

    private sealed class Peer : IServerProcess
    {
        private readonly CommandWriter _input = new();
        private readonly ResponseStream _output = new();
        private readonly ErrorReader _error = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Peer(int id) => Id = id;

        public int Id { get; }
        public int ExitCode { get; private set; }
        public bool HasExited => _exited.Task.IsCompleted;
        public bool WasReaped { get; private set; }
        public TaskCompletionSource ReapStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TextWriter StandardInput => _input;
        public Stream StandardOutput => _output;
        public TextReader StandardError => _error;

        public void SendReady(bool supportsProbe) => _output.WriteLine("RGYPREVIEW READY version=1" + (supportsProbe ? " probe=1" : ""));

        public void Begin(long id)
        {
            _error.WriteLine($"RGYPREVIEW BEGIN id={id}");
            _error.WriteLine("記録を保持");
            _output.WriteLine($"RGYPREVIEW BEGIN id={id}");
        }

        public void End(long id)
        {
            _error.WriteLine($"RGYPREVIEW END id={id} rc=0");
            _output.WriteLine($"RGYPREVIEW END id={id} rc=0");
        }

        public async ValueTask<(string Name, long Id)> NextCommandAsync()
        {
            using var json = JsonDocument.Parse(await _input.Commands.Reader.ReadAsync());
            var command = json.RootElement;
            return (command.GetProperty("cmd").GetString()!, command.TryGetProperty("id", out var id) ? id.GetInt64() : 0);
        }

        public Task WaitForExitAsync() => _exited.Task;

        public Task<bool> ReapAsync(TimeProvider clock, TimeSpan timeout)
        {
            WasReaped = true;
            if (!HasExited) Close();
            ReapStarted.TrySetResult();
            return Task.FromResult(true);
        }

        public void ExitBeforeStderrIsDrained()
        {
            ExitCode = 23;
            _exited.TrySetResult();
            _output.CloseOutput();
        }

        public void FinishStderr(params string[] lines)
        {
            foreach (string line in lines) _error.WriteLine(line);
            _error.CloseOutput();
        }

        public void Close()
        {
            _output.CloseOutput();
            _error.CloseOutput();
            _exited.TrySetResult();
        }

        public void Dispose() => Close();
    }

    private sealed class CommandWriter : TextWriter
    {
        public Channel<string> Commands { get; } = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override Task FlushAsync() => Task.CompletedTask;
        public override Task WriteLineAsync(string? value)
        {
            Commands.Writer.TryWrite(value!);
            return Task.CompletedTask;
        }
    }

    private sealed class ErrorReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public void WriteLine(string value) => _lines.Writer.TryWrite(value);
        public void CloseOutput() => _lines.Writer.TryComplete();
        public override async Task<string?> ReadLineAsync()
            => await _lines.Reader.WaitToReadAsync() && _lines.Reader.TryRead(out var line) ? line : null;
    }

    private sealed class ResponseStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private ReadOnlyMemory<byte> _remaining;

        public void WriteLine(string value) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(value + "\n"));
        public void CloseOutput() => _chunks.Writer.TryComplete();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (_remaining.IsEmpty)
            {
                if (!_chunks.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) return 0;
                if (!_chunks.Reader.TryRead(out var chunk)) throw new InvalidOperationException();
                _remaining = chunk;
            }
            int count = Math.Min(buffer.Length, _remaining.Length);
            _remaining.Span[..count].CopyTo(buffer);
            _remaining = _remaining[count..];
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

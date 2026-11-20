using System.Text;
using System.Threading.Channels;
using HWEncVppPreview.Cli;
using HWEncVppPreview.Core;
using HWEncVppPreview.Testing;

namespace HWEncVppPreview.Tests;

public sealed class AmatsukazeEngineTests
{
    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte> _bytes = Channel.CreateUnbounded<byte>();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long x, SeekOrigin y) => throw new NotSupportedException(); public override void SetLength(long x) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) { foreach (byte b in buffer.AsSpan(offset, count)) _bytes.Writer.TryWrite(b); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { Write(buffer.ToArray(), 0, buffer.Length); return ValueTask.CompletedTask; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty) return 0;
            if (!await _bytes.Reader.WaitToReadAsync(token)) return 0;
            int n = 0; while (n < buffer.Length && _bytes.Reader.TryRead(out byte b)) buffer.Span[n++] = b; return n;
        }
        public async Task<string> LineAsync(CancellationToken token)
        {
            var b = new byte[1]; var result = new List<byte>();
            while (await ReadAsync(b, token) != 0) { if (b[0] == '\n') return Encoding.UTF8.GetString(result.ToArray()); result.Add(b[0]); }
            throw new EndOfStreamException();
        }
        public async Task<byte[]> BytesAsync(int length, CancellationToken token)
        {
            var result = new byte[length]; int offset = 0;
            while (offset < length) { int n = await ReadAsync(result.AsMemory(offset), token); if (n == 0) throw new EndOfStreamException(); offset += n; }
            return result;
        }
        public byte[] Remaining() { var result = new List<byte>(); while (_bytes.Reader.TryRead(out var value)) result.Add(value); return result.ToArray(); }
        public void Send(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value + "\n"); Write(bytes, 0, bytes.Length); }
    }
    private sealed class Source : IFrameNumberSource
    {
        public FrameNumberInfo Info { get; } = new(20, new Y4mHeader { Width = 4, Height = 4, FpsNum = 30, FpsDen = 1 });
        public KfmFrameProperties? GetProperties(int position) => new(2, position % 2 == 0 ? 2 : 1);
        public Y4mFrame GetFrame(int position)
        {
            return new(Info.Header, position / 30d, null, Enumerable.Repeat((byte)position, Info.Header.FrameBytes).ToArray());
        }
        public void Dispose() { }
    }
    private sealed class Backend : IPreviewBackend
    {
        public PreviewRequest? Request { get; private set; }
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Request = request;
            var h = new Y4mHeader { Width = 4, Height = 4, FpsNum = 30, FpsDen = 1 };
            sink.OnHeader(h); sink.OnFrame(new Y4mFrame(h, request.TargetSeconds, null, new byte[h.FrameBytes]));
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task エンコーダ設定で常駐サーバへavsとフィルタを渡す()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = new ChannelStream(); var output = new ChannelStream(); var backend = new Backend();
        await using var engine = new AmatsukazeEngine(input, output, TextWriter.Null, "/tmp", "/tmp", new() { ["QSVEnc"] = "fake" }, (_, _) => new Source(), () => backend);
        Task run = engine.RunAsync(timeout.Token);
        _ = await output.LineAsync(timeout.Token);
        input.Send("{\"cmd\":\"config\",\"gen\":2,\"kind\":\"encoder\",\"encoder\":\"QSVEnc\",\"interlace\":\"tff\",\"vpp\":[\"--vpp-knn\"]}");
        var response1 = await output.LineAsync(timeout.Token);
        input.Send("{\"cmd\":\"frame\",\"id\":5,\"gen\":2,\"pos\":9,\"format\":\"yuv\"}");
        string header = await output.LineAsync(timeout.Token);
        var response2 = await output.BytesAsync(24, timeout.Token);
        input.Send("{\"cmd\":\"quit\"}"); await run.WaitAsync(timeout.Token);
        Assert.StartsWith("AMTPREVIEW INFO gen=2", response1);
        Assert.Contains("id=5 gen=2 pos=9", header);
        Assert.Equal(24, (response2).Length);
        Assert.NotNull(backend.Request);
        Assert.Contains("--avs", backend.Request.InputArgs);
        Assert.Contains("--interlace", backend.Request.InputArgs);
        Assert.Contains("tff", backend.Request.InputArgs);
        Assert.False(backend.Request.Capabilities.TimestampPassthrough);
        Assert.Contains("--vpp-knn", backend.Request.VppArgs);
        Assert.Equal(0.3, backend.Request.TargetSeconds, 6);
    }
    [Fact]
    public async Task 偽常駐サーバではavsのoutputStartを送らない()
    {
        using var cancellation = new CancellationTokenSource();
        string log = Path.Combine(Path.GetTempPath(), "amt-preview-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var input = new ChannelStream(); var output = new ChannelStream();
            await using (var engine = new AmatsukazeEngine(input, output, TextWriter.Null, "/tmp", "/tmp",
                new() { ["QSVEnc"] = BackendTests.FakeEncoderPath() }, (_, _) => new Source(),
                () => TestProcesses.CreateServerBackend(configureStartInfo: p => { p.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = log; p.Environment["FAKE_ENCODER_WINDOW_SEEK"] = "1"; p.Environment["FAKE_ENCODER_OUTPUT_START"] = "1"; })))
            {
                Task run = engine.RunAsync(cancellation.Token);
                try
                {
                    _ = await output.LineAsync(cancellation.Token);
                    input.Send("{\"cmd\":\"config\",\"gen\":1,\"kind\":\"encoder\",\"encoder\":\"QSVEnc\"}");
                    _ = await output.LineAsync(cancellation.Token);
                    input.Send("{\"cmd\":\"frame\",\"id\":3,\"gen\":1,\"pos\":15}");
                    string header = await output.LineAsync(cancellation.Token);
                    int length = int.Parse(header.Split(' ').Single(x => x.StartsWith("bytes=", StringComparison.Ordinal))[6..]);
                    var response3 = await output.BytesAsync(length, cancellation.Token);
                    input.Send("{\"cmd\":\"quit\"}");
                    await run;
                    Assert.Contains("FRAME id=3", header);
                    Assert.Equal(64 * 36 * 3 / 2, response3.Length);
                }
                finally
                {
                    cancellation.Cancel();
                    await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                }
            }
            var entries = FakeLogFiles.ReadAllLines(log).Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
            try
            {
                var request = entries.Select(x => x.RootElement).Single(x => x.GetProperty("cmd").GetString() == "run");
                Assert.False(request.TryGetProperty("output_start_us", out _));
                Assert.Contains(request.GetProperty("args").EnumerateArray(), x => x.GetString() == "--avs");
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
        }
        finally { FakeLogFiles.Delete(log); }
    }
    private sealed class BlockingSource : IFrameNumberSource
    {
        public readonly ManualResetEventSlim Started = new(), Release = new();
        public FrameNumberInfo Info { get; } = new(20, new Y4mHeader { Width = 4, Height = 4, FpsNum = 30, FpsDen = 1 });
        public KfmFrameProperties? GetProperties(int position) => null;
        public Y4mFrame GetFrame(int position)
        {
            Started.Set(); Release.Wait();
            return new(Info.Header, null, null, new byte[Info.Header.FrameBytes]);
        }
        public void Dispose() { }
    }
    [Fact]
    public async Task 世代切替の完了後は新しい世代の応答だけを返す()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = new ChannelStream(); var output = new ChannelStream(); var blocked = new BlockingSource();
        int opened = 0;
        string response4, response5, response6;
        await using (var engine = new AmatsukazeEngine(input, output, TextWriter.Null, "/tmp", "/tmp", new(), (_, _) => Interlocked.Increment(ref opened) == 1 ? blocked : new Source()))
        {
            Task run = engine.RunAsync(timeout.Token); _ = await output.LineAsync(timeout.Token);
            input.Send("{\"cmd\":\"config\",\"gen\":1,\"kind\":\"avs\",\"path\":\"a.avs\"}");
            _ = await output.LineAsync(timeout.Token);
            input.Send("{\"cmd\":\"frame\",\"id\":7,\"gen\":1,\"pos\":3}");
            try
            {
                await Task.Run(() => blocked.Started.Wait(timeout.Token)).WaitAsync(timeout.Token);
                input.Send("{\"cmd\":\"config\",\"gen\":2,\"kind\":\"avs\",\"path\":\"b.avs\"}");
                input.Send("{\"cmd\":\"unknown\"}");
                response4 = await output.LineAsync(timeout.Token);
            }
            finally { blocked.Release.Set(); }
            response5 = await output.LineAsync(timeout.Token);
            input.Send("{\"cmd\":\"frame\",\"id\":8,\"gen\":2,\"pos\":4}");
            response6 = await output.LineAsync(timeout.Token);
            _ = await output.BytesAsync(24, timeout.Token);
            input.Send("{\"cmd\":\"quit\"}"); await run.WaitAsync(timeout.Token);
        }
        Assert.Contains("ERROR id=0 gen=2", response4);
        Assert.StartsWith("AMTPREVIEW INFO gen=2", response5);
        Assert.Contains("FRAME id=8 gen=2", response6);
        Assert.Empty(output.Remaining());
    }
    [Fact]
    public async Task クロップと縮小はYUVプレーン全体に適用される()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = new ChannelStream(); var output = new ChannelStream();
        await using var engine = new AmatsukazeEngine(input, output, TextWriter.Null, "/tmp", "/tmp", new(), (_, _) => new Source());
        Task run = engine.RunAsync(timeout.Token); _ = await output.LineAsync(timeout.Token);
        input.Send("{\"cmd\":\"config\",\"gen\":1,\"kind\":\"avs\",\"path\":\"test.avs\"}");
        _ = await output.LineAsync(timeout.Token);
        input.Send("{\"cmd\":\"frame\",\"id\":9,\"gen\":1,\"pos\":6,\"format\":\"yuv\",\"scale\":2,\"crop\":[0,0,4,4]}");
        string header = await output.LineAsync(timeout.Token);
        var response7 = await output.BytesAsync(6, timeout.Token);
        input.Send("{\"cmd\":\"quit\"}"); await run.WaitAsync(timeout.Token);
        Assert.Contains("width=2 height=2 bytes=6", header);
        Assert.All(response7, b => Assert.Equal(6, b));
    }
    [Fact]
    public async Task 設定後にバイナリフレームを送る()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = new ChannelStream(); var output = new ChannelStream();
        await using var engine = new AmatsukazeEngine(input, output, TextWriter.Null, "/tmp", "/tmp", new(), (_, _) => new Source());
        Task run = engine.RunAsync(timeout.Token);
        var response8 = await output.LineAsync(timeout.Token);
        input.Send("{\"cmd\":\"config\",\"gen\":1,\"kind\":\"avs\",\"path\":\"test.avs\",\"sarnum\":4,\"sarden\":3,\"matrix\":\"bt709\"}");
        string info = await output.LineAsync(timeout.Token);
        input.Send("{\"cmd\":\"frame\",\"id\":4,\"gen\":1,\"pos\":6,\"format\":\"yuv\",\"scale\":1}");
        string header = await output.LineAsync(timeout.Token);
        var response9 = await output.BytesAsync(24, timeout.Token);
        input.Send("{\"cmd\":\"frame\",\"id\":5,\"gen\":1,\"pos\":6,\"step\":1}");
        var response10 = await output.LineAsync(timeout.Token);
        var response11 = await output.BytesAsync(24, timeout.Token);
        input.Send("{\"cmd\":\"quit\"}"); await run.WaitAsync(timeout.Token);
        Assert.Equal("AMTPREVIEW READY version=1", response8);
        Assert.StartsWith("AMTPREVIEW INFO gen=1 frames=20", info);
        Assert.Contains("sarnum=4 sarden=3", info);
        Assert.Contains("id=4 gen=1 pos=6 type=30p dur=2 kept=1 format=yuv420p8", header);
        Assert.Contains("bytes=24", header);
        Assert.Contains("sarnum=4 sarden=3", header);
        Assert.Contains("matrix=bt709", header);
        Assert.All(response9, b => Assert.Equal(6, b));
        Assert.Contains("id=5 gen=1 pos=8", response10);
        Assert.All(response11, b => Assert.Equal(8, b));
    }
}

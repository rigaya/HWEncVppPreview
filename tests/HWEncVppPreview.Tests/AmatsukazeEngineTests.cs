using System.Text;
using System.Threading.Channels;
using HWEncVppPreview.Cli;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed partial class AmatsukazeEngineTests
{
    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte> _bytes = Channel.CreateUnbounded<byte>();
        private readonly object _sync = new();
        private long _written, _read;
        private (long Position, TaskCompletionSource Completion)? _consumed;

        private (byte[] Prefix, TaskCompletionSource Resume)? _writeGate;

        public Task SendAndObserveConsumptionAsync(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\n");
            lock (_sync)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _consumed = (_written + bytes.Length, completion);
                Write(bytes, 0, bytes.Length);
                return completion.Task;
            }
        }

        public TaskCompletionSource BlockAfterWritingLine(string prefix)
        {
            lock (_sync)
            {
                var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _writeGate = (Encoding.UTF8.GetBytes(prefix), resume);
                return resume;
            }
        }

        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long x, SeekOrigin y) => throw new NotSupportedException(); public override void SetLength(long x) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                _written += count;
                foreach (byte b in buffer.AsSpan(offset, count)) _bytes.Writer.TryWrite(b);
            }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            lock (_sync)
            {
                Write(buffer.ToArray(), 0, buffer.Length);
                if (_writeGate is not { } gate || !buffer.Span.StartsWith(gate.Prefix)) return ValueTask.CompletedTask;
                _writeGate = null;
                return new ValueTask(gate.Resume.Task.WaitAsync(token));
            }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty) return 0;
            lock (_sync)
            {
                if (_consumed is { } observed && _read >= observed.Position)
                {
                    _consumed = null;
                    observed.Completion.TrySetResult();
                }
            }
            if (!await _bytes.Reader.WaitToReadAsync(token)) return 0;
            int n = 0;
            while (n < buffer.Length && _bytes.Reader.TryRead(out byte b)) buffer.Span[n++] = b;
            lock (_sync) _read += n;
            return n;
        }
        public async Task<string> LineAsync(CancellationToken token = default)
        {
            var b = new byte[1]; var result = new List<byte>();
            while (await ReadAsync(b, token) != 0) { if (b[0] == '\n') return Encoding.UTF8.GetString(result.ToArray()); result.Add(b[0]); }
            throw new EndOfStreamException();
        }
        public async Task<byte[]> BytesAsync(int length, CancellationToken token = default)
        {
            var result = new byte[length]; int offset = 0;
            while (offset < length) { int n = await ReadAsync(result.AsMemory(offset), token); if (n == 0) throw new EndOfStreamException(); offset += n; }
            return result;
        }
        public void Send(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value + "\n"); Write(bytes, 0, bytes.Length); }
        public void Complete(Exception? error = null) => _bytes.Writer.TryComplete(error);
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
    private sealed class InterlacedSource : IFrameNumberSource
    {
        public FrameNumberInfo Info { get; } = new(1, new Y4mHeader { Width = 4, Height = 4, FpsNum = 30, FpsDen = 1 });
        public KfmFrameProperties? GetProperties(int position) => null;
        public Y4mFrame GetFrame(int position) =>
            new(Info.Header, null, null,
                [20, 20, 20, 20, 120, 120, 120, 120, 20, 20, 20, 20, 120, 120, 120, 120, 128, 128, 128, 128, 128, 128, 128, 128]);
        public void Dispose() { }
    }

    private sealed class BlockingSource : IFrameNumberSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        public FrameNumberInfo Info { get; } = new(20, new Y4mHeader { Width = 4, Height = 4, FpsNum = 30, FpsDen = 1 });
        public KfmFrameProperties? GetProperties(int position) => null;
        public Y4mFrame GetFrame(int position)
        {
            Started.TrySetResult(); Release.Wait();
            return new(Info.Header, null, null, new byte[Info.Header.FrameBytes]);
        }
        public void Dispose() { }
    }
    [Fact]
    public async Task 世代切替の完了後は新しい世代の応答だけを返す()
    {
        var input = new ChannelStream(); var output = new ChannelStream(); var blocked = new BlockingSource();
        int opened = 0;
        string response4, response5, response6;
        var engine = new AmatsukazeEngine(input, output, TextWriter.Null, "/tmp", "/tmp", new(), (_, _) => Interlocked.Increment(ref opened) == 1 ? blocked : new Source());
        Task run = RunEngineAsync(engine, output);
        try
        {
            _ = await output.LineAsync();
            input.Send("{\"cmd\":\"config\",\"gen\":1,\"kind\":\"avs\",\"path\":\"a.avs\"}");
            _ = await output.LineAsync();
            input.Send("{\"cmd\":\"frame\",\"id\":7,\"gen\":1,\"pos\":3}");
            try
            {
                await blocked.Started.Task;
                input.Send("{\"cmd\":\"config\",\"gen\":2,\"kind\":\"avs\",\"path\":\"b.avs\"}");
                input.Send("{\"cmd\":\"unknown\"}");
                response4 = await output.LineAsync();
            }
            finally { blocked.Release.Set(); }
            response5 = await output.LineAsync();
            input.Send("{\"cmd\":\"frame\",\"id\":8,\"gen\":2,\"pos\":4}");
            response6 = await output.LineAsync();
            _ = await output.BytesAsync(24);
            input.Send("{\"cmd\":\"quit\"}");
            await run;
        }
        finally
        {
            await CompleteEngineAsync(input, run);
        }
        Assert.Contains("ERROR id=0 gen=2", response4);
        Assert.StartsWith("AMTPREVIEW INFO gen=2", response5);
        Assert.Contains("FRAME id=8 gen=2", response6);
        Assert.Equal(0, await output.ReadAsync(new byte[1]));
    }
    [Fact]
    public async Task クロップと縮小はYUVプレーン全体に適用される()
    {
        var input = new ChannelStream(); var output = new ChannelStream();
        var engine = new AmatsukazeEngine(input, output, TextWriter.Null, "/tmp", "/tmp", new(), (_, _) => new Source());
        Task run = RunEngineAsync(engine, output);
        try
        {
            _ = await output.LineAsync();
            input.Send("{\"cmd\":\"config\",\"gen\":1,\"kind\":\"avs\",\"path\":\"test.avs\"}");
            _ = await output.LineAsync();
            input.Send("{\"cmd\":\"frame\",\"id\":9,\"gen\":1,\"pos\":6,\"format\":\"yuv\",\"scale\":2,\"crop\":[0,0,4,4]}");
            string header = await output.LineAsync();
            var response7 = await output.BytesAsync(6);
            input.Send("{\"cmd\":\"quit\"}");
            await run;
            Assert.Contains("width=2 height=2 bytes=6", header);
            Assert.All(response7, b => Assert.Equal(6, b));
        }
        finally
        {
            await CompleteEngineAsync(input, run);
        }
    }
    [Fact]
    public async Task 設定後にバイナリフレームを送る()
    {
        var input = new ChannelStream(); var output = new ChannelStream();
        var engine = new AmatsukazeEngine(input, output, TextWriter.Null, "/tmp", "/tmp", new(), (_, _) => new Source());
        Task run = RunEngineAsync(engine, output);
        try
        {
            var response8 = await output.LineAsync();
            input.Send("{\"cmd\":\"config\",\"gen\":1,\"kind\":\"avs\",\"path\":\"test.avs\",\"sarnum\":4,\"sarden\":3,\"matrix\":\"bt709\"}");
            string info = await output.LineAsync();
            input.Send("{\"cmd\":\"frame\",\"id\":4,\"gen\":1,\"pos\":6,\"format\":\"yuv\",\"scale\":1}");
            string header = await output.LineAsync();
            var response9 = await output.BytesAsync(24);
            input.Send("{\"cmd\":\"frame\",\"id\":5,\"gen\":1,\"pos\":6,\"step\":1}");
            var response10 = await output.LineAsync();
            var response11 = await output.BytesAsync(24);
            input.Send("{\"cmd\":\"quit\"}");
            await run;
            Assert.Equal("AMTPREVIEW READY version=2", response8);
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
        finally
        {
            await CompleteEngineAsync(input, run);
        }
    }

    [Fact]
    public async Task 終了後も受信済みの応答を読み切ってからEOFを返す()
    {
        var input = new ChannelStream();
        var output = new ChannelStream();
        var engine = new AmatsukazeEngine(input, output, TextWriter.Null, "/tmp", "/tmp", new());

        input.Send("{\"cmd\":\"quit\"}");
        await RunEngineAsync(engine, output);

        Assert.Equal("AMTPREVIEW READY version=2", await output.LineAsync());
        Assert.Equal(0, await output.ReadAsync(new byte[1]));
    }

    private static async Task RunEngineAsync(AmatsukazeEngine engine, ChannelStream output, CancellationToken token = default)
    {
        try
        {
            await using (engine) await engine.RunAsync(token);
        }
        catch (Exception error)
        {
            output.Complete(error);
            throw;
        }
        // 遅れて発行された応答も出力に残し、破棄まで終わってからEOFを返す。
        output.Complete();
    }

    [Theory]
    [InlineData("tff", "progressive", 20, 120, 70, 70)]
    [InlineData("bff", "bff", 20, 120, 20, 120)]
    [InlineData("progressive", "progressive", 70, 70, 70, 70)]
    public async Task AviSynthのフィルタ前後に別々のインタレ指定を使う(
        string inputInterlace, string outputInterlace, int beforeTop, int beforeBottom, int afterTop, int afterBottom)
    {
        var input = new ChannelStream();
        var output = new ChannelStream();
        var engine = new AmatsukazeEngine(input, output, TextWriter.Null, "/tmp", "/tmp", new(), (_, _) => new InterlacedSource());
        Task run = RunEngineAsync(engine, output);
        try
        {
            Assert.Equal("AMTPREVIEW READY version=2", await output.LineAsync());
            input.Send(System.Text.Json.JsonSerializer.Serialize(new
            {
                cmd = "config", gen = 1, kind = "avs", path = "test.avs",
                interlace = inputInterlace, afterInterlace = outputInterlace, matrix = "bt709",
            }));
            Assert.StartsWith("AMTPREVIEW INFO gen=1 ", await output.LineAsync());

            await CheckSide("before", beforeTop, beforeBottom);
            await CheckSide("after", afterTop, afterBottom);
        }
        finally
        {
            await CompleteEngineAsync(input, run);
        }

        async Task CheckSide(string side, int top, int bottom)
        {
            input.Send(System.Text.Json.JsonSerializer.Serialize(new { cmd = "frame", gen = 1, id = 1, pos = 0, side, scale = 2 }));
            Assert.Contains("width=2 height=2 bytes=6", await output.LineAsync());
            Assert.Equal(new byte[] { (byte)top, (byte)top, (byte)bottom, (byte)bottom, 128, 128 }, await output.BytesAsync(6));
        }
    }

    private static async Task CompleteEngineAsync(ChannelStream input, Task run)
    {
        input.Complete();
        await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
    }
}

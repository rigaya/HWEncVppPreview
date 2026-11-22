using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Cli;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class AmatsukazeEngineParityTests
{
    private const string EncoderPath = "fake-encoder";

    [AvaloniaTheory]
    [InlineData(60)]
    [InlineData(24)]
    public async Task GUIとエンジンは同じシークと前後移動で同じ時刻の画像を選ぶ(int outputRate)
    {
        double[] timestamps = outputRate == 60
            ? [0, 0.016, 0.039, 0.057, 0.081, 0.101, 0.118, 0.144]
            : [0, 0.042, 0.096, 0.136, 0.211, 0.258, 0.305, 0.374];
        double seekSeconds = timestamps[2] + 0.001;
        string directory = Path.GetTempPath();
        var converted = new ConcurrentDictionary<byte, (double? Xts, double? Xdur)>();

        using var input = new ProtocolStream();
        using var output = new ProtocolStream();
        using var log = new StringWriter(CultureInfo.InvariantCulture);
        await using var engine = new AmatsukazeEngine(input, output, log, directory, directory,
            new() { ["QSVEnc"] = EncoderPath }, encoderSessionFactory: () => new(
                TimeProvider.System, PreviewTimeouts.Infinite, new VariableRateBackend(outputRate, timestamps),
                CreateProbe(), TimeSpan.Zero, ReadEncoderInfo));

        await using var vm = TestViewModels.Create(backend: new VariableRateBackend(outputRate, timestamps),
            probe: CreateProbe(), converter: Convert, debounceOverride: TimeSpan.Zero,
            readTextOverride: ReadEncoderInfo);
        vm.EncoderPath = EncoderPath;
        vm.InputPath = Path.Combine(directory, "filter_preview", "src.avs");
        vm.InputArgsText = OperatingSystem.IsWindows()
            ? $"""--avsdll "{Path.Combine(directory, "AviSynth.dll")}" --avs"""
            : "--avs";

        Task run = RunEngineAsync(engine, output);
        try
        {
            Assert.Equal("AMTPREVIEW READY version=2", await output.ReadLineAsync());
            input.Send(new { cmd = "config", gen = 1, kind = "encoder", encoder = "QSVEnc" });
            Assert.StartsWith("AMTPREVIEW INFO gen=1 ", await output.ReadLineAsync());

            await vm.InitializeAsync(false);
            await vm.WaitForIdleAsync();

            vm.CurrentTimeText = seekSeconds.ToString("R", CultureInfo.InvariantCulture);
            vm.CommitTimeTextCommand.Execute(null);
            input.Send(new { cmd = "view", id = 1, gen = 1, op = "seek", seconds = seekSeconds, phase = "commit" });
            await AssertSameSelectionAsync(1, expectedIndex: 2, expectedSeconds: seekSeconds);

            vm.StepFrameCommand.Execute(1);
            input.Send(new { cmd = "view", id = 2, gen = 1, op = "step", direction = "next" });
            await AssertSameSelectionAsync(2, expectedIndex: 3, expectedSeconds: timestamps[3]);

            vm.StepFrameCommand.Execute(-1);
            input.Send(new { cmd = "view", id = 3, gen = 1, op = "step", direction = "previous" });
            await AssertSameSelectionAsync(3, expectedIndex: 2, expectedSeconds: timestamps[2]);

            input.Send(new { cmd = "quit" });
            await run;
        }
        finally
        {
            input.Complete();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        Task<byte[]> Convert(Y4mFrame frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            byte marker = frame.Data[0];
            converted[marker] = (frame.Xts, frame.Xdur);
            byte[] pixels = new byte[frame.Header.Width * frame.Header.Height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = marker;
                pixels[i + 3] = byte.MaxValue;
            }
            return Task.FromResult(pixels);
        }

        async Task AssertSameSelectionAsync(int id, int expectedIndex, double expectedSeconds)
        {
            Selection cli = await ReadSelectionAsync(output, id);
            await vm.WaitForIdleAsync();
            Assert.Null(vm.ErrorText);
            using var bitmap = Assert.IsType<WriteableBitmap>(vm.PreviewImage).Lock();
            byte marker = Marshal.ReadByte(bitmap.Address);

            Assert.Equal((byte)(32 + expectedIndex), marker);
            Assert.Equal(marker, cli.Marker);
            Assert.Equal(timestamps[expectedIndex], cli.Xts);
            Assert.Equal(timestamps[expectedIndex], cli.InputSeconds, 9);
            Assert.Equal(converted[marker], (cli.Xts, cli.Xdur));
            Assert.Equal(expectedSeconds, vm.CurrentSeconds, 9);
            Assert.Equal(vm.CurrentSeconds, cli.CurrentSeconds, 9);
        }
    }

    private static InputProbe CreateProbe() => TestProcesses.CreateDurationProbe(
        PreviewTimeouts.Infinite, TimeProvider.System, new VariableRateBackend(30, [0]),
        (_, _) => throw new InvalidOperationException("入力検査の長さ通知を使用します。"));

    private static Task<string> ReadEncoderInfo(string _, string argument, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(argument == "--version" ? "QSVEncC 8.38" : "");
    }

    private sealed class VariableRateBackend(int rate, double[] timestamps) : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            var header = new Y4mHeader { Width = 4, Height = 4, FpsNum = rate, FpsDen = 1, SarNum = 1, SarDen = 1 };
            sink.OnHeader(header);
            for (int i = 0; i < timestamps.Length && !token.IsCancellationRequested; ++i)
            {
                double duration = i + 1 < timestamps.Length ? timestamps[i + 1] - timestamps[i] : 1.0 / rate;
                byte[] pixels = new byte[header.FrameBytes];
                Array.Fill(pixels, (byte)(32 + i));
                sink.OnFrame(new(header, timestamps[i], duration, pixels));
            }
            return Task.FromResult(new PreviewRunResult(0, "Duration: 00:00:00.500, start: 0.0"));
        }

        public ValueTask AbortAsync() => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task RunEngineAsync(AmatsukazeEngine engine, ProtocolStream output)
    {
        try { await engine.RunAsync(); }
        finally { output.Complete(); }
    }

    private sealed record Selection(double CurrentSeconds, double InputSeconds, double? Xts, double? Xdur, byte Marker);

    private static async Task<Selection> ReadSelectionAsync(ProtocolStream output, int id)
    {
        Selection? chosen = null;
        while (true)
        {
            var line = await output.ReadLineAsync();
            if (line.StartsWith("AMTPREVIEW DONE ", StringComparison.Ordinal))
            {
                Assert.Contains($"id={id} gen=1 status=ok", line);
                return Assert.IsType<Selection>(chosen);
            }

            Assert.StartsWith($"AMTPREVIEW VIEW id={id} gen=1 ", line);
            var header = line.Split(' ').Skip(2)
                .Select(field => field.Split('=', 2)).ToDictionary(field => field[0], field => field[1]);
            byte[] metadata = new byte[int.Parse(header["metaBytes"], CultureInfo.InvariantCulture)];
            await output.ReadExactlyAsync(metadata);
            byte[] pixels = new byte[int.Parse(header["bytes"], CultureInfo.InvariantCulture)];
            await output.ReadExactlyAsync(pixels);
            using var document = JsonDocument.Parse(metadata);
            var view = document.RootElement;

            int offset = 0;
            foreach (var frame in view.GetProperty("frames").EnumerateArray())
            {
                if (frame.GetProperty("side").GetString() == "after")
                {
                    chosen = new(view.GetProperty("currentSeconds").GetDouble(), frame.GetProperty("inputSeconds").GetDouble(),
                        OptionalDouble(frame, "xts"), OptionalDouble(frame, "xdur"), pixels[offset]);
                }
                offset += frame.GetProperty("bytes").GetInt32();
            }
            Assert.Equal(pixels.Length, offset);
        }
    }

    private static double? OptionalDouble(JsonElement item, string name)
        => item.GetProperty(name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    private sealed class ProtocolStream : Stream
    {
        private readonly Channel<byte> _bytes = Channel.CreateUnbounded<byte>(new() { SingleReader = true });

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Send<T>(T command) => Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "\n"));
        public void Complete() => _bytes.Writer.TryComplete();

        public async Task<string> ReadLineAsync()
        {
            var bytes = new List<byte>();
            while (true)
            {
                byte value = await _bytes.Reader.ReadAsync();
                if (value == '\n') return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(value);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty || !await _bytes.Reader.WaitToReadAsync(token).ConfigureAwait(false)) return 0;
            int count = 0;
            while (count < buffer.Length && _bytes.Reader.TryRead(out byte value))
                buffer.Span[count++] = value;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            foreach (byte value in buffer)
                if (!_bytes.Writer.TryWrite(value)) throw new EndOfStreamException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

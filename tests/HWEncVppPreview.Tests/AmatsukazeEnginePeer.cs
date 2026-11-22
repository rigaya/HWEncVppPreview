using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed partial class AmatsukazeEngineTests
{
    private sealed record EncoderRun(string[] Args, int InputFrames, int OutputFrames, bool HasOutputStart);

    private sealed class EncoderPeerFixture(TimeProvider clock)
    {
        public const int DelayedInputs = 3;
        public const int FrameBytes = 24;
        public const string Filter = "--vpp-afs";

        private readonly ConcurrentQueue<EncoderRun> _runs = new();
        private int _nextPeer;
        private BlockedRun? _nextBlock;

        public IEnumerable<EncoderRun> Runs => _runs;
        public IPreviewBackend CreateBackend() => TestProcesses.CreateServerBackendWithPeer(
            _ => new Peer(Interlocked.Increment(ref _nextPeer), this), clock);

        public EncoderPreviewSession CreateSession()
        {
            var inputProbe = TestProcesses.CreateDurationProbe(PreviewTimeouts.Infinite, clock, CreateBackend(),
                (_, _) => throw new InvalidOperationException("入力の長さは偽サーバが報告します。"));
            return new EncoderPreviewSession(clock, PreviewTimeouts.Infinite, CreateBackend(), inputProbe, TimeSpan.Zero,
                (_, _, _) => Task.FromResult("QSVEnc 8.38"), backgroundBackend: CreateBackend(),
                ownershipScope: new RawOwnershipScope(new RawBufferPool()));
        }

        public BlockedRun BlockNextFilteredRun()
        {
            var gate = new BlockedRun();
            if (Interlocked.CompareExchange(ref _nextBlock, gate, null) != null)
                throw new InvalidOperationException("既に取得を保留しています。");
            return gate;
        }

        public static byte[] MakePixels(int inputMilliseconds, bool filtered)
        {
            byte[] pixels = new byte[FrameBytes];
            // 画像を改行や制御行として読んでしまうと、次の応答の境界が崩れる。
            "AMTPREVIEW\n"u8.CopyTo(pixels);
            pixels[11] = (byte)(inputMilliseconds % 251);
            pixels[12] = filtered ? (byte)1 : (byte)2;
            return pixels;
        }

        public sealed class BlockedRun
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class Peer : IServerProcess
        {
            private readonly ChannelStream _out = new();
            private readonly Lines _err = new();
            private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly EncoderPeerFixture _fixture;
            private CancellationTokenSource? _active;
            private Task _run = Task.CompletedTask;

            public Peer(int id, EncoderPeerFixture fixture)
            {
                Id = id;
                _fixture = fixture;
                StandardInput = new Commands(HandleAsync);
                _out.Send("RGYPREVIEW READY version=1 window_seek=1 output_start=1");
            }

            public int Id { get; }
            public int ExitCode => 0;
            public bool HasExited => _exit.Task.IsCompleted;
            public TextWriter StandardInput { get; }
            public Stream StandardOutput => _out;
            public TextReader StandardError => _err;
            public Task WaitForExitAsync() => _exit.Task;

            private async Task HandleAsync(string value)
            {
                using var document = JsonDocument.Parse(value);
                JsonElement command = document.RootElement;
                switch (command.GetProperty("cmd").GetString())
                {
                    case "run":
                        await _run.ConfigureAwait(false);
                        _active?.Dispose();
                        _active = new CancellationTokenSource();
                        long id = command.GetProperty("id").GetInt64();
                        var args = command.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()!).ToArray();
                        _run = EncodeAsync(id, args, command.TryGetProperty("output_start_us", out _), _active.Token);
                        break;
                    case "cancel":
                        _active?.Cancel();
                        break;
                    case "quit":
                        await CloseAsync().ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException("偽常駐サーバの指示が不明です。");
                }
            }

            private async Task EncodeAsync(long id, string[] args, bool hasOutputStart, CancellationToken token)
            {
                string? framesArgument = ValueOf("--frames");
                int limit = framesArgument == null ? int.MaxValue : int.Parse(framesArgument, CultureInfo.InvariantCulture);
                string? seekArgument = ValueOf("--seek");
                int seek = seekArgument == null ? 0 : (int)Math.Round(
                    TimeSpan.Parse(seekArgument, CultureInfo.InvariantCulture).TotalMilliseconds);
                bool filtered = args.Contains(Filter);
                bool decimate = filtered && ValueOf(Filter) == "mode=decimate";
                bool warmup = args.Contains("--y4m");
                int outputRate = filtered ? decimate ? 16 : 60 : 30;

                SendControl($"RGYPREVIEW BEGIN id={id}");
                _err.Send("  Duration: 00:00:08.00, start: 0");
                int emitted = 0;
                try
                {
                    var blocked = filtered ? Interlocked.Exchange(ref _fixture._nextBlock, null) : null;
                    if (blocked != null)
                    {
                        blocked.Started.TrySetResult();
                        await blocked.Released.Task.WaitAsync(token).ConfigureAwait(false);
                    }
                    // 先頭の数枚を処理するまでは画を出さない。旧経路の入力1枚ではrc=0かつ出力0枚になる。
                    int delay = filtered ? DelayedInputs : 1;
                    if (limit < delay) return;
                    if (warmup) { SendFrame(0, 1); return; }

                    // 入力枚数で読める範囲を制限し、出力の並びはフィルタで変える。
                    double lastMillisecond = Math.Min(8000, seek + (limit - delay + 1d) * 1000 / 30);
                    if (filtered)
                    {
                        int[] offsets = decimate ? [0, 74, 155, 211, 250] : [0, 12, 28, 50, 71, 84, 100];
                        for (int start = 0; start < lastMillisecond && !token.IsCancellationRequested; start += offsets[^1])
                            for (int i = 0; i < offsets.Length - 1 && !token.IsCancellationRequested; ++i)
                            {
                                int position = start + offsets[i];
                                if (position >= seek && position < lastMillisecond)
                                    SendFrame(position, (offsets[i + 1] - offsets[i]) / 1000d);
                            }
                    }
                    else
                    {
                        for (int i = 0; i < 240 && !token.IsCancellationRequested; ++i)
                        {
                            int position = (int)Math.Round(i * 1000 / 30d, MidpointRounding.AwayFromZero);
                            if (position >= seek && position < lastMillisecond) SendFrame(position, 1 / 30d);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                finally
                {
                    _fixture._runs.Enqueue(new(args, limit, emitted, hasOutputStart));
                    SendControl($"RGYPREVIEW END id={id} rc=0");
                }

                string? ValueOf(string key)
                {
                    int index = Array.IndexOf(args, key);
                    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
                }

                void SendFrame(int timestamp, double duration)
                {
                    if (emitted++ == 0) _out.Send($"YUV4MPEG2 W4 H4 F{outputRate}:1 Ip A1:1 C420jpeg");
                    _out.Send(FormattableString.Invariant($"FRAME Xts={(timestamp - seek) / 1000d:F9} Xdur={duration:F9}"));
                    _out.Write(MakePixels(timestamp, filtered));
                }
            }

            private void SendControl(string line)
            {
                _err.Send(line);
                _out.Send(line);
            }

            private async Task CloseAsync()
            {
                _active?.Cancel();
                await _run.ConfigureAwait(false);
                _active?.Dispose();
                _active = null;
                _out.Complete();
                _err.Complete();
                _exit.TrySetResult();
            }

            public async Task<bool> ReapAsync(TimeProvider clock, TimeSpan timeout)
            {
                await CloseAsync().ConfigureAwait(false);
                return true;
            }

            public void Dispose() { }
        }

        private sealed class Lines : TextReader
        {
            private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
            public void Send(string line) => _lines.Writer.TryWrite(line);
            public void Complete() => _lines.Writer.TryComplete();
            public override async Task<string?> ReadLineAsync()
                => await _lines.Reader.WaitToReadAsync() && _lines.Reader.TryRead(out var line) ? line : null;
        }

        private sealed class Commands(Func<string, Task> write) : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
            public override Task WriteLineAsync(string? value) => write(value!);
            public override Task FlushAsync() => Task.CompletedTask;
        }
    }
}

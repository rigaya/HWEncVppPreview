using System.Globalization;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

// 偽時計の画面試験ではメタデータも同期可能な偽入力から取得する。
internal sealed class SyntheticInputProbe : IAsyncDisposable
{
    private bool _processAttempted;
    internal InputProbe Probe { get; }

    internal SyntheticInputProbe(TimeProvider clock)
    {
        Probe = TestProcesses.CreateInputProbe(new Backend(), clock, _ =>
        {
            _processAttempted = true;
            throw new InvalidOperationException("この試験ではプロセスを起動しません。");
        });
    }

    internal static Task<string> ReadTextAsync(string encoder, string argument, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (argument != "--help") return Task.FromResult("FakeEncoder");
        bool capabilities = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES") == "1";
        return Task.FromResult("  --vpp-nlmeans [params]\n  --vpp-edgelevel [params]\n  --vpp-deband [params]\n"
            + (capabilities ? "  --timestamp-passthrough  入力時刻を保持する。\n  --opencl-task-threads <int>  スレッド数。\n" : ""));
    }

    public async ValueTask DisposeAsync()
    {
        await Probe.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.False(_processAttempted, "偽時計の試験で実プロセスの起動を試みました。");
    }

    private sealed class Backend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            string[] args = request.InputArgs.ToArray();
            string Value(string option, string fallback)
            {
                int index = Array.IndexOf(args, option);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
            }
            string sar = Value("--fake-sar", "1:1");
            // 指定のない試験では、各移動先を制限しない十分な入力長を返す。
            string duration = Value("--fake-duration", "24:00:00.00");
            double origin = double.Parse(Value("--fake-origin-seconds", "1000"), CultureInfo.InvariantCulture);
            var header = Y4mHeader.Parse($"YUV4MPEG2 W64 H36 F30:1 Ip A{sar} C420jpeg");
            sink.OnHeader(header);
            sink.OnFrame(new Y4mFrame(header, origin, 1.0 / 30, new byte[header.FrameBytes]));
            return Task.FromResult(new PreviewRunResult(0, $"  Duration: {duration}, start: 0"));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace FakeEncoder;

/// <summary>実GPUなしでパイプ出力と中断・障害を再現する。</summary>
internal static class Program
{
    private sealed record Request(long Id, string[] Args, long Sequence);
    private static readonly Stream Output = Console.OpenStandardOutput();
    private static readonly object Sync = new();
    private static CancellationTokenSource? _currentCancellation;
    private static long _currentId;
    private static long _sequence;
    private static long _cancelledPending = -1;

    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        string? pidFile = Environment.GetEnvironmentVariable("FAKE_ENCODER_PID_FILE");
        if (!string.IsNullOrEmpty(pidFile)) await File.AppendAllTextAsync(pidFile, $"{Environment.ProcessId}\n");
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            await Console.Out.WriteLineAsync("FakeEncoder 1.0");
            return 0;
        }
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            string? helpFile = Environment.GetEnvironmentVariable("FAKE_ENCODER_HELP_FILE");
            if (!string.IsNullOrEmpty(helpFile))
            {
                await Console.Out.WriteAsync(await File.ReadAllTextAsync(helpFile));
                return 0;
            }
            await Console.Out.WriteLineAsync("  --vpp-nlmeans [params]\n    非局所平均によるノイズ除去。\n    sigma=<float> 強さ\n  --vpp-edgelevel [params]\n    エッジの調整。\n  --vpp-deband [params]\n    バンディングの低減。\n  --vpp-yadif [params]\n    インターレースの解除。");
            return 0;
        }
        if (!args.Contains("--preview-server", StringComparer.Ordinal))
        {
            return await EncodeAsync(args, CancellationToken.None);
        }
        if (Environment.GetEnvironmentVariable("FAKE_ENCODER_READY_NONE") != "1")
        {
            await WriteLineAsync("RGYPREVIEW READY version=1 encoder=fake");
        }
        var channel = Channel.CreateBounded<Request>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var worker = RunServerAsync(channel.Reader);
        try
        {
            while (await Console.In.ReadLineAsync() is string line)
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                string? command = root.GetProperty("cmd").GetString();
                if (command == "quit") break;
                long id = root.GetProperty("id").GetInt64();
                lock (Sync)
                {
                    if (command == "cancel")
                    {
                        if (_currentId == id) _currentCancellation?.Cancel();
                        else _cancelledPending = id;
                    }
                    else if (command == "run")
                    {
                        _currentCancellation?.Cancel();
                        string[] runArgs = root.GetProperty("args").EnumerateArray().Select(x => x.GetString()!).ToArray();
                        channel.Writer.TryWrite(new Request(id, runArgs, ++_sequence));
                    }
                }
            }
        }
        catch (JsonException)
        {
            await Console.Error.WriteLineAsync("FAKE malformed request");
        }
        finally
        {
            lock (Sync) _currentCancellation?.Cancel();
            channel.Writer.Complete();
        }
        await worker;
        return 0;
    }

    private static async Task RunServerAsync(ChannelReader<Request> reader)
    {
        int completed = 0;
        await foreach (var request in reader.ReadAllAsync())
        {
            using var cancellation = new CancellationTokenSource();
            lock (Sync)
            {
                if (request.Sequence != _sequence) continue;
                _currentId = request.Id;
                _currentCancellation = cancellation;
                if (_cancelledPending == request.Id) cancellation.Cancel();
            }
            await Console.Error.WriteLineAsync($"RGYPREVIEW BEGIN id={request.Id}");
            await WriteLineAsync($"RGYPREVIEW BEGIN id={request.Id}");
            int rc = await EncodeAsync(request.Args, cancellation.Token);
            await Console.Error.WriteLineAsync($"RGYPREVIEW END id={request.Id} rc={rc}");
            await WriteLineAsync($"RGYPREVIEW END id={request.Id} rc={rc}");
            lock (Sync)
            {
                _currentCancellation = null;
                _currentId = 0;
            }
            int dropAfter = EnvInt("FAKE_ENCODER_DROP_AFTER", 0);
            if (dropAfter > 0 && ++completed >= dropAfter) Environment.Exit(71);
        }
    }

    private static async Task<int> EncodeAsync(string[] args, CancellationToken cancellationToken)
    {
        int count = Number(args, "--frames", 1);
        int step = Math.Max(1, Number(args, "--fake-step", args.Contains("--vpp-decimate", StringComparer.Ordinal) ? 2 : 1));
        string? outputRate = Value(args, "--fake-output-rate");
        if (outputRate is not (null or "double" or "film" or "vfr"))
        {
            await Console.Error.WriteLineAsync("FAKE invalid output rate");
            return -24;
        }
        bool noDuration = args.Contains("--fake-no-xdur", StringComparer.Ordinal);
        int outputFps = outputRate is "double" or "vfr" ? 60 : outputRate == "film" ? 24 : 30;
        int width = 64, height = 36;
        string? outputSize = Value(args, "--output-res");
        if (outputSize != null)
        {
            string[] dimensions = outputSize.Split('x');
            if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out width) || !int.TryParse(dimensions[1], out height)
                || width <= 0 || height <= 0 || width > 8192 || height > 8192)
            {
                await Console.Error.WriteLineAsync("FAKE invalid output size");
                return -24;
            }
        }
        string sar = Value(args, "--fake-sar") ?? "1:1";
        int hangAfter = Number(args, "--fake-hang-after", -1);
        int crashAt = Number(args, "--fake-crash-at", -1);
        int badControlAt = Number(args, "--fake-bad-control-at", -1);
        int delayMs = Number(args, "--fake-delay-ms", 0);
        int rc = Number(args, "--fake-rc", 0);
        int gateAfter = Number(args, "--fake-gate-after", 0);
        string? gate = Value(args, "--fake-gate");
        bool ignoreCancel = args.Contains("--fake-ignore-cancel", StringComparer.Ordinal);
        CancellationToken effectiveToken = ignoreCancel ? CancellationToken.None : cancellationToken;
        var vpp = new List<string>();
        bool collecting = false;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal)) collecting = arg.StartsWith("--vpp-", StringComparison.Ordinal);
            if (collecting) vpp.Add(arg);
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", vpp)));
        double seek = ParseSeek(Value(args, "--seek"));
        await Console.Error.WriteLineAsync($"FAKEPID {Environment.ProcessId} seek={seek.ToString(CultureInfo.InvariantCulture)}");
        string? duration = Value(args, "--fake-duration");
        bool durationAtExit = args.Contains("--fake-duration-at-exit", StringComparer.Ordinal);
        if (duration != null && !durationAtExit) await Console.Error.WriteLineAsync($"  Duration: {duration}, start: 0.000000, bitrate: 1000 kb/s");
        // ログ到着後に偽時計を進めるテスト用の同期点。
        string? logReadyFile = Environment.GetEnvironmentVariable("FAKE_ENCODER_LOG_READY_FILE");
        if (!string.IsNullOrEmpty(logReadyFile)) await File.WriteAllTextAsync(logReadyFile, "");
        int stderrBytes = Number(args, "--fake-stderr-bytes", 0);
        if (stderrBytes > 0) await Console.Error.WriteLineAsync(new string('x', stderrBytes));
        if (rc != 0) await Console.Error.WriteLineAsync($"FAKE requested rc={rc}");
        try
        {
            if (count > 0)
            {
                await WriteLineAsync($"YUV4MPEG2 W{width} H{height} F{outputFps}:1 Ip A{sar} C420jpeg XTIMEBASE=1:{outputFps} XPID={Environment.ProcessId} XSEEK={seek.ToString(CultureInfo.InvariantCulture)}");
            }
            foreach (var frame in OutputFrames(count, step, seek, outputRate, outputFps))
            {
                int i = frame.Index;
                effectiveToken.ThrowIfCancellationRequested();
                if (badControlAt == i)
                {
                    await WriteLineAsync("RGYPREVIEW BROKEN");
                    await Task.Delay(Timeout.Infinite, effectiveToken);
                }
                if (hangAfter >= 0 && i >= hangAfter) await Task.Delay(Timeout.Infinite, effectiveToken);
                if (gate != null && i >= gateAfter) await WaitForFileAsync(gate, effectiveToken);
                if (delayMs > 0) await Task.Delay(delayMs, effectiveToken);
                string timestamp = frame.Time.ToString(outputRate == null ? "F9" : "R", CultureInfo.InvariantCulture);
                string durationTag = noDuration ? "" : string.Create(CultureInfo.InvariantCulture, $" Xdur={frame.Duration:F9}");
                await WriteLineAsync($"FRAME Xts={timestamp}{durationTag}");
                var data = new byte[width * height + 2 * ((width + 1) / 2) * ((height + 1) / 2)];
                for (int j = 0; j < data.Length; j++) data[j] = (byte)(hash[j % hash.Length] + i);
                if (crashAt == i)
                {
                    await Output.WriteAsync(data.AsMemory(0, data.Length / 2));
                    await Output.FlushAsync();
                    Environment.Exit(72);
                }
                // フレーム単位で書き切り、中断でバイナリ境界が壊れないようにする。
                await Output.WriteAsync(data);
                await Output.FlushAsync();
            }
            if (durationAtExit)
            {
                string? exitGate = Value(args, "--fake-duration-exit-gate");
                string? ready = Environment.GetEnvironmentVariable("FAKE_ENCODER_DURATION_EXIT_READY_FILE");
                if (!string.IsNullOrEmpty(ready)) await File.WriteAllTextAsync(ready, "");
                if (exitGate != null) await WaitForFileAsync(exitGate, effectiveToken);
                if (duration != null) await Console.Error.WriteLineAsync($"  Duration: {duration}, start: 0.000000, bitrate: 1000 kb/s");
            }
            return rc;
        }
        catch (OperationCanceledException)
        {
            return -12;
        }
    }

    // レート変更時も--framesは入力枚数で数え、seekをまたいだ時刻と画素の対応を保つ。
    private static IEnumerable<(int Index, double Time, double Duration)> OutputFrames(int count, int step,
        double seek, string? rate, int fps)
    {
        if (rate == null)
        {
            for (int i = 0; i < count; i += step) yield return (i, i / 30.0, step / 30.0);
            yield break;
        }
        double Timestamp(int index)
        {
            if (rate != "vfr") return index / (double)fps;
            // 最短間隔は1/60秒。1周期に2・3・1・4tickを並べた不規則な時刻列。
            int[] offsets = [0, 2, 5, 6];
            return (index / 4 * 10 + offsets[index % 4]) / 60.0;
        }
        int first = rate == "vfr" ? (int)Math.Floor(seek * 6) * 4 : (int)Math.Floor(seek * fps);
        while (Timestamp(first) < seek - 1e-9) ++first;
        double end = seek + count / 30.0;
        for (int i = first; Timestamp(i) < end - 1e-9; i += step)
            yield return (i, Timestamp(i) - seek, Timestamp(i + step) - Timestamp(i));
    }

    private static async Task WaitForFileAsync(string path, CancellationToken token)
    {
        if (File.Exists(path)) return;
        string fullPath = Path.GetFullPath(path);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => ready.TrySetResult();
        watcher.Renamed += (_, _) => { if (File.Exists(fullPath)) ready.TrySetResult(); };
        watcher.EnableRaisingEvents = true;
        // 監視を有効にする前後で作られたファイルも取りこぼさない。
        if (File.Exists(fullPath)) return;
        await ready.Task.WaitAsync(token);
    }

    private static async Task WriteLineAsync(string line)
    {
        await Output.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"));
        await Output.FlushAsync();
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Number(string[] args, string name, int fallback) =>
        int.TryParse(Value(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static double ParseSeek(string? text)
    {
        if (text == null) return 0;
        double result = 0;
        foreach (string part in text.Split(':')) result = result * 60 + double.Parse(part, CultureInfo.InvariantCulture);
        return result;
    }
}

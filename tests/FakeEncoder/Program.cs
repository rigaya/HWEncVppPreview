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
        int step = Math.Max(1, Number(args, "--fake-step", 1));
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
                await WriteLineAsync($"YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420jpeg XTIMEBASE=1:30 XPID={Environment.ProcessId} XSEEK={seek.ToString(CultureInfo.InvariantCulture)}");
            }
            for (int i = 0; i < count; i += step)
            {
                effectiveToken.ThrowIfCancellationRequested();
                if (badControlAt == i)
                {
                    await WriteLineAsync("RGYPREVIEW BROKEN");
                    await Task.Delay(Timeout.Infinite, effectiveToken);
                }
                if (hangAfter >= 0 && i >= hangAfter) await Task.Delay(Timeout.Infinite, effectiveToken);
                if (gate != null && i >= gateAfter) await WaitForFileAsync(gate, effectiveToken);
                if (delayMs > 0) await Task.Delay(delayMs, effectiveToken);
                await WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"FRAME Xts={i / 30.0:F9} Xdur={1.0 / 30:F9}"));
                var data = new byte[64 * 36 * 3 / 2];
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
            return rc;
        }
        catch (OperationCanceledException)
        {
            return -12;
        }
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

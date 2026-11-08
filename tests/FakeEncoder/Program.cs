using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace FakeEncoder;

/// <summary>実GPUなしでパイプ出力と中断・障害を再現する。</summary>
internal static class Program
{
    private sealed record Request(long Id, string[] Args, long Sequence, bool IsProbe = false);
    private static readonly Stream Output = Console.OpenStandardOutput();
    private static readonly object Sync = new();
    private static CancellationTokenSource? _currentCancellation;
    private static long _currentId;
    private static long _sequence;
    private static long _cancelledPending = -1;
    private static long _logSequence, _lastLogTimestamp;

    // 別プロセスのappendで受信記録を失わないよう、共有baseごとにPID別ファイルへ出す。
    private static string ProcessLogPath(string path) => Path.Combine(Path.GetDirectoryName(path) ?? "",
        Path.GetFileNameWithoutExtension(path) + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(path));

    private static async Task WriteCommandLogAsync(string path, JsonElement root)
    {
        var command = root.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
        long sequence = ++_logSequence;
        // 時計が戻っても同じPID内の受信順を保ち、PID間の集約にはUTCの時刻を使う。
        long timestamp = _lastLogTimestamp = Math.Max(DateTime.UtcNow.Ticks, _lastLogTimestamp + 1);
        command["_fakeLogPid"] = JsonSerializer.SerializeToElement(Environment.ProcessId);
        command["_fakeLogSequence"] = JsonSerializer.SerializeToElement(sequence);
        command["_fakeLogTimestamp"] = JsonSerializer.SerializeToElement(timestamp);
        await File.AppendAllTextAsync(ProcessLogPath(path), JsonSerializer.Serialize(command) + "\n");
    }

    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Contains("--fake-hold-output-child", StringComparer.Ordinal))
            return await HoldOutputChildAsync();
        string? pidFile = Environment.GetEnvironmentVariable("FAKE_ENCODER_PID_FILE");
        if (!string.IsNullOrEmpty(pidFile)) await File.AppendAllTextAsync(ProcessLogPath(pidFile), $"{Environment.ProcessId}\n");
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
            if (Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES") == "1")
            {
                await Console.Out.WriteLineAsync("  --timestamp-passthrough  入力原点付きの時刻を出力する。\n  --opencl-task-threads <int>  テストでは受理するだけで画像を変えない。");
            }
            if (Environment.GetEnvironmentVariable("FAKE_ENCODER_COLOR_CAPABILITIES") == "1")
            {
                await Console.Out.WriteLineAsync("  --colormatrix <string>  入力の色行列を引き継ぐ。\n  --colorprim <string>  入力の原色を引き継ぐ。\n  --transfer <string>  入力の伝達特性を引き継ぐ。\n  --colorrange <string>  入力の色範囲を引き継ぐ。\n  --chromaloc <string>  入力の色差の位置を引き継ぐ。");
            }
            return 0;
        }
        if (!args.Contains("--preview-server", StringComparer.Ordinal))
        {
            int result = await EncodeAsync(args, CancellationToken.None);
            // 指定時だけ、都度起動でも親終了後のpipe保持を再現する。
            if (result == 0) await StartHoldOutputChildAsync();
            return result;
        }
        if (Environment.GetEnvironmentVariable("FAKE_ENCODER_READY_NONE") != "1")
        {
            string encoder = Environment.GetEnvironmentVariable("FAKE_ENCODER_READY_ENCODER") ?? "fake";
            await WriteLineAsync("RGYPREVIEW READY version=1" + (encoder.Length == 0 ? "" : " encoder=" + encoder)
                + (Environment.GetEnvironmentVariable("FAKE_ENCODER_PROBE_SUPPORTED") == "1" ? " probe=1" : "")
                + (Environment.GetEnvironmentVariable("FAKE_ENCODER_WINDOW_SEEK") is string windowSeek ? " window_seek=" + windowSeek : "")
                + (Environment.GetEnvironmentVariable("FAKE_ENCODER_OUTPUT_START") is string outputStart ? " output_start=" + outputStart : ""));
        }
        var channel = Channel.CreateBounded<Request>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var worker = RunServerAsync(channel.Reader);
        bool receivedQuit = false;
        try
        {
            while (await Console.In.ReadLineAsync() is string line)
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                string? command = root.GetProperty("cmd").GetString();
                string? commandLog = Environment.GetEnvironmentVariable("FAKE_ENCODER_COMMAND_LOG_FILE");
                if (!string.IsNullOrEmpty(commandLog)) await WriteCommandLogAsync(commandLog, root);
                if (command == "quit")
                {
                    if (Environment.GetEnvironmentVariable("FAKE_ENCODER_IGNORE_QUIT") == "1") continue;
                    receivedQuit = true;
                    break;
                }
                long id = root.GetProperty("id").GetInt64();
                lock (Sync)
                {
                    if (command == "cancel")
                    {
                        if (_currentId == id) _currentCancellation?.Cancel();
                        else _cancelledPending = id;
                    }
                    else if (command == "probe")
                    {
                        string[] probeArgs = root.GetProperty("args").EnumerateArray().Select(x => x.GetString()!).ToArray();
                        channel.Writer.TryWrite(new Request(id, probeArgs, _sequence, IsProbe: true));
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
        if (receivedQuit) await StartHoldOutputChildAsync();
        return 0;
    }

    // 親の正常終了後もパイプを保持し、回帰テストがEOF待ちを再現できるようにする。
    private static async Task StartHoldOutputChildAsync()
    {
        string? ready = Environment.GetEnvironmentVariable("FAKE_ENCODER_HOLD_OUTPUT_READY_FILE");
        string? release = Environment.GetEnvironmentVariable("FAKE_ENCODER_HOLD_OUTPUT_RELEASE_FILE");
        if (string.IsNullOrEmpty(ready) || string.IsNullOrEmpty(release)) return;
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("実行パスがありません"))
        {
            UseShellExecute = false,
            // リダイレクトせず親の標準出力・標準エラーのパイプを継承する。
            RedirectStandardInput = false, RedirectStandardOutput = false, RedirectStandardError = false,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--fake-hold-output-child");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("パイプ保持用の子を起動できませんでした");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitForFileAsync(ready, timeout.Token);
        }
        catch
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            throw;
        }
    }

    private static async Task<int> HoldOutputChildAsync()
    {
        string? ready = Environment.GetEnvironmentVariable("FAKE_ENCODER_HOLD_OUTPUT_READY_FILE");
        string? release = Environment.GetEnvironmentVariable("FAKE_ENCODER_HOLD_OUTPUT_RELEASE_FILE");
        if (string.IsNullOrEmpty(ready) || string.IsNullOrEmpty(release)) return -24;
        // テスト側が途中で失敗しても無期限の孤児にしない。通常はreleaseで直ちに終了する。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string temporaryReady = ready + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        await File.WriteAllTextAsync(temporaryReady, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        File.Move(temporaryReady, ready, overwrite: true);
        try
        {
            await WaitForFileAsync(release, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
        string? exited = Environment.GetEnvironmentVariable("FAKE_ENCODER_HOLD_OUTPUT_EXIT_FILE");
        if (!string.IsNullOrEmpty(exited)) await File.WriteAllTextAsync(exited, "0");
        return 0;
    }

    private static async Task RunServerAsync(ChannelReader<Request> reader)
    {
        int completed = 0;
        await foreach (var request in reader.ReadAllAsync())
        {
            // 実サーバと同様に、先行runのENDを送信してからprobeへ進む。
            if (request.IsProbe) { await ProbeAsync(request); continue; }
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

    private static async Task ProbeAsync(Request request)
    {
        if (Environment.GetEnvironmentVariable("FAKE_ENCODER_PROBE_NONE") == "1") return;
        int delay = EnvInt("FAKE_ENCODER_PROBE_DELAY_MS", 0);
        if (delay > 0) await Task.Delay(delay);
        string? reply = Environment.GetEnvironmentVariable("FAKE_ENCODER_PROBE_REPLY");
        if (reply != null)
        {
            await WriteLineAsync(reply.Replace("{id}", request.Id.ToString(CultureInfo.InvariantCulture)));
            return;
        }
        // これは通信回帰専用の簡易判定。実フィルタの監査結果はNativeのprobeで検証する。
        int history = EnvInt("FAKE_ENCODER_PROBE_HISTORY", -1);
        if (history < 0)
        {
            history = 0;
            for (int index = 0; index < request.Args.Length; index++)
            {
                string option = request.Args[index];
                string argument = index + 1 < request.Args.Length ? request.Args[index + 1] : "";
                if (option is "--vpp-kfm" or "--vpp-afs" or "--vpp-deband" or "--vpp-unknown") history = 1;
                if (option is "--vpp-nlmeans" or "--vpp-knn")
                {
                    foreach (string parameter in argument.Split(','))
                        if (parameter.StartsWith("d=", StringComparison.Ordinal)
                            && parameter[2..] != "0") history = 1;
                }
                if (option == "--vpp-resize" && !argument.StartsWith("algo=spline36", StringComparison.Ordinal)) history = 1;
            }
        }
        int rc = EnvInt("FAKE_ENCODER_PROBE_RC", 0);
        await WriteLineAsync($"RGYPREVIEW PROBE id={request.Id} history={history} rc={rc}");
    }

    private static async Task<int> EncodeAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Contains("--y4m", StringComparer.Ordinal))
        {
            string? ready = Environment.GetEnvironmentVariable("FAKE_ENCODER_INITIALIZE_READY");
            string? release = Environment.GetEnvironmentVariable("FAKE_ENCODER_INITIALIZE_RELEASE");
            if (ready != null)
            {
                string temporary = ready + ".tmp";
                await File.WriteAllTextAsync(temporary, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                File.Move(temporary, ready, overwrite: true);
            }
            try { if (release != null) await WaitForFileAsync(release, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return -12; }
            int initializationRc = EnvInt("FAKE_ENCODER_INITIALIZE_RC", 0);
            if (initializationRc != 0) return initializationRc;
        }
        // --framesを省いた要求だけを無限にし、既存の有限要求の意味は変えない。
        int? count = Array.IndexOf(args, "--frames") >= 0 ? Number(args, "--frames", 1) : null;
        bool passthrough = args.Contains("--timestamp-passthrough", StringComparer.Ordinal);
        bool zeroOrigin = args.Contains("--fake-zero-origin", StringComparer.Ordinal)
            || args.Contains("--fake-ignore-passthrough", StringComparer.Ordinal);
        string? originText = Value(args, "--fake-origin-seconds");
        double origin = 1000;
        if (originText != null && (!double.TryParse(originText, NumberStyles.Float, CultureInfo.InvariantCulture, out origin)
            || !double.IsFinite(origin)))
        {
            await Console.Error.WriteLineAsync("FAKE invalid timestamp origin");
            return -24;
        }
        string? endText = Value(args, "--fake-input-end");
        double? inputEnd = null;
        if (endText != null)
        {
            if (!double.TryParse(endText, NumberStyles.Float, CultureInfo.InvariantCulture, out double end)
                || !double.IsFinite(end) || end < 0)
            {
                await Console.Error.WriteLineAsync("FAKE invalid input end");
                return -24;
            }
            inputEnd = end;
        }
        // 新しい時計モードでは画素番号も入力位置に合わせる。旧相対モードの期待値は維持する。
        int clockBackstepAt = Number(args, "--fake-clock-backstep-at", -1);
        int clockJumpAt = Number(args, "--fake-clock-jump-at", -1);
        bool clockFaultConfigured = clockBackstepAt > 0 || clockJumpAt > 0;
        bool globalMediaFrames = passthrough || zeroOrigin || originText != null || clockFaultConfigured;
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
        // 色変換のテストでは、画素とは独立に出力ヘッダの色情報を指定できる。
        var colorTags = new List<string>();
        foreach (var (option, tag) in new[]
        {
            ("--fake-colormatrix", "XCOLORMATRIX"), ("--fake-colorprim", "XCOLORPRIM"),
            ("--fake-transfer", "XTRANSFER"), ("--fake-colorrange", "XCOLORRANGE"),
            ("--fake-chromaloc", "XCHROMALOC")
        })
        {
            if (Value(args, option) is string value) colorTags.Add(tag + "=" + (tag == "XCOLORRANGE" ? value.ToUpperInvariant() : value));
        }
        string colorExtensions = colorTags.Count == 0 ? "" : " " + string.Join(" ", colorTags);
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
        // 試験指定時だけ開始位置で画素を変え、時計を壊さず別取得のstitch拒否を再現する。
        long seekPixelOffset = args.Contains("--fake-seek-pixel-offset", StringComparer.Ordinal)
            ? (long)Math.Round(seek * 30) : 0;
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
            if (count == null || count > 0)
            {
                await WriteLineAsync($"YUV4MPEG2 W{width} H{height} F{outputFps}:1 Ip A{sar} C420jpeg XTIMEBASE=1:{outputFps} XPID={Environment.ProcessId} XSEEK={seek.ToString(CultureInfo.InvariantCulture)}{colorExtensions}");
            }
            double? firstTime = null;
            double? previousTimestamp = null;
            long outputOrdinal = 0;
            foreach (var frame in OutputFrames(count, step, seek, outputRate, outputFps, globalMediaFrames, inputEnd))
            {
                long i = frame.Index;
                firstTime ??= frame.Time;
                effectiveToken.ThrowIfCancellationRequested();
                if (badControlAt == i)
                {
                    await WriteLineAsync("RGYPREVIEW BROKEN");
                    await Task.Delay(Timeout.Infinite, effectiveToken);
                }
                if (hangAfter >= 0 && i >= hangAfter) await Task.Delay(Timeout.Infinite, effectiveToken);
                if (gate != null && i >= gateAfter) await WaitForFileAsync(gate, effectiveToken);
                if (delayMs > 0) await Task.Delay(delayMs, effectiveToken);
                // 時計だけを変え、ゼロ起点の異常モードでも同じ入力位置の画素を返す。
                double timestampValue = zeroOrigin ? frame.Time - firstTime.Value
                    : passthrough ? origin + seek + frame.Time : frame.Time;
                ++outputOrdinal;
                // 枚数は入力indexではなく要求内の出力順。再取得時は画素を保ち時計だけ正常に戻す。
                if (passthrough)
                {
                    if (clockJumpAt > 0 && outputOrdinal >= clockJumpAt) timestampValue += 10;
                    if (outputOrdinal == clockBackstepAt && previousTimestamp is double previous)
                        timestampValue = previous - 2 * frame.Duration;
                }
                else if (clockFaultConfigured) timestampValue = frame.Time - firstTime.Value;
                previousTimestamp = timestampValue;
                string timestamp = timestampValue.ToString(outputRate == null ? "F9" : "R", CultureInfo.InvariantCulture);
                string durationTag = noDuration ? "" : string.Create(CultureInfo.InvariantCulture, $" Xdur={frame.Duration:F9}");
                await WriteLineAsync($"FRAME Xts={timestamp}{durationTag}");
                var data = new byte[width * height + 2 * ((width + 1) / 2) * ((height + 1) / 2)];
                for (int j = 0; j < data.Length; j++) data[j] = (byte)(hash[j % hash.Length] + i + seekPixelOffset);
                if (crashAt == i)
                {
                    await Output.WriteAsync(data.AsMemory(0, data.Length / 2));
                    await Output.FlushAsync();
                    Environment.Exit(72);
                }
                // 指定した要求内の枚数で画素途中を止め、実Readerの所有とread待ちを試験する。
                // 入力probeは除外し、seek後の絶対indexにも依存しない。
                string? bodyGate = Value(args, "--fake-body-gate");
                if (bodyGate != null && Value(args, "--log-level") != "input=debug"
                    && outputOrdinal == Number(args, "--fake-body-gate-at", 2))
                {
                    int split = data.Length / 2;
                    await Output.WriteAsync(data.AsMemory(0, split));
                    await Output.FlushAsync();
                    string ready = bodyGate + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".ready";
                    string temporary = ready + ".tmp";
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
                    {
                        pid = Environment.ProcessId, input = Value(args, "-i"), seek, outputOrdinal, index = i,
                    }));
                    File.Move(temporary, ready, overwrite: true);
                    try { await WaitForFileAsync(bodyGate, effectiveToken); }
                    finally
                    {
                        // Serverの取消でも残半を送り切り、続くENDを画素へ混入させない。
                        await Output.WriteAsync(data.AsMemory(split));
                        await Output.FlushAsync();
                    }
                    continue;
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
    // 無限出力も1枚ずつ生成してawaitで書くため、未読の画像をメモリへ積み上げない。
    private static IEnumerable<(long Index, double Time, double Duration)> OutputFrames(int? count, int step,
        double seek, string? rate, int fps, bool globalMediaFrames, double? inputEnd)
    {
        if (rate == null && !globalMediaFrames)
        {
            for (long i = 0; !count.HasValue || i < count.Value; i += step)
            {
                double timestamp = i / 30.0;
                if (inputEnd is double end && seek + timestamp >= end - 1e-9) yield break;
                yield return (i, timestamp, step / 30.0);
            }
            yield break;
        }
        double Timestamp(long index)
        {
            if (rate != "vfr") return index / (double)fps;
            // 最短間隔は1/60秒。1周期に2・3・1・4tickを並べた不規則な時刻列。
            int[] offsets = [0, 2, 5, 6];
            return (index / 4 * 10 + offsets[index % 4]) / 60.0;
        }
        long first = rate == "vfr" ? (long)Math.Floor(seek * 6) * 4 : (long)Math.Floor(seek * fps);
        while (Timestamp(first) < seek - 1e-9) ++first;
        double limit = count.HasValue ? seek + count.Value / 30.0 : double.PositiveInfinity;
        if (inputEnd is double endTime) limit = Math.Min(limit, endTime);
        for (long i = first; Timestamp(i) < limit - 1e-9; i += step)
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

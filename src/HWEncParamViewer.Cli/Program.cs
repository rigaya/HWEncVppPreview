using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.Cli;

/// <summary>GUIと同じController経路で画像を選択するヘッドレス実行入口。</summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await RunAsync(args, Console.Out, cancellationToken: cancellation.Token).ConfigureAwait(false); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    /// <summary>結果JSONを指定ライターへ1行出力する。時間依存処理は注入した時計を使う。</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var result = new CliResult();
        try
        {
            var options = Options.Parse(args);
            await ExecuteAsync(options, result, timeProvider ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            result.ExitCode = 2;
            result.Rc = 2;
            result.Error = exception.Message;
        }
        catch (InputProbeException exception)
        {
            result.ExitCode = 1;
            result.Rc = exception.ExitCode;
            result.Error = exception.Message;
            result.StderrTail = Tail(exception.StandardError);
        }
        catch (Exception exception)
        {
            result.ExitCode = 3;
            result.Rc = 3;
            result.Error = exception.Message;
            result.TimedOut = exception is TimeoutException;
            if (exception is PreviewBackendException backendException) result.StderrTail = Tail(backendException.StandardError);
            else if (exception is InputProbeTimeoutException probeTimeout) result.StderrTail = Tail(probeTimeout.StandardError);
        }
        finally
        {
            result.ElapsedMs = watch.Elapsed.TotalMilliseconds;
        }
        // エンコーダのログやバイナリはstdoutへ流さず、この1行だけを出力する。
        await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
        return result.ExitCode;
    }

    private static async Task ExecuteAsync(Options options, CliResult result, TimeProvider clock, CancellationToken cancellationToken)
    {
        var capabilities = await ReadHelpAsync(options.Encoder, clock, cancellationToken).ConfigureAwait(false);
        await using var probe = new InputProbe(timeProvider: clock, timeout: options.Watchdog);
        var input = await probe.ProbeAsync(options.Encoder, options.Input, options.InputArgs, cancellationToken, capabilities).ConfigureAwait(false);
        var request = new PreviewRequest
        {
            EncoderPath = options.Encoder, InputFile = options.Input, InputArgs = options.InputArgs,
            VppArgs = options.VppArgs, TargetSeconds = options.Time, PreMarginSeconds = options.PreMargin,
            PostMarginSeconds = options.PostMargin, Fps = input.Fps,
            Capabilities = capabilities, TimestampOriginSeconds = input.TimestampOriginSeconds,
            TimestampOriginTrusted = input.TimestampOriginTrusted,
            InputDurationSeconds = input.Duration?.TotalSeconds,
        };
        var plan = CommandBuilder.Plan(request);
        if (plan.FrameCount <= 0 || (plan.ActualPreMargin + options.PostMargin) * input.Fps > int.MaxValue - 1)
            throw new ArgumentException("要求区間のフレーム数が大きすぎます");
        result.InputFps = input.Fps;
        result.SeekSeconds = plan.SeekSeconds;
        result.Frames = plan.FrameCount;
        var recording = new Recording();
        IPreviewBackend preferred = options.Mode == "oneshot"
            ? new RecordingBackend(new OneShotBackend(), "oneshot", recording)
            : new RecordingBackend(new ServerBackend(timeProvider: clock), "server", recording);
        IPreviewBackend fallback = options.Mode == "auto"
            ? new RecordingBackend(new OneShotBackend(), "oneshot", recording) : preferred;
        await using var controller = new PreviewController(preferred, fallback, cache: new FrameCache(0),
            timeProvider: clock, debounce: TimeSpan.Zero, watchdog: options.Watchdog, enableFallback: options.Mode == "auto");
        PreviewFrameResult? selected = null;
        PreviewCompletion? completion = null;
        PreviewFailure? failure = null;
        controller.FrameReady += value => selected = value;
        controller.Completed += value => completion = value;
        controller.Failed += value => failure = value;
        controller.Submit(request);
        await controller.WaitForIdleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        // replayで取り直した完了列だけを採用し、初回の異常出力をdumpへ連結しない。
        if (completion is { Frames.Count: > 0 })
        {
            recording.Frames.Clear();
            recording.Frames.AddRange(completion.Frames);
            recording.Header = recording.Frames[0].Header;
        }
        var winningSegment = completion?.Segment ?? selected?.Segment;
        var physical = completion?.Segment?.PhysicalRequest ?? recording.PhysicalRequest ?? selected?.Segment?.PhysicalRequest;
        result.TimestampPassthrough = physical?.Capabilities.TimestampPassthrough == true;
        result.TrustedAbsolute = winningSegment?.Clock.TrustedAbsolute == true && result.TimestampPassthrough;
        result.TimestampOriginSeconds = physical?.TimestampOriginSeconds;
        // 実際に適用した暖機と安全範囲を、画素照合用ドライバから確認できるようにする。
        result.WarmupSeconds = physical?.WarmupSeconds;
        result.SafeStartSeconds = winningSegment?.SafeStartSeconds;
        result.SafeEndSeconds = winningSegment?.SafeEndSeconds;
        result.FirstOutputXts = recording.Frames.FirstOrDefault()?.Xts;
        // 暖機やlegacy再取得で変わった場合も、JSONは実際に取得した計画を返す。
        if (physical != null)
        {
            var actualPlan = CommandBuilder.Plan(physical);
            result.SeekSeconds = actualPlan.SeekSeconds;
            result.Frames = actualPlan.FrameCount;
            double seek = actualPlan.SeekSeconds ?? 0;
            result.PlannedSafeStartSeconds = seek > 0 ? seek + physical.WarmupSeconds : 0;
        }
        result.Mode = recording.Mode;
        result.FrameCount = recording.Frames.Count;
        var header = recording.Header;
        if (header != null)
        {
            result.Width = header.Width;
            result.Height = header.Height;
            result.Chroma = Chroma(header.Chroma);
            result.BitDepth = header.BitDepth;
        }
        var selectedFrame = selected?.Frame;
        int selectedIndex = selectedFrame == null ? -1 : recording.Frames.FindIndex(frame => ReferenceEquals(frame, selectedFrame));
        if (selectedIndex < 0) selected = null;
        result.SelectedIndex = selectedIndex < 0 ? null : selectedIndex;
        result.SelectedXts = selected?.Frame.Xts;
        result.TargetNotReached = completion?.TargetNotReached ?? selected?.TargetNotReached ?? false;
        if (failure != null)
        {
            result.ExitCode = failure.Result == null ? 3 : 1;
            result.Rc = failure.Result?.ExitCode ?? 3;
            result.Error = failure.Reason;
            result.TimedOut = failure.TimedOut || failure.Exception is TimeoutException;
            string log = failure.Diagnostic ?? failure.Result?.StandardError ?? (failure.Exception as PreviewBackendException)?.StandardError
                ?? recording.Result?.StandardError ?? "";
            result.StderrTail = Tail(log);
            return;
        }
        if (completion == null || selected == null)
        {
            result.ExitCode = 3;
            result.Rc = 3;
            result.Error = "表示するフレームを選択できませんでした";
            result.StderrTail = Tail(recording.Result?.StandardError ?? "");
            return;
        }
        result.Rc = completion.Result.ExitCode;
        result.Sha256 = Convert.ToHexStringLower(SHA256.HashData(selected.Frame.Data));
        if (options.Dump != null) await File.WriteAllBytesAsync(options.Dump, selected.Frame.Data, cancellationToken).ConfigureAwait(false);
        if (options.DumpY4m != null) await DumpY4mAsync(options.DumpY4m, recording.Frames, input.Fps, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PreviewCapabilities> ReadHelpAsync(string encoder, TimeProvider clock, CancellationToken token)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(encoder)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            process.StartInfo.ArgumentList.Add("--help");
            process.Start();
            using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5), clock, token).ConfigureAwait(false); }
            finally
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            if (process.ExitCode != 0) return new();
            string help = await stdout.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(help)) help = await stderr.ConfigureAwait(false);
            return CommandBuilder.CapabilitiesFromHelp(new HelpIndex(help));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return new(); }
    }

    private static string Tail(string text) => text.Length <= 8192 ? text : text[^8192..];
    private static string Chroma(Y4mChroma chroma) => chroma switch
    {
        Y4mChroma.C420 => "420", Y4mChroma.C422 => "422", Y4mChroma.C444 => "444", _ => "mono",
    };

    private static async Task DumpY4mAsync(string path, IReadOnlyList<Y4mFrame> frames, double requestedFps, CancellationToken token)
    {
        await using var output = File.Create(path);
        if (frames.Count == 0) return;
        var header = frames[0].Header;
        var tags = string.Join(" ", header.ExtTags.Select(pair => pair.Key + (pair.Value.Length == 0 ? "" : "=" + pair.Value)));
        string line = string.Create(CultureInfo.InvariantCulture,
            $"YUV4MPEG2 W{header.Width} H{header.Height} F{header.FpsNum}:{header.FpsDen} I{header.Interlace} A{header.SarNum}:{header.SarDen} C{header.ColorSpaceTag} {tags}\n");
        await output.WriteAsync(Encoding.ASCII.GetBytes(line), token).ConfigureAwait(false);
        for (int i = 0; i < frames.Count; ++i)
        {
            var frame = frames[i];
            double timestamp = frame.Xts ?? i / FrameSelector.FrameRate(frame, requestedFps);
            double duration = FrameSelector.Duration(frame, requestedFps);
            line = string.Create(CultureInfo.InvariantCulture, $"FRAME Xts={timestamp:R} Xdur={duration:R}\n");
            await output.WriteAsync(Encoding.ASCII.GetBytes(line), token).ConfigureAwait(false);
            await output.WriteAsync(frame.Data, token).ConfigureAwait(false);
        }
    }

    private sealed class Recording
    {
        public string? Mode { get; set; }
        public PreviewRequest? PhysicalRequest { get; set; }
        public Y4mHeader? Header { get; set; }
        public List<Y4mFrame> Frames { get; } = new();
        public PreviewRunResult? Result { get; set; }
    }

    /// <summary>バックエンドへ透過的に委譲し、Controllerが選んだ画像と区間全体を対応付ける。</summary>
    private sealed class RecordingBackend(IPreviewBackend inner, string mode, Recording recording) : IPreviewBackend
    {
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken cancellationToken = default)
            => inner.GetHistoryRequirementAsync(request, cancellationToken);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            recording.Mode = mode;
            recording.PhysicalRequest = request;
            recording.Header = null;
            recording.Frames.Clear();
            return recording.Result = await inner.RunAsync(request, new RecordingSink(sink, recording), token).ConfigureAwait(false);
        }
        public ValueTask AbortAsync() => inner.AbortAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        private sealed class RecordingSink(IPreviewSink sink, Recording recording) : IPreviewSink
        {
            public void OnOutput() => sink.OnOutput();
            public ValueTask BeforeReadAsync(CancellationToken token) => sink.BeforeReadAsync(token);
            public void OnReadStateChanged(bool reading) => sink.OnReadStateChanged(reading);
            public void OnHeader(Y4mHeader header) { recording.Header = header; sink.OnHeader(header); }
            public void OnFrame(Y4mFrame frame) { recording.Frames.Add(frame); sink.OnFrame(frame); }
        }
    }

    private sealed class Options
    {
        public string Encoder { get; private set; } = "";
        public string Input { get; private set; } = "";
        public double Time { get; private set; }
        public double PreMargin { get; private set; } = 2;
        public double PostMargin { get; private set; } = 0.5;
        public TimeSpan Watchdog { get; private set; } = TimeSpan.FromSeconds(30);
        public string Mode { get; private set; } = "auto";
        public string[] VppArgs { get; private set; } = Array.Empty<string>();
        public string[] InputArgs { get; private set; } = new[] { "--avhw" };
        public string? Dump { get; private set; }
        public string? DumpY4m { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            bool hasTime = false;
            for (int i = 0; i < args.Length; ++i)
            {
                string option = args[i];
                int equals = option.IndexOf('=');
                string? inline = equals >= 0 ? option[(equals + 1)..] : null;
                if (equals >= 0) option = option[..equals];
                string value = inline ?? (++i < args.Length ? args[i] : throw new ArgumentException($"{option}の値がありません"));
                switch (option)
                {
                    case "--enc": options.Encoder = value; break;
                    case "--input": options.Input = value; break;
                    case "--time": options.Time = ParseTime(value); hasTime = true; break;
                    case "--vpp": options.VppArgs = OptionTextParser.ParseText(value); break;
                    case "--input-args": options.InputArgs = OptionTextParser.ParseText(value); break;
                    case "--pre-margin": options.PreMargin = ParseSeconds(value); break;
                    case "--post-margin": options.PostMargin = ParseSeconds(value); break;
                    case "--watchdog-seconds":
                        double seconds = ParseSeconds(value);
                        if (seconds <= 0) throw new ArgumentException("watchdogは正の秒数が必要です");
                        try { options.Watchdog = TimeSpan.FromSeconds(seconds); }
                        catch (OverflowException exception) { throw new ArgumentException("watchdogの秒数が大きすぎます", exception); }
                        break;
                    case "--mode": options.Mode = value; break;
                    case "--dump": options.Dump = value; break;
                    case "--dump-y4m": options.DumpY4m = value; break;
                    default: throw new ArgumentException($"不明な引数です: {option}");
                }
            }
            if (string.IsNullOrWhiteSpace(options.Encoder) || string.IsNullOrWhiteSpace(options.Input) || !hasTime)
                throw new ArgumentException("--enc、--input、--timeが必要です");
            if (options.Mode is not ("auto" or "server" or "oneshot")) throw new ArgumentException("--modeはauto/server/oneshotのいずれかです");
            return options;
        }

        private static double ParseTime(string value)
        {
            if (!value.Contains(':')) return ParseSeconds(value);
            var parts = value.Split(':');
            if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long hours)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes >= 60)
                throw new ArgumentException("時刻は秒数またはh:mm:ss.fffで指定してください");
            double seconds = ParseSeconds(parts[2]);
            if (seconds >= 60) throw new ArgumentException("時刻の秒は60未満が必要です");
            double total = hours * 3600.0 + minutes * 60.0 + seconds;
            if (total > TimeSpan.MaxValue.TotalSeconds) throw new ArgumentException("時刻が大きすぎます");
            return total;
        }

        private static double ParseSeconds(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || !double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
                throw new ArgumentException("秒数には有限の0以上の値が必要です");
            return seconds;
        }
    }

    private sealed class CliResult
    {
        public int Rc { get; set; }
        public string? Mode { get; set; }
        public int? SelectedIndex { get; set; }
        public double? SelectedXts { get; set; }
        public bool TargetNotReached { get; set; }
        public int FrameCount { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public string? Chroma { get; set; }
        public int? BitDepth { get; set; }
        public string? Sha256 { get; set; }
        public double? SeekSeconds { get; set; }
        public bool TimestampPassthrough { get; set; }
        public bool TrustedAbsolute { get; set; }
        public double? TimestampOriginSeconds { get; set; }
        public double? WarmupSeconds { get; set; }
        public double? SafeStartSeconds { get; set; }
        public double? SafeEndSeconds { get; set; }
        public double? FirstOutputXts { get; set; }
        public double? PlannedSafeStartSeconds { get; set; }
        public double? InputFps { get; set; }
        public int? Frames { get; set; }
        public double ElapsedMs { get; set; }
        public string? Error { get; set; }
        public string? StderrTail { get; set; }
        public bool TimedOut { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public int ExitCode { get; set; }
    }
}

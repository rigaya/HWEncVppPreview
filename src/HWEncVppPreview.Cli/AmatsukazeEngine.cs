using System.Globalization;
using System.Text;
using System.Text.Json;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Cli;

/// <summary>Amatsukazeサーバーと標準入出力で通信するプレビューエンジン。</summary>
public sealed partial class AmatsukazeEngine : IAsyncDisposable
{
    private readonly Stream _input, _output;
    private readonly TextWriter _log;
    private readonly string _tmp, _directory;
    private readonly Dictionary<string, string> _encoders;
    private readonly Func<string, string, IFrameNumberSource> _open;
    private readonly Func<EncoderPreviewSession> _encoderSessionFactory;
    private readonly SemaphoreSlim _serial = new(1, 1), _write = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource _generation = new();
    private readonly Dictionary<long, CancellationTokenSource> _requests = new();
    private readonly List<Task> _tasks = new();
    private FrameNumberController? _controller, _before;
    private CancellationTokenSource? _prefetch;
    private EncoderPreviewSession? _encoderSession;
    private long _configuredGeneration = -1;
    private int _sarNum, _sarDen;
    private Dictionary<string, string> _colorTags = new(StringComparer.Ordinal);
    private long _gen = -1;
    private Task _configured = Task.CompletedTask;
    private bool _closing;

    public AmatsukazeEngine(Stream input, Stream output, TextWriter log, string tmp, string directory,
        Dictionary<string, string> encoders, Func<string, string, IFrameNumberSource>? open = null,
        Func<EncoderPreviewSession>? encoderSessionFactory = null)
    {
        _input = input; _output = output; _log = log; _tmp = Path.GetFullPath(tmp); _directory = Path.GetFullPath(directory);
        _encoders = new(encoders, StringComparer.OrdinalIgnoreCase);
        _open = open ?? ((dir, path) => new AvisynthFrameSource(dir, path));
        _encoderSessionFactory = encoderSessionFactory ?? (() => new EncoderPreviewSession());
    }

    public static async Task<int> RunCommandAsync(string[] args, Stream input, Stream output, TextWriter log)
    {
        try
        {
            string? tmp = null, directory = null; var encoders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i]; if (arg == "--amatsukaze-engine") continue;
                if (i + 1 >= args.Length) throw new ArgumentException($"{arg}の値がありません。");
                string value = args[++i];
                switch (arg) { case "--tmp": tmp = value; break; case "--amatsukaze-dir": directory = value; break;
                    case "--video": if (value != "0") throw new ArgumentException("映像番号0のみ対応しています。"); break;
                    case "--qsvenc": encoders["QSVEnc"] = value; break; case "--nvenc": encoders["NVEnc"] = value; break;
                    case "--vceenc": encoders["VCEEnc"] = value; break; default: throw new ArgumentException($"不明な引数: {arg}"); }
            }
            if (tmp is null || directory is null) throw new ArgumentException("--tmpと--amatsukaze-dirが必要です。");
            await using var engine = new AmatsukazeEngine(input, output, log, tmp, directory, encoders);
            await engine.RunAsync().ConfigureAwait(false); return 0;
        }
        catch (Exception e) { await log.WriteLineAsync(e.Message).ConfigureAwait(false); return 1; }
    }
    private async Task<bool> WriteAsync(string line, byte[]? bytes = null, long? generation = null, CancellationToken cancellationToken = default,
        IReadOnlyList<byte[]>? additionalPayloads = null, Task? superseded = null)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync) { if (_closing || generation is not null && generation != _gen) return false; }
            cancellationToken.ThrowIfCancellationRequested();
            if (superseded?.IsCompleted == true) return false;
            byte[] text = Encoding.UTF8.GetBytes(line + "\n");
            await _output.WriteAsync(text).ConfigureAwait(false);
            if (bytes is not null) await _output.WriteAsync(bytes).ConfigureAwait(false);
            if (additionalPayloads is not null)
                foreach (var payload in additionalPayloads)
                    await _output.WriteAsync(payload).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
            return true;
        }
        finally { _write.Release(); }
    }
    private static string Message(Exception e) => e.Message.Replace('\n', ' ').Replace('\r', ' ');
    private static long RequiredLong(JsonElement e, string key) => e.GetProperty(key).GetInt64();
    private static string RequiredString(JsonElement e, string key) => e.GetProperty(key).GetString() ?? throw new ArgumentException($"{key}がありません。");
    public async Task RunAsync(CancellationToken token = default)
    {
        await WriteAsync("AMTPREVIEW READY version=2").ConfigureAwait(false);
        using var reader = new StreamReader(_input, Encoding.UTF8, false, 4096, leaveOpen: true);
        while (!token.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(token).ConfigureAwait(false); if (line is null) break;
            try
            {
                using var doc = JsonDocument.Parse(line); var command = doc.RootElement;
                switch (RequiredString(command, "cmd"))
                {
                    case "config": StartConfig(command.Clone()); break;
                    case "frame": StartFrame(command.Clone()); break;
                    case "view": StartView(command.Clone()); break;
                    case "cancel": Cancel(RequiredLong(command, "id")); break;
                    case "quit": return;
                    default: throw new ArgumentException("不明なコマンドです。");
                }
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
            { await WriteAsync($"AMTPREVIEW ERROR id=0 gen={_gen} message={Message(e)}").ConfigureAwait(false); }
        }
    }
    private void StartConfig(JsonElement command)
    {
        long gen = RequiredLong(command, "gen");
        lock (_sync)
        {
            _prefetch?.Cancel();
            if (gen <= _gen) throw new ArgumentException("genは増加させてください。");
            _gen = gen; _generation.Cancel(); _generation.Dispose(); _generation = new();
            foreach (var request in _requests.Values) request.Cancel();
            _tasks.RemoveAll(task => task.IsCompleted);
            Task[] previous = _tasks.ToArray();
            _viewAccepted = Task.CompletedTask;
            _configured = ConfigureAsync(command, gen, previous, _generation.Token);
            _tasks.Add(_configured);
        }
    }
    private async Task ConfigureAsync(JsonElement command, long gen, Task[] previous, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            // 旧応答が保持中の表示フレームを返してから、使用中のセッションを入れ替える。
            await Task.WhenAll(previous).WaitAsync(token).ConfigureAwait(false);
            await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_controller is not null)
                {
                    await _controller.DisposeAsync().ConfigureAwait(false);
                    _controller = null;
                }
                var tags = ReadColorTags(command);
                string kind = RequiredString(command, "kind");
                if (kind == "encoder")
                {
                    await ConfigureEncoderAsync(command, gen, tags, token).ConfigureAwait(false);
                    return;
                }
                if (kind != "avs") throw new ArgumentException("未対応のフィルタ種別です。");
                if (_encoderSession is not null)
                {
                    await _encoderSession.DisposeAsync().ConfigureAwait(false);
                    _encoderSession = null;
                }
                var source = await Task.Run(() => _open(_directory, RequiredString(command, "path"))).ConfigureAwait(false);
                if (token.IsCancellationRequested) { source.Dispose(); token.ThrowIfCancellationRequested(); }
                var info = source.Info;
                var header = info.Header;
                try { (_sarNum, _sarDen) = ReadSar(command, header.SarNum, header.SarDen); }
                catch { source.Dispose(); throw; }
                _controller = new FrameNumberController(source);
                _colorTags = tags;
                _configuredGeneration = gen;
                await WriteAsync($"AMTPREVIEW INFO gen={gen} frames={info.NumFrames} fpsnum={header.FpsNum} fpsden={header.FpsDen} width={header.Width} height={header.Height} sarnum={_sarNum} sarden={_sarDen}",
                    generation: gen, cancellationToken: token).ConfigureAwait(false);
            }
            finally { _serial.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { await ReportErrorAsync(0, gen, e, token).ConfigureAwait(false); }
    }

    private static Dictionary<string, string> ReadColorTags(JsonElement command)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, tag) in new[] { ("matrix", "XCOLORMATRIX"), ("range", "XCOLORRANGE"), ("primaries", "XCOLORPRIM"), ("transfer", "XTRANSFER"), ("chromaloc", "XCHROMALOC") })
        {
            if (!command.TryGetProperty(name, out var property)) continue;
            string value = property.GetString() ?? throw new ArgumentException($"{name}が不正です。");
            if (value.Length == 0 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
                throw new ArgumentException($"{name}が不正です。");
            tags[tag] = value;
        }
        return tags;
    }

    private static (int Numerator, int Denominator) ReadSar(JsonElement command, int defaultNumerator, int defaultDenominator)
    {
        int sn = command.TryGetProperty("sarnum", out var num) ? num.GetInt32() : defaultNumerator;
        int sd = command.TryGetProperty("sarden", out var den) ? den.GetInt32() : defaultDenominator;
        if (sn < 0 || sd < 0 || (sn == 0) != (sd == 0)) throw new ArgumentException("SARが不正です。");
        return (sn, sd);
    }

    private async Task ReportErrorAsync(long id, long gen, Exception error, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        await _log.WriteLineAsync(error.ToString()).ConfigureAwait(false);
        try { await WriteAsync($"AMTPREVIEW ERROR id={id} gen={gen} message={Message(error)}", generation: gen, cancellationToken: token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void StartFrame(JsonElement command)
    {
        long id = RequiredLong(command, "id"), gen = RequiredLong(command, "gen");
        lock (_sync)
        {
            if (gen != _gen) return;
            _prefetch?.Cancel();
            if (_requests.Remove(id, out var previous)) { previous.Cancel(); }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_generation.Token);
            _requests[id] = cts;
            _tasks.RemoveAll(task => task.IsCompleted);
            Task task = FrameAsync(command, id, gen, _configured, cts); _tasks.Add(task);
        }
    }
    private void Cancel(long id) { lock (_sync) if (_requests.TryGetValue(id, out var cts)) cts.Cancel(); }
    private async Task FrameAsync(JsonElement command, long id, long gen, Task configured, CancellationTokenSource cts)
    {
        await Task.Yield(); CancellationToken token = cts.Token;
        try
        {
            await configured.WaitAsync(token).ConfigureAwait(false);
            await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_configuredGeneration != gen || _controller is null) throw new InvalidOperationException("AviSynthが設定されていません。");
                int position = command.GetProperty("pos").GetInt32();
                if (position < 0 || position >= _controller.Info.NumFrames) throw new ArgumentOutOfRangeException(nameof(position));
                bool decimate = !command.TryGetProperty("decimate", out var flag) || flag.GetBoolean();
                if (command.TryGetProperty("step", out var step))
                {
                    int direction = step.GetInt32();
                    int? next = await _controller.GetNeighborAsync(position, direction, decimate, token).ConfigureAwait(false);
                    position = next ?? throw new ArgumentOutOfRangeException(nameof(position), "これ以上コマ送りできません。");
                }
                string side = command.TryGetProperty("side", out var sideValue) ? sideValue.GetString() ?? "after" : "after";
                if (side is not ("before" or "after")) throw new ArgumentException("sideが不正です。");
                string format = command.TryGetProperty("format", out var f) ? f.GetString() ?? "yuv" : "yuv";
                int scale = command.TryGetProperty("scale", out var sc) ? sc.GetInt32() : 1;
                int[]? crop = command.TryGetProperty("crop", out var cr) ? cr.EnumerateArray().Select(x => x.GetInt32()).ToArray() : null;
                NumberedFrame selected;
                if (side == "before")
                {
                    if (_before is null)
                    {
                        var beforeSource = await Task.Run(() => _open(_directory, Path.Combine(_tmp, "filter_preview", "src.avs"))).ConfigureAwait(false);
                        if (token.IsCancellationRequested) { beforeSource.Dispose(); token.ThrowIfCancellationRequested(); }
                        _before = new FrameNumberController(beforeSource);
                    }
                    double fromFps = _controller.Info.Header.Fps, beforeFps = _before.Info.Header.Fps;
                    int beforePosition = fromFps > 0 && beforeFps > 0
                        ? (int)Math.Min(_before.Info.NumFrames - 1L, (long)Math.Round(position * beforeFps / fromFps))
                        : Math.Min(position, _before.Info.NumFrames - 1);
                    selected = await _before.GetFrameAsync(beforePosition, token).ConfigureAwait(false);
                }
                else selected = await _controller.GetFrameAsync(position, token).ConfigureAwait(false);
                var input = selected.Frame;
                if (_colorTags.Count > 0)
                {
                    var original = input.Header;
                    var tags = new Dictionary<string, string>(original.ExtTags, StringComparer.Ordinal);
                    foreach (var pair in _colorTags) tags[pair.Key] = pair.Value;
                    var updated = new Y4mHeader { Width = original.Width, Height = original.Height, FpsNum = original.FpsNum, FpsDen = original.FpsDen,
                        SarNum = _sarNum, SarDen = _sarDen, Chroma = original.Chroma, BitDepth = original.BitDepth, ColorSpaceTag = original.ColorSpaceTag, ExtTags = tags };
                    input = new Y4mFrame(updated, input.Xts, input.Xdur, input.Data);
                }
                var transformed = EngineImage.Transform(input, scale, crop);
                byte[] payload = EngineImage.Encode(transformed, format); token.ThrowIfCancellationRequested();
                var h = transformed.Header; string chroma = h.Chroma switch { Y4mChroma.C420 => "420", Y4mChroma.C422 => "422", _ => "444" };
                string type = selected.Kfm?.FrameType switch { 1 => "60p", 2 => "30p", 3 => "24p", 4 => "ucf", _ => "none" };
                string fmt = format == "yuv" ? $"yuv{chroma}p{h.BitDepth}" : format;
                string Safe(string? value) => string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace) ? "unknown" : value;
                var (chromaWidth, _) = h.ChromaPlaneSize;
                int lumaPitch = checked(h.Width * h.BytesPerSample), chromaPitch = checked(chromaWidth * h.BytesPerSample);
                await WriteAsync($"AMTPREVIEW FRAME id={id} gen={gen} pos={position} type={type} dur={selected.Kfm?.Duration ?? 0} kept={(selected.IsKept ? 1 : 0)} format={fmt} width={h.Width} height={h.Height} bytes={payload.Length} sarnum={_sarNum} sarden={_sarDen} pitches={lumaPitch},{chromaPitch},{chromaPitch} matrix={Safe(h.ColorMatrix)} range={Safe(h.ColorRange)} primaries={Safe(h.ColorPrimaries)} transfer={Safe(h.Transfer)} chromaloc={Safe(h.ChromaLocation)}", payload, gen, token).ConfigureAwait(false);
                if (side == "after")
                {
                    lock (_sync)
                    {
                        if (gen == _gen && !token.IsCancellationRequested)
                        {
                            _prefetch?.Cancel(); _prefetch?.Dispose(); _prefetch = CancellationTokenSource.CreateLinkedTokenSource(_generation.Token);
                            var prefetch = _controller.PrefetchAsync(position, 1, 1, decimate, _prefetch.Token);
                            _tasks.Add(ObservePrefetchAsync(prefetch));
                        }
                    }
                }
            }
            finally { _serial.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { await ReportErrorAsync(id, gen, e, token).ConfigureAwait(false); }
        finally { lock (_sync) { if (_requests.TryGetValue(id, out var current) && ReferenceEquals(current, cts)) _requests.Remove(id); } cts.Dispose(); }
    }
    private async Task ObservePrefetchAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception e) { await _log.WriteLineAsync(e.ToString()).ConfigureAwait(false); }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_sync) { _closing = true; _generation.Cancel(); foreach (var request in _requests.Values) request.Cancel(); tasks = _tasks.ToArray(); }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (_controller is not null) await _controller.DisposeAsync().ConfigureAwait(false);
        if (_before is not null) await _before.DisposeAsync().ConfigureAwait(false);
        if (_encoderSession is not null) await _encoderSession.DisposeAsync().ConfigureAwait(false);
        _prefetch?.Dispose(); _generation.Dispose(); _serial.Dispose(); _write.Dispose();
    }
}

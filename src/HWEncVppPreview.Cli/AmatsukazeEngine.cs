using System.Globalization;
using System.Text;
using System.Text.Json;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Cli;

/// <summary>Amatsukazeサーバーと標準入出力で通信するプレビューエンジン。</summary>
public sealed class AmatsukazeEngine : IAsyncDisposable
{
    private readonly Stream _input, _output;
    private readonly TextWriter _log;
    private readonly string _tmp, _directory;
    private readonly Dictionary<string, string> _encoders;
    private readonly Func<string, string, IFrameNumberSource> _open;
    private readonly Func<IPreviewBackend> _backendFactory;
    private readonly SemaphoreSlim _serial = new(1, 1), _write = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource _generation = new();
    private readonly Dictionary<long, CancellationTokenSource> _requests = new();
    private readonly List<Task> _tasks = new();
    private FrameNumberController? _controller, _before;
    private CancellationTokenSource? _prefetch;
    private IPreviewBackend? _backend;
    private string? _encoder, _kind, _interlace;
    private string[] _vpp = [];
    private int _sarNum, _sarDen;
    private Dictionary<string, string> _colorTags = new(StringComparer.Ordinal);
    private long _gen = -1;
    private Task _configured = Task.CompletedTask;
    private bool _closing;

    public AmatsukazeEngine(Stream input, Stream output, TextWriter log, string tmp, string directory,
        Dictionary<string, string> encoders, Func<string, string, IFrameNumberSource>? open = null,
        Func<IPreviewBackend>? backendFactory = null)
    {
        _input = input; _output = output; _log = log; _tmp = Path.GetFullPath(tmp); _directory = Path.GetFullPath(directory);
        _encoders = new(encoders, StringComparer.OrdinalIgnoreCase);
        _open = open ?? ((dir, path) => new AvisynthFrameSource(dir, path));
        _backendFactory = backendFactory ?? (() => new ServerBackend());
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
    private async Task WriteAsync(string line, byte[]? bytes = null, long? generation = null, CancellationToken cancellationToken = default)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync) { if (_closing || generation is not null && generation != _gen) return; }
            cancellationToken.ThrowIfCancellationRequested();
            byte[] text = Encoding.UTF8.GetBytes(line + "\n");
            await _output.WriteAsync(text).ConfigureAwait(false);
            if (bytes is not null) await _output.WriteAsync(bytes).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }
    private static string Message(Exception e) => e.Message.Replace('\n', ' ').Replace('\r', ' ');
    private static long RequiredLong(JsonElement e, string key) => e.GetProperty(key).GetInt64();
    private static string RequiredString(JsonElement e, string key) => e.GetProperty(key).GetString() ?? throw new ArgumentException($"{key}がありません。");
    public async Task RunAsync(CancellationToken token = default)
    {
        await WriteAsync("AMTPREVIEW READY version=1").ConfigureAwait(false);
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
            _configured = ConfigureAsync(command, gen, _generation.Token); _tasks.Add(_configured);
        }
    }
    private async Task ConfigureAsync(JsonElement command, long gen, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_controller is not null) { await _controller.DisposeAsync().ConfigureAwait(false); _controller = null; }
                if (_backend is not null) { await _backend.DisposeAsync().ConfigureAwait(false); _backend = null; }
                string kind = RequiredString(command, "kind"), path;
                string? encoder = null, interlace = null; string[] vpp = [];
                if (kind == "avs") path = RequiredString(command, "path");
                else if (kind == "encoder")
                {
                    string name = RequiredString(command, "encoder");
                    if (!_encoders.TryGetValue(name, out encoder)) throw new ArgumentException("エンコーダが指定されていません。");
                    path = Path.Combine(_tmp, "filter_preview", "src.avs");
                    if (command.TryGetProperty("interlace", out var field))
                    {
                        interlace = field.GetString();
                        if (interlace is not ("tff" or "bff" or "progressive")) throw new ArgumentException("interlaceが不正です。");
                    }
                    if (command.TryGetProperty("vpp", out var array)) vpp = array.EnumerateArray().Select(x => x.GetString() ?? throw new ArgumentException("vppが不正です。")).ToArray();
                }
                else throw new ArgumentException("未対応のフィルタ種別です。");
                var tags = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, tag) in new[] { ("matrix", "XCOLORMATRIX"), ("range", "XCOLORRANGE"), ("primaries", "XCOLORPRIM"), ("transfer", "XTRANSFER"), ("chromaloc", "XCHROMALOC") })
                {
                    if (!command.TryGetProperty(name, out var property)) continue;
                    string value = property.GetString() ?? throw new ArgumentException($"{name}が不正です。");
                    if (value.Length == 0 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))) throw new ArgumentException($"{name}が不正です。");
                    tags[tag] = value;
                }
                var source = await Task.Run(() => _open(_directory, path)).ConfigureAwait(false);
                if (token.IsCancellationRequested) { source.Dispose(); token.ThrowIfCancellationRequested(); }
                var info = source.Info; var h = info.Header;
                int sarNum, sarDen;
                try
                {
                    sarNum = command.TryGetProperty("sarnum", out var sn) ? sn.GetInt32() : h.SarNum;
                    sarDen = command.TryGetProperty("sarden", out var sd) ? sd.GetInt32() : h.SarDen;
                    if (sarNum < 0 || sarDen < 0 || (sarNum == 0) != (sarDen == 0)) throw new ArgumentException("SARが不正です。");
                }
                catch { source.Dispose(); throw; }
                try { if (kind == "encoder") _backend = _backendFactory(); }
                catch { source.Dispose(); throw; }
                _controller = new FrameNumberController(source); _kind = kind; _encoder = encoder; _interlace = interlace; _vpp = vpp;
                _sarNum = sarNum; _sarDen = sarDen; _colorTags = tags;
                await WriteAsync($"AMTPREVIEW INFO gen={gen} frames={info.NumFrames} fpsnum={h.FpsNum} fpsden={h.FpsDen} width={h.Width} height={h.Height} sarnum={sarNum} sarden={sarDen}", generation: gen).ConfigureAwait(false);
            }
            finally { _serial.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { await _log.WriteLineAsync(e.ToString()).ConfigureAwait(false); await WriteAsync($"AMTPREVIEW ERROR id=0 gen={gen} message={Message(e)}", generation: gen).ConfigureAwait(false); }
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
    private sealed class FirstSink : IPreviewSink
    {
        public readonly TaskCompletionSource<Y4mFrame> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnHeader(Y4mHeader header) { }
        public void OnFrame(Y4mFrame frame) => First.TrySetResult(frame);
        public void OnOutput() { }
    }
    private async Task FrameAsync(JsonElement command, long id, long gen, Task configured, CancellationTokenSource cts)
    {
        await Task.Yield(); CancellationToken token = cts.Token;
        try
        {
            await configured.WaitAsync(token).ConfigureAwait(false);
            await _serial.WaitAsync(token).ConfigureAwait(false);
            Task<PreviewRunResult>? encoderRun = null;
            CancellationTokenSource? encoderStop = null;
            try
            {
                if (_controller is null) throw new InvalidOperationException("設定されていません。");
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
                if (_kind == "encoder" && side == "after")
                {
                    var info = _controller.Info; var sink = new FirstSink();
                    encoderStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var inputArgs = new List<string>();
                    if (OperatingSystem.IsWindows()) inputArgs.AddRange(["--avsdll", Path.Combine(_directory, "AviSynth.dll")]);
                    inputArgs.Add("--avs");
                    if (_interlace is "tff" or "bff") inputArgs.AddRange(["--interlace", _interlace]);
                    var req = new PreviewRequest { EncoderPath = _encoder!, InputFile = Path.Combine(_tmp, "filter_preview", "src.avs"),
                        InputArgs = inputArgs, VppArgs = _vpp, TargetSeconds = position / info.Header.Fps, Fps = info.Header.Fps,
                        PreMarginSeconds = 0, PostMarginSeconds = 0 };
                    encoderRun = _backend!.RunAsync(req, sink, encoderStop.Token);
                    Task winner = await Task.WhenAny(sink.First.Task, encoderRun).WaitAsync(token).ConfigureAwait(false);
                    if (winner == encoderRun && !sink.First.Task.IsCompleted) { var end = await encoderRun.ConfigureAwait(false); throw new InvalidOperationException($"フレームを取得できませんでした: {end.StandardError}"); }
                    var frame = await sink.First.Task.ConfigureAwait(false); encoderStop.Cancel();
                    selected = new(position, frame, null, true);
                }
                else if (_kind == "avs" && side == "before")
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
                if (_kind == "avs" && side == "after")
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
            finally
            {
                try
                {
                    if (encoderStop is not null) encoderStop.Cancel();
                    if (encoderRun is not null)
                    {
                        try
                        {
                            var result = await encoderRun.ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(result.StandardError)) await _log.WriteLineAsync(result.StandardError).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { }
                    }
                }
                finally { encoderStop?.Dispose(); _serial.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { await _log.WriteLineAsync(e.ToString()).ConfigureAwait(false); await WriteAsync($"AMTPREVIEW ERROR id={id} gen={gen} message={Message(e)}", generation: gen).ConfigureAwait(false); }
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
        if (_backend is not null) await _backend.DisposeAsync().ConfigureAwait(false);
        _prefetch?.Dispose(); _generation.Dispose(); _serial.Dispose(); _write.Dispose();
    }
}

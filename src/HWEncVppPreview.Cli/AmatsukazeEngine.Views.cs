using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Cli;

public sealed partial class AmatsukazeEngine
{
    private Task _viewAccepted = Task.CompletedTask;
    private readonly Dictionary<CancellationTokenSource, TaskCompletionSource> _activeViews = new();

    private async Task ConfigureEncoderAsync(JsonElement command, long gen, Dictionary<string, string> tags,
        CancellationToken token)
    {
        string encoderName = RequiredString(command, "encoder");
        if (!_encoders.TryGetValue(encoderName, out var encoder))
            throw new ArgumentException("エンコーダが指定されていません。");

        var arguments = new List<string>();
        if (OperatingSystem.IsWindows())
            arguments.AddRange(["--avsdll", Path.Combine(_directory, "AviSynth.dll")]);
        arguments.Add("--avs");

        if (command.TryGetProperty("interlace", out var interlace))
        {
            switch (interlace.GetString())
            {
                case "tff": arguments.AddRange(["--interlace", "tff"]); break;
                case "bff": arguments.AddRange(["--interlace", "bff"]); break;
                case "progressive": break;
                default: throw new ArgumentException("interlaceが不正です。");
            }
        }

        string[] vpp = command.TryGetProperty("vpp", out var array)
            ? array.EnumerateArray().Select(value => value.GetString() ?? throw new ArgumentException("vppが不正です。")).ToArray()
            : [];
        var session = _encoderSession ??= _encoderSessionFactory();
        var state = await session.ConfigureAsync(new(encoder, Path.Combine(_tmp, "filter_preview", "src.avs"),
            arguments, vpp), token).ConfigureAwait(false);
        if (state.Input is not { } info)
            throw new InvalidOperationException(state.Error ?? "入力映像の情報を取得できませんでした。");
        (_sarNum, _sarDen) = ReadSar(command, info.SarNum, info.SarDen);
        _colorTags = tags;
        _configuredGeneration = gen;
        var duration = state.DurationSeconds is double seconds
            ? " durationSeconds=" + seconds.ToString("R", CultureInfo.InvariantCulture) : "";
        await WriteAsync($"AMTPREVIEW INFO gen={gen} kind=encoder width={info.Width} height={info.Height} sarnum={_sarNum} sarden={_sarDen} fpsnum={info.FpsNum} fpsden={info.FpsDen}{duration}",
            generation: gen, cancellationToken: token).ConfigureAwait(false);
    }

    private void StartView(JsonElement command)
    {
        long id = RequiredLong(command, "id"), gen = RequiredLong(command, "gen");
        lock (_sync)
        {
            if (gen != _gen) return;
            if (_requests.Remove(id, out var previous)) previous.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_generation.Token);
            _requests[id] = cancellation;
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = ViewAsync(command, id, gen, _configured, _viewAccepted, accepted, cancellation);
            _viewAccepted = accepted.Task;
            _tasks.RemoveAll(value => value.IsCompleted);
            _tasks.Add(task);
        }
    }

    private async Task ViewAsync(JsonElement command, long id, long gen, Task configured, Task previous,
        TaskCompletionSource accepted, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        var token = cancellation.Token;
        var superseded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EncoderPreviewSession? session = null;
        Channel<EncoderPreviewView>? updates = null;
        Action<EncoderPreviewView>? observe = null;
        try
        {
            await previous.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await configured.WaitAsync(token).ConfigureAwait(false);
            if (_configuredGeneration != gen || _encoderSession is not { } encoder)
                throw new InvalidOperationException("エンコーダが設定されていません。");
            session = encoder;
            var options = ReadViewOptions(command);
            string operation = RequiredString(command, "op");
            if (command.TryGetProperty("pos", out _) || command.TryGetProperty("step", out _))
                throw new ArgumentException("エンコーダの操作は時刻または方向を指定してください。");

            if (operation == "snapshot")
            {
                long? revision = command.TryGetProperty("revision", out var specified) ? specified.GetInt64() : null;
                using var snapshot = await session.AcquireViewAsync(revision, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("指定した表示の更新は失効しています。");
                accepted.TrySetResult();
                await WriteViewAsync(id, gen, snapshot, options, includeOldFrames: true, token).ConfigureAwait(false);
                await WriteDoneAsync(id, gen, "ok", token).ConfigureAwait(false);
                return;
            }

            // stdoutが詰まってもCoreの配送を止めない。表示の更新が速いときは最新の数件だけを保持する。
            updates = Channel.CreateBounded<EncoderPreviewView>(new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            }, dropped => dropped.Dispose());
            observe = view => { if (!updates.Writer.TryWrite(view)) view.Dispose(); };
            session.ViewChanged += observe;
            var before = await session.GetStateAsync(token).ConfigureAwait(false);
            var state = await ApplyViewOperationAsync(session, operation, command, token).ConfigureAwait(false);

            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (state.Generation != before.Generation || state.DisplayMode != before.DisplayMode)
                    foreach (var active in _activeViews.Values) active.TrySetResult();
                _activeViews[cancellation] = superseded;
            }
            accepted.TrySetResult();

            if (operation == "step" && state.Generation == before.Generation && state.IsBusy)
            {
                await WriteDoneAsync(id, gen, "ignored", token).ConfigureAwait(false);
                return;
            }

            await SendViewUpdatesAsync(session, updates.Reader, superseded.Task, state, options, id, gen, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { await ReportErrorAsync(id, gen, exception, token).ConfigureAwait(false); }
        finally
        {
            // 取消済み要求も、前の受付が済んでから順序を解放する。
            accepted.TrySetResult();
            if (observe is not null) session!.ViewChanged -= observe;
            if (updates is not null)
            {
                updates.Writer.TryComplete();
                while (updates.Reader.TryRead(out var view)) view.Dispose();
            }
            lock (_sync)
            {
                _activeViews.Remove(cancellation);
                if (_requests.TryGetValue(id, out var current) && ReferenceEquals(current, cancellation))
                    _requests.Remove(id);
            }
            cancellation.Dispose();
        }
    }

    private async Task SendViewUpdatesAsync(EncoderPreviewSession session, ChannelReader<EncoderPreviewView> changes,
        Task superseded, EncoderPreviewState accepted, ViewOptions options, long id, long gen, CancellationToken token)
    {
        long lastRevision = -1;
        long lastGeneration = -1;
        string? diagnostic = null;
        bool wroteView = false;
        var delivery = new EncoderPreviewViewDelivery();

        // 受付直後の通知がなかった場合（同じ位置や表示モードなど）も、現在の状態から開始する。
        using (var current = await session.AcquireViewAsync(cancellationToken: token).ConfigureAwait(false))
        {
            while (!superseded.IsCompleted)
            {
                if (changes.TryRead(out var queued))
                {
                    using (queued)
                        if (await SendAsync(queued).ConfigureAwait(false)) return;
                    continue;
                }
                if (current is not null && await SendAsync(current).ConfigureAwait(false)) return;
                break;
            }
        }

        while (!superseded.IsCompleted)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<bool> ready = changes.WaitToReadAsync(wait.Token).AsTask();
            if (await Task.WhenAny(ready, superseded).ConfigureAwait(false) == superseded)
            {
                wait.Cancel();
                try { await ready.ConfigureAwait(false); } catch (OperationCanceledException) { }
                break;
            }
            if (!await ready.ConfigureAwait(false)) break;
            while (!superseded.IsCompleted && changes.TryRead(out var view))
            {
                using (view)
                    if (await SendAsync(view).ConfigureAwait(false)) return;
            }
        }

        await WriteDoneAsync(id, gen, "superseded", token).ConfigureAwait(false);

        async Task<bool> SendAsync(EncoderPreviewView view)
        {
            token.ThrowIfCancellationRequested();
            var state = view.State;
            if (state.Generation > accepted.Generation || state.DisplayMode != accepted.DisplayMode)
            {
                // 後続操作による変更か、同じ操作内の再取得かは受付処理が確定してから判別する。
                Task receipt;
                lock (_sync) receipt = _viewAccepted;
                await receipt.WaitAsync(token).ConfigureAwait(false);
                if (superseded.IsCompleted || state.DisplayMode != accepted.DisplayMode) return false;
            }
            if (state.Generation < accepted.Generation || state.Generation < lastGeneration
                || state.ViewRevision < Math.Max(lastRevision, accepted.ViewRevision)) return false;
            if (state.Diagnostics != diagnostic)
            {
                diagnostic = state.Diagnostics;
                if (!string.IsNullOrWhiteSpace(diagnostic)) await _log.WriteLineAsync(diagnostic).ConfigureAwait(false);
            }
            if (state.Error is { } error) throw new InvalidOperationException(error);

            bool edge = state.Progress is PreviewProgress.Start or PreviewProgress.End;
            bool hasAfter = view.After is { } after && (after.Generation == state.Generation || edge);
            bool hasBefore = view.Before is { } before && (before.Generation == state.Generation || edge);
            bool wantsBefore = state.DisplayMode != EncoderPreviewDisplayMode.After;
            bool wantsAfter = state.DisplayMode != EncoderPreviewDisplayMode.Before;
            bool beforeReady = hasBefore && view.Before?.IsProvisional == false;
            if (lastRevision != state.ViewRevision || lastGeneration != state.Generation)
            {
                // 不要な背景の更新だけで画像を返さないが、新しい世代で表示が未到着なら空の結果を返して失効を伝える。
                if ((wantsAfter && hasAfter) || (wantsBefore && hasBefore) || wroteView)
                {
                    if (!await WriteViewAsync(id, gen, view, options, includeOldFrames: edge, token, superseded, delivery)
                        .ConfigureAwait(false)) return false;
                    wroteView = true;
                }
                lastRevision = state.ViewRevision;
                lastGeneration = state.Generation;
            }

            if (state.IsBusy || (!hasAfter && !edge)) return false;
            if (wantsBefore && !edge && (state.IsBeforeBusy || !beforeReady && !state.BeforeUnavailable)) return false;

            string status = state.Progress switch
            {
                PreviewProgress.Start => "start",
                PreviewProgress.End => "end",
                PreviewProgress.DoneTargetNotReached => "targetNotReached",
                _ when wantsBefore && !beforeReady => "partial",
                _ => "ok",
            };
            return await WriteDoneAsync(id, gen, status, token, superseded).ConfigureAwait(false);
        }
    }

    private static Task<EncoderPreviewState> ApplyViewOperationAsync(EncoderPreviewSession session, string operation,
        JsonElement command, CancellationToken token) => operation switch
    {
        "seek" => session.SeekAsync(command.GetProperty("seconds").GetDouble(),
            command.TryGetProperty("phase", out var phase) ? phase.GetString() switch
            {
                "drag" => EncoderPreviewSeekPhase.Drag,
                "commit" => EncoderPreviewSeekPhase.Commit,
                _ => throw new ArgumentException("phaseが不正です。"),
            } : EncoderPreviewSeekPhase.Commit, token),
        "step" => session.StepAsync(RequiredString(command, "direction") switch
        {
            "next" => 1,
            "previous" => -1,
            _ => throw new ArgumentException("directionが不正です。"),
        }, token),
        "display" => session.SetDisplayAsync(RequiredString(command, "mode") switch
        {
            "after" => EncoderPreviewDisplayMode.After,
            "before" => EncoderPreviewDisplayMode.Before,
            "split" => EncoderPreviewDisplayMode.Split,
            "sideBySide" => EncoderPreviewDisplayMode.SideBySide,
            _ => throw new ArgumentException("modeが不正です。"),
        }, token),
        _ => throw new ArgumentException("未対応の表示操作です。"),
    };

    internal sealed record ViewOptions(string Format, int Scale, int[]? Crop);
    private static ViewOptions ReadViewOptions(JsonElement command)
    {
        string format = command.TryGetProperty("format", out var value) ? value.GetString() ?? "" : "yuv";
        int scale = command.TryGetProperty("scale", out var factor) ? factor.GetInt32() : 1;
        int[]? crop = command.TryGetProperty("crop", out var rect)
            ? rect.EnumerateArray().Select(value => value.GetInt32()).ToArray() : null;
        if (format is not ("yuv" or "jpeg" or "png") || scale is < 1 or > 16 || crop is { Length: not 4 })
            throw new ArgumentException("表示画像の転送形式が不正です。");
        return new(format, scale, crop);
    }

    private Task<bool> WriteDoneAsync(long id, long gen, string status, CancellationToken token, Task? superseded = null)
        => WriteAsync($"AMTPREVIEW DONE id={id} gen={gen} status={status}", generation: gen, cancellationToken: token, superseded: superseded);

    internal async Task<bool> WriteViewAsync(long id, long gen, EncoderPreviewView view, ViewOptions options, bool includeOldFrames,
        CancellationToken token, Task? superseded = null, EncoderPreviewViewDelivery? delivery = null)
    {
        var state = view.State;
        var frames = new List<object>(2);
        var payloads = new List<byte[]>(2);
        var after = state.DisplayMode == EncoderPreviewDisplayMode.Before ? null : Select(view.After);
        var before = state.DisplayMode == EncoderPreviewDisplayMode.After ? null : Select(view.Before);
        if (after is not null) Append("after", after, delivery?.ReuseAfter(after) == true);
        if (before is not null) Append("before", before, delivery?.ReuseBefore(before) == true);
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            currentSeconds = state.CurrentSeconds,
            durationSeconds = state.DurationSeconds,
            displayMode = JsonNamingPolicy.CamelCase.ConvertName(state.DisplayMode.ToString()),
            progress = JsonNamingPolicy.CamelCase.ConvertName(state.Progress.ToString()),
            busy = state.IsBusy,
            beforeBusy = state.IsBeforeBusy,
            frames,
        });
        long bytes = payloads.Sum(payload => payload.LongLength);
        bool sent = await WriteAsync($"AMTPREVIEW VIEW id={id} gen={gen} revision={state.ViewRevision} metaBytes={metadata.Length} bytes={bytes}",
            metadata, gen, token, payloads, superseded).ConfigureAwait(false);
        if (sent) delivery?.Remember(after, before);
        return sent;

        EncoderPreviewFrame? Select(EncoderPreviewFrame? frame)
            => frame is not null && (includeOldFrames || frame.Generation == state.Generation) ? frame : null;

        void Append(string side, EncoderPreviewFrame selected, bool reuse)
        {
            token.ThrowIfCancellationRequested();
            var timing = selected.Timing;
            if (reuse)
            {
                frames.Add(new
                {
                    side,
                    inputSeconds = Finite(timing.InputSeconds),
                    xts = Finite(timing.Xts),
                    xdur = Finite(timing.Xdur),
                    durationSeconds = Finite(timing.DurationSeconds),
                    provisional = selected.IsProvisional,
                    bytes = 0,
                    reuse = true,
                });
                return;
            }
            var input = selected.Frame;
            var original = input.Header;
            var tags = new Dictionary<string, string>(original.ExtTags, StringComparer.Ordinal);
            // 出力の色指定があればそちらを優先し、ソースの情報は欠落時だけ補う。
            foreach (var pair in _colorTags)
                if (!tags.TryGetValue(pair.Key, out var value) || value == "unknown")
                    tags[pair.Key] = pair.Value;
            var header = new Y4mHeader
            {
                Width = original.Width, Height = original.Height, FpsNum = original.FpsNum, FpsDen = original.FpsDen,
                SarNum = original.SarNum > 0 ? original.SarNum : _sarNum,
                SarDen = original.SarDen > 0 ? original.SarDen : _sarDen,
                Chroma = original.Chroma, BitDepth = original.BitDepth, ColorSpaceTag = original.ColorSpaceTag, ExtTags = tags,
            };
            // プレビューから借りたrawはVIEWが保持し、変換結果だけを転送側で所有する。
            var frame = EngineImage.Transform(new(header, input.Xts, input.Xdur, input.RawPixels), options.Scale, options.Crop);
            byte[] payload = EngineImage.Encode(frame, options.Format);
            var h = frame.Header;
            var (chromaWidth, _) = h.ChromaPlaneSize;
            int lumaPitch = checked(h.Width * h.BytesPerSample), chromaPitch = checked(chromaWidth * h.BytesPerSample);
            string chroma = h.Chroma switch { Y4mChroma.C420 => "420", Y4mChroma.C422 => "422", _ => "444" };
            frames.Add(new
            {
                side,
                inputSeconds = Finite(timing.InputSeconds),
                xts = Finite(timing.Xts),
                xdur = Finite(timing.Xdur),
                durationSeconds = Finite(timing.DurationSeconds),
                provisional = selected.IsProvisional,
                format = options.Format == "yuv" ? $"yuv{chroma}p{h.BitDepth}" : options.Format,
                width = h.Width,
                height = h.Height,
                bytes = payload.Length,
                pitches = new[] { lumaPitch, chromaPitch, chromaPitch },
                sarnum = h.SarNum,
                sarden = h.SarDen,
                matrix = h.ColorMatrix ?? "unknown",
                range = h.ColorRange ?? "unknown",
                primaries = h.ColorPrimaries ?? "unknown",
                transfer = h.Transfer ?? "unknown",
                chromaloc = h.ChromaLocation ?? "unknown",
            });
            payloads.Add(payload);
        }
    }

    private static double? Finite(double? value) => value is double number && double.IsFinite(number) ? number : null;
}

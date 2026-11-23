using System.Diagnostics;

namespace HWEncVppPreview.Core;

public sealed record EncoderPreviewConfiguration(string Encoder, string Input,
    IReadOnlyList<string> InputArgs, IReadOnlyList<string> VppArgs);

internal sealed record EncoderPreviewMetadata(string Version, string Help, HelpIndex HelpIndex,
    PreviewCapabilities Capabilities);

/// <summary>エンコーダプレビューの入力検査と論理要求を管理する。</summary>
public sealed partial class EncoderPreviewSession : IAsyncDisposable
{
    private readonly TimeProvider _clock;
    private readonly PreviewTimeouts _timeouts;
    private readonly InputProbe _probe;
    private readonly bool _borrowInputProbe;
    private readonly Func<string, string, CancellationToken, Task<string>>? _readTextOverride;

    private PreviewController Controller { get; }

    internal EncoderPreviewSession(TimeProvider clock, PreviewTimeouts timeouts, IPreviewBackend? backend,
        IPreviewBackend? backgroundBackend, FrameCache cache, RawOwnershipScope? ownershipScope,
        InputProbe? probe, TimeSpan? debounce,
        Func<string, string, CancellationToken, Task<string>>? readTextOverride,
        IEncoderPreviewHost host, Action<Action> post, bool beforeEnabled)
    {
        _host = host;
        _beforeEnabled = beforeEnabled;
        _post = post;
        _cache = cache;
        _rawOwnershipScope = ownershipScope;
        _usesOwnedRaw = ownershipScope != null;
        _clock = clock;
        _timeouts = timeouts;
        _probe = probe ?? new InputProbe(timeouts, clock);
        _borrowInputProbe = probe == null && backend == null;
        _readTextOverride = readTextOverride;
        var observed = new ObservedBackend(backend ?? new ServerBackend(timeouts, clock), this);
        var before = backgroundBackend != null ? new ObservedBackend(backgroundBackend, this) :
            backend == null ? new ObservedBackend(new ServerBackend(timeouts, clock), this) : observed;
        Controller = new PreviewController(timeouts, clock, observed, cache, debounce: debounce,
            backgroundBackend: before, ownershipScope: ownershipScope);
        Controller.ReplayStarted += OnReplayStarted;
        if (_usesOwnedRaw)
        {
            Controller.OwnedSegmentUpdated += OnOwnedSegmentUpdated;
            Controller.OwnedFrameReady += OnOwnedFrameReady;
            Controller.OwnedCompleted += OnOwnedCompleted;
        }
        else
        {
            Controller.SegmentUpdated += OnSegmentUpdated;
            Controller.FrameReady += OnFrameReady;
            Controller.Completed += OnCompleted;
        }
        Controller.Failed += OnFailed;
    }

    private async Task LoadMetadataAsync(string encoder, CancellationToken token)
    {
        try
        {
            var metadata = await ReadMetadataAsync(encoder, token).ConfigureAwait(false);
            await PostAsync(() =>
            {
                if (IsDisposed || _host.EncoderPath != encoder) return;
                _host.ApplyMetadata(encoder, metadata);
                _capabilities = metadata.Capabilities;
                CancelEncoderInitializationTracked();
                Track(StartEncoderInitialization(encoder, _lifetime.Token));
                _host.MetadataReady();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await PostAsync(() =>
            {
                if (IsDisposed || _host.EncoderPath != encoder) return;
                _metadataEncoder = "";
                _capabilities = new();
                _host.MetadataUnavailable(exception.Message);
            }).ConfigureAwait(false);
        }
    }

    internal void ClearMetadata()
    {
        _metadataEncoder = "";
        _capabilities = new();
    }

    internal void CancelEncoderInitializationTracked()
    {
        if (CancelEncoderInitialization() is { } cancellation) Track(cancellation);
    }

    internal Task WaitForBackendIdleAsync() => Controller.WaitForIdleAsync();

    private async Task<EncoderPreviewMetadata> ReadMetadataAsync(string encoder, CancellationToken token)
    {
        string version = await ReadTextAsync(encoder, "--version", token).ConfigureAwait(false);
        string help = await ReadTextAsync(encoder, "--help", token).ConfigureAwait(false);
        var index = new HelpIndex(help);
        return new(version, help, index, CommandBuilder.CapabilitiesFromHelp(index));
    }

    internal async Task<InputInfo> PrepareInputAsync(EncoderPreviewConfiguration configuration,
        PreviewCapabilities capabilities, InputInfo? currentInfo, bool validateRestoredTime, CancellationToken token)
    {
        var (encoder, input, inputArgs, _) = configuration;
        InputInfo info = currentInfo ?? await (_borrowInputProbe
            ? Controller.ProbeInputAsync(_probe, encoder, input, inputArgs, capabilities, token)
            : _probe.ProbeAsync(encoder, input, inputArgs, token, capabilities)).ConfigureAwait(false);
        // 常駐の初回検査で長さが不明なら、復元時刻の検証にだけ独立した長さ検査を使う。
        if (validateRestoredTime && info.Duration == null)
        {
            try
            {
                var duration = await ProbeDurationAsync(encoder, input, inputArgs, capabilities, token).ConfigureAwait(false);
                info = info with { Duration = duration };
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                // 長さを判定できない入力も、従来どおりプレビューは利用できる。
                if (PerfTrace.Enabled) PerfTrace.Write("vm.startup_duration.failed", new { error = exception.Message });
            }
        }
        return info;
    }

    internal Task<TimeSpan?> ProbeDurationAsync(string encoder, string input,
        IReadOnlyList<string> inputArgs, PreviewCapabilities capabilities, CancellationToken token)
        => _probe.ProbeDurationAsync(encoder, input, inputArgs, token, capabilities);

    internal PreviewRequest CreateAfterRequest(EncoderPreviewConfiguration configuration, InputInfo info,
        PreviewCapabilities capabilities, double target, long generation, bool reverse,
        bool allowConcurrentBefore, double? knownDuration)
    {
        var request = new PreviewRequest
        {
            EncoderPath = configuration.Encoder, InputFile = configuration.Input,
            InputArgs = configuration.InputArgs, VppArgs = configuration.VppArgs,
            AllowConcurrentBackground = allowConcurrentBefore,
            Fps = info.Fps, TargetSeconds = target, CorrelationId = generation,
            InputFormatHint = new Y4mHeader { Width = info.Width, Height = info.Height,
                FpsNum = info.FpsNum, FpsDen = info.FpsDen, Chroma = info.Chroma,
                BitDepth = info.BitDepth, ColorSpaceTag = info.ColorSpaceTag },
            PostMarginSeconds = capabilities.TimestampPassthrough && info.TimestampOriginSeconds.HasValue
                ? reverse ? 0.5 : 1.0 : 0.5,
            // 絶対時計の逆方向は、暖機2秒を除いても手前3秒を表示できる物理窓にする。
            // 順方向の論理2秒はCoreで物理3秒へ広げ、legacyの逆方向3秒は維持する。
            PreMarginSeconds = reverse
                ? capabilities.TimestampPassthrough && info.TimestampOriginSeconds.HasValue ? 5 : 3 : 2,
            Capabilities = capabilities, TimestampOriginSeconds = info.TimestampOriginSeconds,
            TimestampOriginTrusted = info.TimestampOriginTrusted,
            InputDurationSeconds = info.Duration?.TotalSeconds ?? knownDuration,
            Continuous = capabilities.TimestampPassthrough && info.TimestampOriginSeconds.HasValue && !reverse,
        };
        if (Controller.IsLegacySource(request))
            request = request with { Continuous = false, PreMarginSeconds = reverse ? 3 : request.PreMarginSeconds };
        return request;
    }

    internal void SubmitAfter(PreviewRequest request, bool bypassCache, bool requirePhysicalRefetch,
        bool navigation, bool reverse, int neighborDirection, bool bypassDebounce)
    {
        // 隣不足では旧cache hitを避ける。正方向だけ、生存中の同じ物理streamへ合流を明示する。
        bool reuseNeighbor = requirePhysicalRefetch && !bypassCache && navigation && !reverse
            && neighborDirection > 0 && request.Continuous;
        Controller.Submit(request, bypassCache || requirePhysicalRefetch, navigation: navigation,
            reuseActiveStream: reuseNeighbor, bypassDebounce: bypassDebounce);
    }

    internal ValueTask DisposeProbeAsync() => _probe.DisposeAsync();

    private async Task<string> ReadTextAsync(string encoder, string argument, CancellationToken token)
    {
        // 実プロセスを使わない試験でも、受け取った文字列の処理は通常経路を通す。
        if (_readTextOverride != null) return await _readTextOverride(encoder, argument, token).ConfigureAwait(false);
        using var process = new Process { StartInfo = new ProcessStartInfo(encoder)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(_timeouts.Metadata, _clock, token).ConfigureAwait(false); }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        if (process.ExitCode != 0)
        {
            string diagnostics = await error.ConfigureAwait(false);
            throw new PreviewBackendException(EncoderErrorDetails.Append(CoreText.EncoderInfoFailed(process.ExitCode), diagnostics), diagnostics);
        }
        string stdout = await output.ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stdout) ? await error.ConfigureAwait(false) : stdout;
    }

    private EncoderInitialization? _encoderInitialization;
    private sealed record EncoderInitialization(CancellationTokenSource Cancellation, Task Completion);

    internal Task StartEncoderInitialization(string encoder, CancellationToken token)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task completion = Controller.InitializeEncoderAsync(encoder, cancellation.Token);
        Interlocked.Exchange(ref _encoderInitialization, new(cancellation, completion));
        return completion;
    }

    internal Task? CancelEncoderInitialization()
    {
        var previous = Interlocked.Exchange(ref _encoderInitialization, null);
        if (previous == null) return null;
        // 変更・終了時に古い初期化を止め、consumerが終わってから取消源を破棄する。
        return Task.Run(async () =>
        {
            try
            {
                await previous.Cancellation.CancelAsync().ConfigureAwait(false);
                await previous.Completion.ConfigureAwait(false);
            }
            finally { previous.Cancellation.Dispose(); }
        });
    }
}

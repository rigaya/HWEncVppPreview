using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HWEncParamViewer.Core;

/// <summary>最初のフレームと入力ログから得た画像形式と長さ。原点数値と信頼性は分離し、長さ不明ならDurationはnull。</summary>
public sealed record InputInfo(int Width, int Height, int FpsNum, int FpsDen, int SarNum, int SarDen,
    Y4mChroma Chroma, int BitDepth, string ColorSpaceTag, TimeSpan? Duration = null, double? TimestampOriginSeconds = null, bool TimestampOriginTrusted = true)
{
    public double Fps => FpsDen > 0 ? (double)FpsNum / FpsDen : 0;
}

/// <summary>最初のフレームを取得する前に入力検査がエラー終了した、または有効な入力情報を返さなかったときの診断。</summary>
public sealed class InputProbeException : Exception
{
    public InputProbeException(string message, int exitCode, string standardError) : base(message)
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }
    public int ExitCode { get; }
    public string StandardError { get; }
}

/// <summary>入力検査のタイムアウトと、回収できた標準エラーログ。</summary>
public sealed class InputProbeTimeoutException : TimeoutException
{
    public InputProbeTimeoutException(string message, string standardError, Exception? innerException = null)
        : base(message, innerException) => StandardError = standardError;
    public string StandardError { get; }
}

/// <summary>都度起動で最初のフレームまで読み、入力形式をキャッシュする。外部中断や失敗した結果は保存しない。</summary>
/// <remarks>
/// 非キーフレームから始まるTSに対応するため、300フレームを上限として起動する。
/// ヘッダと最初のフレームを得たら自ら中断・回収し、その終了コードは無視する。
/// 画像検査同士と長さ検査同士はそれぞれ直列化し、画像と長さは並行して取得できる。
/// DisposeAsyncは検査中のプロセスも終了・回収する。
/// </remarks>
public sealed class InputProbe : IAsyncDisposable
{
    private const int ProbeFrameLimit = 300;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _durationGate = new(1, 1);
    private readonly Dictionary<ProbeKey, InputInfo> _cache = new();
    private readonly Dictionary<ProbeKey, TimeSpan?> _durationCache = new();
    private readonly Dictionary<Process, Task> _durationProcesses = new();
    private readonly Action<ProcessStartInfo>? _configureStartInfo;
    private readonly IPreviewBackend _backend;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private Task? _disposeTask;

    public InputProbe(TimeProvider? timeProvider = null, TimeSpan? timeout = null,
        Action<ProcessStartInfo>? configureStartInfo = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _backend = new OneShotBackend(configureStartInfo, _clock, StopTimeout);
        _configureStartInfo = configureStartInfo;
    }

    // 終了要求を無視する検査backendの回帰を、GPUやOSの異常終了に頼らず再現する。
    internal InputProbe(IPreviewBackend backend, TimeProvider timeProvider)
        : this(timeProvider) => _backend = backend;

    /// <summary>追加引数の既定は--avhw。引数配列を複製し、値が同じ要求にはキャッシュを返す。</summary>
    /// <exception cref="InputProbeException">エンコーダがエラー終了した、または有効なヘッダがない。</exception>
    /// <exception cref="InputProbeTimeoutException">検査全体が指定時間内に完了しなかった。</exception>
    public async Task<InputInfo> ProbeAsync(string encoderPath, string inputFile,
        IReadOnlyList<string>? inputArgs = null, CancellationToken cancellationToken = default, PreviewCapabilities? capabilities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFile);
        CancellationToken lifetime;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lifetime = _lifetime.Token;
        }
        string[] arguments = inputArgs?.ToArray() ?? new[] { "--avhw" };
        var key = new ProbeKey(encoderPath, inputFile, JsonSerializer.Serialize(arguments), capabilities ?? new());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_sync) { if (_cache.TryGetValue(key, out var cached)) return cached; }
            // fps未取得なので仮に1を使い、計画を300フレームに固定する。先頭の復号待ちを許し、取得後は即座に止める。
            var request = new PreviewRequest
            {
                EncoderPath = encoderPath, InputFile = inputFile, InputArgs = arguments.Concat(new[] { "--log-level", "input=debug" }).ToArray(),
                TargetSeconds = 0, PreMarginSeconds = 0, PostMarginSeconds = ProbeFrameLimit - 1, Fps = 1,
                Capabilities = capabilities ?? new(),
            };
            using var stopAfterFrame = new CancellationTokenSource();
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, stopAfterFrame.Token);
            var sink = new ProbeSink(() => stopAfterFrame.Cancel());
            Task<PreviewRunResult> run = _backend.RunAsync(request, sink, execution.Token);
            PreviewRunResult result;
            try
            {
                result = await run.WaitAsync(_timeout, _clock, cancellation.Token).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                string diagnostics = await StopProbeRunAsync(run, cancellation).ConfigureAwait(false);
                throw new InputProbeTimeoutException(CoreText.InputProbeTimedOut, diagnostics, exception);
            }
            catch (OperationCanceledException)
            {
                await StopProbeRunAsync(run, cancellation).ConfigureAwait(false);
                throw;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            // 自己中断と外部のキャンセルは区別する。最初のフレーム取得後の自己中断だけを成功扱いにする。
            if (!sink.HasFrame && result.ExitCode != 0)
                throw new InputProbeException(CoreText.InputProbeEncoderExited(result.ExitCode), result.ExitCode, result.StandardError);
            if (!sink.HasFrame || sink.Header is not { } header || header.Fps <= 0 || !double.IsFinite(header.Fps))
                throw new InputProbeException(CoreText.InputHeaderUnavailable, -1, result.StandardError);
            double? origin = capabilities?.TimestampPassthrough == true && sink.FirstTimestamp is double timestamp && double.IsFinite(timestamp) ? timestamp : null;
            var info = new InputInfo(header.Width, header.Height, header.FpsNum, header.FpsDen,
                header.SarNum, header.SarDen, header.Chroma, header.BitDepth, header.ColorSpaceTag, ParseDuration(result.StandardError),
                origin, origin.HasValue && !PreviewClockMap.IsNearTimestampWrap(origin.Value));
            lock (_sync)
            {
                if (info.Duration == null && _durationCache.TryGetValue(key with { Capabilities = new() }, out var knownDuration))
                    info = info with { Duration = knownDuration };
                _cache.Add(key, info);
            }
            return info;
        }
        finally { _gate.Release(); }
    }

    /// <summary>自然終了までログを読み、画像検査と独立して入力の長さを取得する。終了コードは判定しない。</summary>
    /// <remarks>長さ不明の結果もキャッシュする。画像検査で長さが取得済みなら追加のプロセスは起動しない。総時間の上限は画像検査と同じ。</remarks>
    /// <exception cref="InputProbeTimeoutException">自然終了までの長さ検査が指定時間内に終わらなかった。</exception>
    public async Task<TimeSpan?> ProbeDurationAsync(string encoderPath, string inputFile,
        IReadOnlyList<string>? inputArgs = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFile);
        CancellationToken lifetime;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lifetime = _lifetime.Token;
        }
        string[] arguments = inputArgs?.ToArray() ?? new[] { "--avhw" };
        var key = new ProbeKey(encoderPath, inputFile, JsonSerializer.Serialize(arguments), new());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        await _durationGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                var known = _cache.FirstOrDefault(pair => SameInput(pair.Key, key) && pair.Value.Duration.HasValue).Value;
                if (known != null) return known.Duration;
                if (_durationCache.TryGetValue(key, out var cached)) return cached;
            }
            var command = arguments.Concat(new[]
            {
                "-i", inputFile, "--log-level", "input=debug", "--frames", "1", "-c", "raw", "-o", "-",
            });
            Process? process = null;
            int pid = 0;
            Task<string>? stderr = null;
            Task completion = Task.CompletedTask;
            CancellationTokenRegistration registration = default;
            CleanupDeadline? cleanup = null;
            TimeSpan Remaining() => (cleanup ??= new CleanupDeadline(_clock, StopTimeout)).Remaining;
            async Task StopIfOwnerAsync()
            {
                if (process == null) return;
                Task? owned;
                // 通常終了・期限切れ・Disposeのうち、一度だけ取り出せた側が回収・破棄する。
                lock (_sync) _durationProcesses.Remove(process, out owned);
                if (owned != null) await StopDurationProcessAsync(process, owned, Remaining()).ConfigureAwait(false);
            }
            try
            {
                lock (_sync)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    process = ProcessSupport.Start(encoderPath, command, _configureStartInfo);
                    pid = process.Id;
                    var pending = new List<Task>();
                    try
                    {
                        // 終了待ちは一度だけ作り、回収側がDispose後に新しい待ちを登録しないようにする。
                        pending.Add(process.WaitForExitAsync());
                        pending.Add(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null));
                        stderr = process.StandardError.ReadToEndAsync();
                        pending.Add(stderr);
                    }
                    finally
                    {
                        // 起動・pipe準備と登録を同じロックに収め、回収側へ完了待ち一式を渡す。
                        completion = Task.WhenAll(pending);
                        _durationProcesses.Add(process, completion);
                    }
                }
                registration = cancellation.Token.Register(() => ProcessSupport.Observe(Task.Run(() => ProcessSupport.Kill(process))));
                try { await completion.WaitAsync(_timeout, _clock, cancellation.Token).ConfigureAwait(false); }
                catch (TimeoutException exception)
                {
                    await ProcessSupport.JoinAsync(cancellation.CancelAsync(), _clock, Remaining(), "probe.duration_cancel", pid).ConfigureAwait(false);
                    await StopIfOwnerAsync().ConfigureAwait(false);
                    await ProcessSupport.JoinAsync(completion, _clock, Remaining(), "probe.duration_timeout", pid).ConfigureAwait(false);
                    throw new InputProbeTimeoutException(CoreText.InputDurationTimedOut,
                        stderr!.IsCompletedSuccessfully ? stderr.Result : "", exception);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                TimeSpan? duration = ParseDuration(await stderr!.ConfigureAwait(false));
                lock (_sync)
                {
                    _durationCache.Add(key, duration);
                    if (duration.HasValue)
                        foreach (var imageKey in _cache.Keys.Where(candidate => SameInput(candidate, key)).ToArray())
                            _cache[imageKey] = _cache[imageKey] with { Duration = duration };
                }
                return duration;
            }
            finally
            {
                registration.Dispose();
                await StopIfOwnerAsync().ConfigureAwait(false);
            }
        }
        finally { _durationGate.Release(); }
    }

    /// <summary>入力ログのDuration行を読む。N/A、欠落、不正な値やTimeSpanの範囲外ならnull。</summary>
    public static TimeSpan? ParseDuration(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        // ログ色指定が数値の前後へ入っても同じ値として読めるようにする。
        string plain = Regex.Replace(standardError, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        var line = Regex.Match(plain, @"\bDuration:\s*(?<value>[^,\r\n]*)", RegexOptions.CultureInvariant);
        if (!line.Success) return null;
        var match = Regex.Match(line.Groups["value"].Value.Trim(),
            @"^(?<hours>[0-9]+):(?<minutes>[0-9]{2}):(?<seconds>[0-9]{2})(?:\.(?<fraction>[0-9]{1,7}))?$",
            RegexOptions.CultureInvariant);
        if (!match.Success
            || !long.TryParse(match.Groups["hours"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long hours)
            || !int.TryParse(match.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes >= 60
            || !int.TryParse(match.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds >= 60)
            return null;
        string fractionText = match.Groups["fraction"].Value.PadRight(7, '0');
        int fraction = int.Parse(fractionText, NumberStyles.None, CultureInfo.InvariantCulture);
        try
        {
            return TimeSpan.FromTicks(checked(hours * TimeSpan.TicksPerHour
                + minutes * TimeSpan.TicksPerMinute + seconds * TimeSpan.TicksPerSecond + fraction));
        }
        catch (OverflowException) { return null; }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask != null) return new(_disposeTask);
            _disposed = true;
            _disposeTask = DisposeCoreAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var deadline = new CleanupDeadline(_clock, StopTimeout);
        Task cancellation = Task.Run(async () => await _lifetime.CancelAsync().ConfigureAwait(false));
        Task backend = Task.Run(async () => await _backend.DisposeAsync().ConfigureAwait(false));
        KeyValuePair<Process, Task>[] processes;
        lock (_sync)
        {
            processes = _durationProcesses.ToArray();
            _durationProcesses.Clear();
        }
        Task[] reaps = processes.Select(pair => StopDurationProcessAsync(pair.Key, pair.Value, deadline.Remaining)).ToArray();
        await JoinIgnoringFailureAsync(cancellation, deadline, "probe.cancel").ConfigureAwait(false);
        await JoinIgnoringFailureAsync(backend, deadline, "probe.backend_dispose").ConfigureAwait(false);
        await JoinIgnoringFailureAsync(Task.WhenAll(reaps), deadline, "probe.duration_reap").ConfigureAwait(false);
        Task imageGate = ClearAfterGateAsync(_gate, () => _cache.Clear());
        Task durationGate = ClearAfterGateAsync(_durationGate, () => _durationCache.Clear());
        await JoinIgnoringFailureAsync(imageGate, deadline, "probe.image_gate").ConfigureAwait(false);
        await JoinIgnoringFailureAsync(durationGate, deadline, "probe.duration_gate").ConfigureAwait(false);
        Task resources = Task.WhenAll(new[] { cancellation, backend, imageGate, durationGate }.Concat(reaps));
        ProcessSupport.Observe(resources);
        if (resources.IsCompleted) _lifetime.Dispose();
        else ProcessSupport.Observe(resources.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default));
    }

    private async Task StopDurationProcessAsync(Process process, Task completion, TimeSpan timeout)
    {
        var deadline = new CleanupDeadline(_clock, timeout);
        int pid = process.Id;
        Task kill = Task.Run(() => ProcessSupport.Kill(process));
        bool complete = false;
        try
        {
            if (await ProcessSupport.JoinAsync(kill, _clock, deadline.Remaining, "probe.duration_kill", pid).ConfigureAwait(false))
            {
                try { complete = await ProcessSupport.JoinAsync(completion, _clock, deadline.Remaining, "probe.duration_pipes", pid).ConfigureAwait(false); }
                catch { complete = completion.IsCompleted; }
            }
        }
        finally
        {
            // 終了・pipe読取とOS操作がすべて終わるまでProcessを破棄しない。
            if (complete) process.Dispose();
            else ProcessSupport.DisposeAfter(process, kill, completion);
        }
    }

    private async Task ClearAfterGateAsync(SemaphoreSlim gate, Action clear)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { lock (_sync) clear(); }
        finally { gate.Release(); }
    }

    private async Task JoinIgnoringFailureAsync(Task task, CleanupDeadline deadline, string stage)
    {
        try { await ProcessSupport.JoinAsync(task, _clock, deadline.Remaining, stage).ConfigureAwait(false); }
        catch (Exception exception) { System.Diagnostics.Trace.TraceWarning("Input probe shutdown failure ({0}): {1}", stage, exception.Message); }
    }

    private async Task<string> StopProbeRunAsync(Task<PreviewRunResult> run, CancellationTokenSource cancellation)
    {
        var deadline = new CleanupDeadline(_clock, StopTimeout);
        await JoinIgnoringFailureAsync(cancellation.CancelAsync(), deadline, "probe.run_cancel").ConfigureAwait(false);
        Task abort = Task.Run(async () => await _backend.AbortAsync().ConfigureAwait(false));
        await JoinIgnoringFailureAsync(abort, deadline, "probe.run_abort").ConfigureAwait(false);
        await JoinIgnoringFailureAsync(run, deadline, "probe.run_join").ConfigureAwait(false);
        return run.IsCompletedSuccessfully ? run.Result.StandardError : "";
    }

    private static bool SameInput(ProbeKey a, ProbeKey b) => a.EncoderPath == b.EncoderPath
        && a.InputFile == b.InputFile && a.InputArgs == b.InputArgs;

    private sealed record ProbeKey(string EncoderPath, string InputFile, string InputArgs, PreviewCapabilities? Capabilities = null);

    private sealed class ProbeSink(Action stop) : IPreviewSink
    {
        public Y4mHeader? Header { get; private set; }
        public bool HasFrame { get; private set; }
        public double? FirstTimestamp { get; private set; }
        public void OnHeader(Y4mHeader header) => Header = header;
        public void OnFrame(Y4mFrame frame)
        {
            FirstTimestamp ??= frame.Xts;
            HasFrame = true;
            stop();
        }
        public void OnOutput() { }
    }
}

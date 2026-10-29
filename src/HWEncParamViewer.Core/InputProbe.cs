using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HWEncParamViewer.Core;

/// <summary>最初のフレームと入力ログから得た画像形式と長さ。長さ不明の場合はDurationがnull。</summary>
public sealed record InputInfo(int Width, int Height, int FpsNum, int FpsDen, int SarNum, int SarDen,
    Y4mChroma Chroma, int BitDepth, string ColorSpaceTag, TimeSpan? Duration = null)
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
    private readonly HashSet<Process> _durationProcesses = new();
    private readonly Action<ProcessStartInfo>? _configureStartInfo;
    private readonly OneShotBackend _backend;
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
        _backend = new OneShotBackend(configureStartInfo);
        _configureStartInfo = configureStartInfo;
    }

    /// <summary>追加引数の既定は--avhw。引数配列を複製し、値が同じ要求にはキャッシュを返す。</summary>
    /// <exception cref="InputProbeException">エンコーダがエラー終了した、または有効なヘッダがない。</exception>
    /// <exception cref="InputProbeTimeoutException">検査全体が指定時間内に完了しなかった。</exception>
    public async Task<InputInfo> ProbeAsync(string encoderPath, string inputFile,
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
        var key = new ProbeKey(encoderPath, inputFile, JsonSerializer.Serialize(arguments));
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
                cancellation.Cancel();
                await _backend.AbortAsync().ConfigureAwait(false);
                string diagnostics = await ReadDiagnosticsAsync(run).ConfigureAwait(false);
                throw new InputProbeTimeoutException("入力情報の取得がタイムアウトしました", diagnostics, exception);
            }
            catch (OperationCanceledException)
            {
                cancellation.Cancel();
                await _backend.AbortAsync().ConfigureAwait(false);
                await ObserveAsync(run).ConfigureAwait(false);
                throw;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            // 自己中断と外部のキャンセルは区別する。最初のフレーム取得後の自己中断だけを成功扱いにする。
            if (!sink.HasFrame && result.ExitCode != 0)
                throw new InputProbeException($"入力情報の取得でエンコーダがエラー終了しました: {result.ExitCode}", result.ExitCode, result.StandardError);
            if (!sink.HasFrame || sink.Header is not { } header || header.Fps <= 0 || !double.IsFinite(header.Fps))
                throw new InputProbeException("入力情報の取得で有効なfps付きヘッダとフレームを受信できませんでした", -1, result.StandardError);
            var info = new InputInfo(header.Width, header.Height, header.FpsNum, header.FpsDen,
                header.SarNum, header.SarDen, header.Chroma, header.BitDepth, header.ColorSpaceTag, ParseDuration(result.StandardError));
            lock (_sync)
            {
                if (info.Duration == null && _durationCache.TryGetValue(key, out var knownDuration))
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
        var key = new ProbeKey(encoderPath, inputFile, JsonSerializer.Serialize(arguments));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        await _durationGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_cache.TryGetValue(key, out var input) && input.Duration.HasValue) return input.Duration;
                if (_durationCache.TryGetValue(key, out var cached)) return cached;
            }
            var command = arguments.Concat(new[]
            {
                "-i", inputFile, "--log-level", "input=debug", "--frames", "1", "-c", "raw", "-o", "-",
            });
            Process process;
            lock (_sync)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                process = ProcessSupport.Start(encoderPath, command, _configureStartInfo);
                _durationProcesses.Add(process);
            }
            Task? stdout = null;
            Task<string>? stderr = null;
            Task? completion = null;
            using var registration = cancellation.Token.Register(() => ProcessSupport.Kill(process));
            try
            {
                // 失敗終了時はy4mでないこともある。stdoutは解析せず読み切り、stderrと並行して詰まりを防ぐ。
                stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                stderr = process.StandardError.ReadToEndAsync();
                completion = Task.WhenAll(process.WaitForExitAsync(), stdout, stderr);
                try { await completion.WaitAsync(_timeout, _clock, cancellation.Token).ConfigureAwait(false); }
                catch (TimeoutException exception)
                {
                    cancellation.Cancel();
                    await ProcessSupport.ReapAsync(process).ConfigureAwait(false);
                    await ObserveAsync(completion).ConfigureAwait(false);
                    throw new InputProbeTimeoutException("入力の長さの取得がタイムアウトしました",
                        await ReadStandardErrorAsync(stderr).ConfigureAwait(false), exception);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                TimeSpan? duration = ParseDuration(await stderr.ConfigureAwait(false));
                lock (_sync)
                {
                    _durationCache.Add(key, duration);
                    if (_cache.TryGetValue(key, out var input) && duration.HasValue)
                        _cache[key] = input with { Duration = duration };
                }
                return duration;
            }
            finally
            {
                try
                {
                    await ProcessSupport.ReapAsync(process).ConfigureAwait(false);
                    if (completion != null) await ObserveAsync(completion).ConfigureAwait(false);
                    else
                    {
                        if (stdout != null) await ObserveAsync(stdout).ConfigureAwait(false);
                        if (stderr != null) await ObserveAsync(stderr).ConfigureAwait(false);
                    }
                }
                finally
                {
                    registration.Dispose();
                    lock (_sync) _durationProcesses.Remove(process);
                    process.Dispose();
                }
            }
        }
        finally { _durationGate.Release(); }
    }

    private static async Task<string> ReadStandardErrorAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return ""; }
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

    private static async Task<string> ReadDiagnosticsAsync(Task<PreviewRunResult> task)
    {
        try { return (await task.ConfigureAwait(false)).StandardError; }
        catch (PreviewBackendException exception) { return exception.StandardError; }
        catch { return ""; }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { /* 呼び出し元の中断・タイムアウトを優先する。 */ }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask != null) return new(_disposeTask);
            _disposed = true;
            _lifetime.Cancel();
            _disposeTask = DisposeCoreAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Process[] processes;
        lock (_sync) processes = _durationProcesses.ToArray();
        foreach (var process in processes) ProcessSupport.Kill(process);
        await _backend.DisposeAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { lock (_sync) _cache.Clear(); }
        finally { _gate.Release(); }
        await _durationGate.WaitAsync().ConfigureAwait(false);
        try { lock (_sync) _durationCache.Clear(); }
        finally { _durationGate.Release(); }
        _lifetime.Dispose();
    }

    private sealed record ProbeKey(string EncoderPath, string InputFile, string InputArgs);

    private sealed class ProbeSink(Action stop) : IPreviewSink
    {
        public Y4mHeader? Header { get; private set; }
        public bool HasFrame { get; private set; }
        public void OnHeader(Y4mHeader header) => Header = header;
        public void OnFrame(Y4mFrame frame)
        {
            HasFrame = true;
            stop();
        }
        public void OnOutput() { }
    }
}

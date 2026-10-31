using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HWEncParamViewer.Core;

/// <summary>常駐プロトコルを順次実行する。重なった要求は先行要求を中断してから開始する。</summary>
public sealed class ServerBackend : IPreviewBackend
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _readyTimeout;
    private readonly TimeSpan _cancelTimeout;
    private readonly ServerRestartPolicy _restartPolicy;
    private readonly Action<ProcessStartInfo>? _configure;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private Session? _session;
    private CancellationTokenSource? _latest;
    private bool _disposed;
    private long _nextId;

    public ServerBackend(TimeProvider? timeProvider = null, TimeSpan? readyTimeout = null,
        TimeSpan? cancelTimeout = null, ServerRestartPolicy? restartPolicy = null,
        Action<ProcessStartInfo>? configureStartInfo = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(3);
        _cancelTimeout = cancelTimeout ?? TimeSpan.FromSeconds(5);
        if (_readyTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(readyTimeout));
        if (_cancelTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cancelTimeout));
        _restartPolicy = restartPolicy ?? new ServerRestartPolicy();
        _configure = configureStartInfo;
    }

    public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink,
        CancellationToken cancellationToken = default)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _latest?.Cancel();
            _latest = execution;
        }
        bool entered = false;
        Session? session = null;
        Task<string>? requestLog = null;
        try
        {
            await _runGate.WaitAsync(execution.Token).ConfigureAwait(false);
            entered = true;
            execution.Token.ThrowIfCancellationRequested();
            session = await GetSessionAsync(request.EncoderPath, sink, execution.Token).ConfigureAwait(false);
            long id = Interlocked.Increment(ref _nextId);
            Task<string> logTask = session.RegisterLog(id);
            requestLog = logTask;
            if (PerfTrace.Enabled) PerfTrace.Write("server.run_send", new { request.CorrelationId, id,
                pid = session.Process.Id, request.TargetSeconds, frames = CommandBuilder.Plan(request).FrameCount });
            await session.SendAsync(new { cmd = "run", id, args = CommandBuilder.BuildServer(request) }).ConfigureAwait(false);
            var readTask = Task.Run(() => ReadRequest(session, id, request.CorrelationId, sink, execution.Token));
            session.LastRead = readTask;
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = execution.Token.Register(() => cancelled.TrySetResult());
            bool wasCancelled = await Task.WhenAny(readTask, cancelled.Task).ConfigureAwait(false) != readTask
                || execution.IsCancellationRequested;
            PreviewEnd end;
            if (wasCancelled)
            {
                try
                {
                    long cancelStart = 0;
                    if (PerfTrace.Enabled)
                    {
                        cancelStart = Stopwatch.GetTimestamp();
                        PerfTrace.Write("server.cancel_send", new { request.CorrelationId, id });
                    }
                    await session.SendAsync(new { cmd = "cancel", id }).ConfigureAwait(false);
                    end = await readTask.WaitAsync(_cancelTimeout, _timeProvider).ConfigureAwait(false);
                    if (PerfTrace.Enabled) PerfTrace.Write("server.cancel_end", new { request.CorrelationId, id, end.Rc,
                        elapsedMs = Stopwatch.GetElapsedTime(cancelStart).TotalMilliseconds });
                }
                catch
                {
                    if (PerfTrace.Enabled) PerfTrace.Write("server.cancel_drop", new { request.CorrelationId, id });
                    await DropSessionAsync(session).ConfigureAwait(false);
                    return new PreviewRunResult(-12, await logTask.ConfigureAwait(false), true);
                }
            }
            else end = await readTask.ConfigureAwait(false);

            // stdoutとstderrは別パイプなので、stdoutのENDだけでは要求ログが揃うとは限らない。
            string log = await logTask.WaitAsync(_cancelTimeout, _timeProvider).ConfigureAwait(false);
            if (PerfTrace.Enabled) PerfTrace.Write("server.request_complete", new { request.CorrelationId, id,
                end.Rc, wasCancelled });
            session.CompletedRequests++;
            if (_restartPolicy.ShouldRestart(session.CompletedRequests, end.Rc, processExited: session.Process.HasExited))
                await DropSessionAsync(session).ConfigureAwait(false);
            return new PreviewRunResult(end.Rc, log, wasCancelled);
        }
        catch (Exception) when (execution.IsCancellationRequested)
        {
            if (session != null) await DropSessionAsync(session).ConfigureAwait(false);
            string diagnostics = requestLog == null ? "" : await requestLog.ConfigureAwait(false);
            return new PreviewRunResult(-12, diagnostics, true);
        }
        catch (PreviewServerNotSupportedException) { throw; }
        catch (Exception exception)
        {
            if (session != null) await DropSessionAsync(session).ConfigureAwait(false);
            string diagnostics = requestLog == null ? "" : await requestLog.ConfigureAwait(false);
            throw new PreviewBackendException("常駐サーバの要求を完了できませんでした", diagnostics, exception);
        }
        finally
        {
            if (session != null) session.Sink = null;
            lock (_sync) { if (ReferenceEquals(_latest, execution)) _latest = null; }
            if (entered) _runGate.Release();
        }
    }

    private static PreviewEnd ReadRequest(Session session, long id, long correlationId, IPreviewSink sink, CancellationToken token)
    {
        bool begun = false;
        int frames = 0;
        long bytes = 0;
        double? firstFrameMs = null;
        double? lastFrameMs = null;
        long readStart = PerfTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
        while (session.Reader.ReadEvent() is { } item)
        {
            switch (item)
            {
                case PreviewBegin begin when !begun && begin.Id == id:
                    begun = true;
                    break;
                case PreviewHeader header when begun:
                    if (!token.IsCancellationRequested) sink.OnHeader(header.Header);
                    break;
                case PreviewFrame frame when begun:
                    if (PerfTrace.Enabled)
                    {
                        frames++;
                        bytes += frame.Frame.Data.LongLength;
                        lastFrameMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                        if (firstFrameMs == null)
                        {
                            firstFrameMs = lastFrameMs;
                            PerfTrace.Write("server.first_frame", new { correlationId, id, frame.Frame.Xts,
                                elapsedMs = firstFrameMs });
                        }
                    }
                    if (!token.IsCancellationRequested) sink.OnFrame(frame.Frame);
                    break;
                case PreviewEnd end when begun && end.Id == id:
                    if (PerfTrace.Enabled) PerfTrace.Write("server.end", new { correlationId, id, end.Rc,
                        frames, bytes, firstFrameMs, lastFrameMs,
                        elapsedMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds,
                        cancelled = token.IsCancellationRequested });
                    return end;
                default:
                    throw new PreviewProtocolException("要求と一致しない常駐プロトコルのイベントを受信しました");
            }
        }
        throw new EndOfStreamException("ENDの前にエンコーダが終了しました");
    }

    private async Task<Session> GetSessionAsync(string path, IPreviewSink sink, CancellationToken token)
    {
        Session? session;
        lock (_sync) session = _session;
        if (session != null && (session.Path != path || session.Process.HasExited))
        {
            await DropSessionAsync(session).ConfigureAwait(false);
            session = null;
        }
        if (session != null)
        {
            session.Sink = sink;
            return session;
        }
        if (PerfTrace.Enabled) PerfTrace.Write("server.process_start", new { path });
        session = new Session(path, ProcessSupport.Start(path, ["--preview-server"], _configure));
        session.Sink = sink;
        lock (_sync) _session = session;
        var readyTask = Task.Run(session.Reader.ReadEvent);
        session.LastRead = readyTask;
        try
        {
            PreviewEvent? ready = await readyTask.WaitAsync(_readyTimeout, _timeProvider, token).ConfigureAwait(false);
            if (ready is not PreviewReady info || !info.Values.TryGetValue("version", out string? version) || version != "1")
                throw new PreviewServerNotSupportedException("対応するREADYを受信できませんでした");
            if (PerfTrace.Enabled) PerfTrace.Write("server.ready", new { pid = session.Process.Id });
            return session;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await DropSessionAsync(session).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await DropSessionAsync(session).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new PreviewServerNotSupportedException("常駐サーバのハンドシェイクに失敗しました", exception);
        }
    }

    private async Task DropSessionAsync(Session session)
    {
        lock (_sync) { if (ReferenceEquals(_session, session)) _session = null; }
        await session.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask AbortAsync()
    {
        Session? session;
        lock (_sync)
        {
            _latest?.Cancel();
            session = _session;
        }
        if (session != null) await DropSessionAsync(session).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _latest?.Cancel();
        }
        await AbortAsync().ConfigureAwait(false);
        await _runGate.WaitAsync().ConfigureAwait(false);
        _runGate.Release();
    }

    private sealed class Session
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Dictionary<long, RequestLog> _logs = [];
        private readonly Task _stderrTask;
        private Task? _stopTask;
        private long? _currentLog;
        public Session(string path, Process process)
        {
            Path = path;
            Process = process;
            Reader = new PreviewProtocolReader(new ProgressReadStream(process.StandardOutput.BaseStream,
                () => Volatile.Read(ref _sink)?.OnOutput()));
            _stderrTask = DrainErrorAsync();
        }
        private IPreviewSink? _sink;
        public IPreviewSink? Sink { set => Volatile.Write(ref _sink, value); }
        public string Path { get; }
        public Process Process { get; }
        public PreviewProtocolReader Reader { get; }
        public Task? LastRead { get; set; }
        public int CompletedRequests { get; set; }

        public Task<string> RegisterLog(long id)
        {
            lock (_sync)
            {
                var log = new RequestLog();
                _logs.Add(id, log);
                return log.Completion.Task;
            }
        }

        public async Task SendAsync<T>(T command)
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command)).ConfigureAwait(false);
                await Process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
        }

        private async Task DrainErrorAsync()
        {
            try
            {
                while (await Process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (_sync)
                    {
                        long? begin = ParseId(line, "RGYPREVIEW BEGIN id=");
                        if (begin.HasValue) _currentLog = begin;
                        if (_currentLog is long current && _logs.TryGetValue(current, out var log))
                        {
                            log.Text.AppendLine(line);
                            if (ParseId(line, "RGYPREVIEW END id=") == current)
                            {
                                log.Completion.TrySetResult(log.Text.ToString());
                                _logs.Remove(current);
                                _currentLog = null;
                            }
                        }
                    }
                }
            }
            finally
            {
                lock (_sync)
                {
                    foreach (var log in _logs.Values) log.Completion.TrySetResult(log.Text.ToString());
                    _logs.Clear();
                }
            }
        }

        private static long? ParseId(string line, string prefix)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string value = line[prefix.Length..].Split(' ')[0];
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : null;
        }

        public Task StopAsync()
        {
            lock (_sync) return _stopTask ??= StopCoreAsync();
        }

        private async Task StopCoreAsync()
        {
            await ProcessSupport.ReapAsync(Process).ConfigureAwait(false);
            if (LastRead != null) { try { await LastRead.ConfigureAwait(false); } catch { } }
            await _stderrTask.ConfigureAwait(false);
            Process.Dispose();
        }

        private sealed class RequestLog
        {
            public StringBuilder Text { get; } = new();
            public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

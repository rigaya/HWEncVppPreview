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
    private readonly TimeSpan _stopTimeout;
    private readonly ServerRestartPolicy _restartPolicy;
    private readonly Action<ProcessStartInfo>? _configure;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private Session? _session;
    private CancellationTokenSource? _latest;
    private bool _disposed;
    private Task? _disposeTask;
    private readonly List<Task> _pendingCleanup = [];
    private long _nextId;

    public ServerBackend(TimeProvider? timeProvider = null, TimeSpan? readyTimeout = null,
        TimeSpan? cancelTimeout = null, ServerRestartPolicy? restartPolicy = null,
        Action<ProcessStartInfo>? configureStartInfo = null, TimeSpan? stopTimeout = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(3);
        _cancelTimeout = cancelTimeout ?? TimeSpan.FromSeconds(5);
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        if (_stopTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        if (_readyTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(readyTimeout));
        if (_cancelTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cancelTimeout));
        _restartPolicy = restartPolicy ?? new ServerRestartPolicy();
        _configure = configureStartInfo;
    }

    public async Task<string?> GetReadyEncoderAsync(string encoderPath, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                var session = await GetSessionAsync(encoderPath, null, cancellationToken).ConfigureAwait(false);
                return session.ReadyEncoder;
            }
            catch (OperationCanceledException) { throw; }
            catch (PreviewServerNotSupportedException) { throw; }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new PreviewServerNotSupportedException(CoreText.ServerHandshakeFailed, exception);
            }
        }
        finally { _runGate.Release(); }
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
            // 計画後にプロセスが再起動しても、別実装へ暖機0を流用しない。
            if (request.WarmupSeconds == 0 && !PreviewWarmupPolicy.AllowsZeroWarmup(session.ReadyEncoder))
                throw new PreviewServerNotSupportedException(CoreText.ServerHandshakeFailed);
            long id = Interlocked.Increment(ref _nextId);
            Task<string> logTask = session.RegisterLog(id);
            requestLog = logTask;
            if (PerfTrace.Enabled) PerfTrace.Write("server.run_send", new { request.CorrelationId, id,
                pid = session.Process.Id, request.TargetSeconds, continuous = request.Continuous,
                frames = request.Continuous ? (int?)null : CommandBuilder.Plan(request).FrameCount,
                timestampPassthrough = request.Capabilities.TimestampPassthrough,
                seekSeconds = CommandBuilder.Plan(request).SeekSeconds, request.PreMarginSeconds, request.PostMarginSeconds, request.WarmupSeconds });
            await session.SendAsync(new { cmd = "run", id, args = CommandBuilder.BuildServer(request) }).ConfigureAwait(false);
            var readTask = Task.Run(() => ReadRequestAsync(session, id, request.CorrelationId, sink, execution.Token));
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
                    return new PreviewRunResult(-12, await session.ReadLogAsync(logTask).ConfigureAwait(false), true);
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
            string diagnostics = requestLog == null ? "" : await session!.ReadLogAsync(requestLog).ConfigureAwait(false);
            return new PreviewRunResult(-12, diagnostics, true);
        }
        catch (PreviewServerNotSupportedException) { throw; }
        catch (Exception exception)
        {
            if (session != null) await DropSessionAsync(session).ConfigureAwait(false);
            string diagnostics = requestLog == null ? "" : await session!.ReadLogAsync(requestLog).ConfigureAwait(false);
            throw new PreviewBackendException(CoreText.ServerRequestFailed, diagnostics, exception);
        }
        finally
        {
            if (session != null) session.Sink = null;
            lock (_sync) { if (ReferenceEquals(_latest, execution)) _latest = null; }
            if (entered) _runGate.Release();
        }
    }

    private static async Task<PreviewEnd> ReadRequestAsync(Session session, long id, long correlationId, IPreviewSink sink, CancellationToken token)
    {
        bool begun = false;
        int frames = 0;
        long bytes = 0;
        double? firstFrameMs = null;
        double? lastFrameMs = null;
        long readStart = PerfTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
        while (true)
        {
            if (!token.IsCancellationRequested)
            {
                try { await PreviewReadGate.WaitAsync(sink, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // 取消後は需要待ちを迂回する。fwriteの背圧を解き、ENDまで必ず排出する。
                    // Cancelのコールバック内で同期読取に入ると、cancel送信やkillへ進めなくなる。
                    await Task.Yield();
                }
            }
            long eventStart = PerfTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
            var item = session.Reader.ReadEvent();
            if (PerfTrace.Enabled) PerfTrace.Write("server.read", new { correlationId, id,
                kind = item?.GetType().Name ?? "EOF", Xts = (item as PreviewFrame)?.Frame.Xts,
                elapsedMs = Stopwatch.GetElapsedTime(eventStart).TotalMilliseconds });
            if (item is null) break;
            switch (item)
            {
                case PreviewBegin begin when !begun && begin.Id == id:
                    if (PerfTrace.Enabled) PerfTrace.Write("server.begin", new { correlationId, id });
                    begun = true;
                    break;
                case PreviewHeader header when begun:
                    if (PerfTrace.Enabled) PerfTrace.Write("server.header", new { correlationId, id,
                        header.Header.Width, header.Header.Height, header.Header.Fps, header.Header.FrameBytes });
                    if (!token.IsCancellationRequested) sink.OnHeader(header.Header);
                    break;
                case PreviewFrame frame when begun:
                    if (PerfTrace.Enabled)
                    {
                        frames++;
                        bytes += frame.Frame.Data.LongLength;
                        lastFrameMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                        PerfTrace.Write("server.frame", new { correlationId, id, index = frames - 1, frame.Frame.Xts,
                            bytes = frame.Frame.Data.Length, elapsedMs = lastFrameMs });
                        if (firstFrameMs == null)
                        {
                            firstFrameMs = lastFrameMs;
                            PerfTrace.Write("server.first_frame", new { correlationId, id, frame.Frame.Xts,
                                elapsedMs = firstFrameMs });
                        }
                    }
                    if (!token.IsCancellationRequested)
                    {
                        // 受信後のキャッシュ更新と選択処理を、パイプ待ちから分けて観測する。
                        long sinkStart = PerfTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
                        sink.OnFrame(frame.Frame);
                        if (PerfTrace.Enabled) PerfTrace.Write("server.sink_frame", new { correlationId, id,
                            frame.Frame.Xts, elapsedMs = Stopwatch.GetElapsedTime(sinkStart).TotalMilliseconds });
                    }
                    break;
                case PreviewEnd end when begun && end.Id == id:
                    if (PerfTrace.Enabled) PerfTrace.Write("server.end", new { correlationId, id, end.Rc,
                        frames, bytes, firstFrameMs, lastFrameMs,
                        elapsedMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds,
                        cancelled = token.IsCancellationRequested });
                    return end;
                default:
                    throw new PreviewProtocolException(CoreText.ServerEventMismatch);
            }
        }
        throw new EndOfStreamException(CoreText.EncoderExitedBeforeEnd);
    }

    private async Task<Session> GetSessionAsync(string path, IPreviewSink? sink, CancellationToken token)
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
        session = new Session(path, ProcessSupport.Start(path, ["--preview-server"], _configure), _timeProvider, _stopTimeout);
        session.Sink = sink;
        lock (_sync) _session = session;
        var readyTask = Task.Run(session.Reader.ReadEvent);
        session.LastRead = readyTask;
        try
        {
            PreviewEvent? ready = await readyTask.WaitAsync(_readyTimeout, _timeProvider, token).ConfigureAwait(false);
            if (ready is not PreviewReady info || !info.Values.TryGetValue("version", out string? version) || version != "1")
                throw new PreviewServerNotSupportedException(CoreText.ServerReadyUnavailable);
            session.ReadyEncoder = info.Values.GetValueOrDefault("encoder");
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
            throw new PreviewServerNotSupportedException(CoreText.ServerHandshakeFailed, exception);
        }
    }

    private async Task DropSessionAsync(Session session, TimeSpan? timeout = null)
    {
        lock (_sync) { if (ReferenceEquals(_session, session)) _session = null; }
        if (!await session.StopAsync(timeout: timeout).ConfigureAwait(false))
            lock (_sync) _pendingCleanup.Add(session.CleanupCompletion);
    }

    private async Task<bool> JoinPendingCleanupAsync(CleanupDeadline deadline)
    {
        Task[] pending;
        lock (_sync)
        {
            _pendingCleanup.RemoveAll(task => task.IsCompletedSuccessfully);
            pending = _pendingCleanup.ToArray();
        }
        // 過去の期限切れと、現在も残る未回収を区別する。遅れて回収できたsessionは除外する。
        bool completed = await ProcessSupport.JoinAsync(Task.WhenAll(pending), _timeProvider,
            deadline.Remaining, "server.pending_cleanup").ConfigureAwait(false);
        if (completed)
            lock (_sync) _pendingCleanup.RemoveAll(task => task.IsCompletedSuccessfully);
        return completed;
    }

    public async ValueTask AbortAsync()
    {
        var deadline = new CleanupDeadline(_timeProvider, _stopTimeout);
        Session? session;
        CancellationTokenSource? latest;
        lock (_sync) { latest = _latest; session = _session; }
        bool cancelled = true;
        try
        {
            if (latest != null) cancelled = await ProcessSupport.JoinAsync(latest.CancelAsync(), _timeProvider,
                deadline.Remaining, "server.abort_cancel").ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        if (session != null) await DropSessionAsync(session, deadline.Remaining).ConfigureAwait(false);
        bool recovered = await JoinPendingCleanupAsync(deadline).ConfigureAwait(false);
        if (!cancelled || !recovered) throw new TimeoutException(CoreText.ServerStopTimedOut);
    }

    public async ValueTask DisposeAsync()
    {
        Task disposal;
        bool first;
        lock (_sync)
        {
            first = _disposeTask == null;
            _disposed = true;
            disposal = _disposeTask ??= Task.Run(DisposeCoreAsync);
        }
        try { await disposal.ConfigureAwait(false); }
        catch (TimeoutException) when (!first)
        {
            // 初回の未回収は通知済み。再試行は、実際の遅延回収が終わった場合だけ成功する。
            if (!await JoinPendingCleanupAsync(new CleanupDeadline(_timeProvider, _stopTimeout)).ConfigureAwait(false))
                throw;
        }
    }

    private async Task DisposeCoreAsync()
    {
        var deadline = new CleanupDeadline(_timeProvider, _stopTimeout);
        Session? session;
        CancellationTokenSource? latest;
        lock (_sync)
        {
            latest = _latest;
            session = _session;
        }
        // 呼出し元は要求の協調取消を先行する。残る要求にもENDを返す猶予を与える。
        bool cancelled = true;
        try
        {
            if (latest != null) cancelled = await ProcessSupport.JoinAsync(latest.CancelAsync(), _timeProvider,
                deadline.Remaining, "server.dispose_cancel").ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        Task waitRun = _runGate.WaitAsync();
        bool entered = await ProcessSupport.JoinAsync(waitRun, _timeProvider, deadline.Remaining, "server.run_gate").ConfigureAwait(false);
        if (entered) _runGate.Release();
        else ProcessSupport.Observe(waitRun.ContinueWith(task => { if (task.IsCompletedSuccessfully) _runGate.Release(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        lock (_sync) session = _session ?? session;
        if (session != null)
        {
            lock (_sync) { if (ReferenceEquals(_session, session)) _session = null; }
            if (!await session.StopAsync(graceful: entered, timeout: deadline.Remaining).ConfigureAwait(false))
                lock (_sync) _pendingCleanup.Add(session.CleanupCompletion);
        }
        bool recovered = await JoinPendingCleanupAsync(deadline).ConfigureAwait(false);
        if (!cancelled || !entered || !recovered) throw new TimeoutException(CoreText.ServerCleanupTimedOut);
    }

    private sealed class Session
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Dictionary<long, RequestLog> _logs = [];
        private readonly Task _stderrTask;
        private Task<bool>? _stopTask;
        private readonly int _pid;
        private readonly TimeProvider _clock;
        private readonly TimeSpan _stopTimeout;
        private readonly TaskCompletionSource _forceStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long? _currentLog;
        public Session(string path, Process process, TimeProvider clock, TimeSpan stopTimeout)
        {
            Path = path;
            Process = process;
            _pid = process.Id;
            _clock = clock;
            _stopTimeout = stopTimeout;
            Reader = new PreviewProtocolReader(new ProgressReadStream(process.StandardOutput.BaseStream,
                () => Volatile.Read(ref _sink)?.OnOutput()));
            _stderrTask = DrainErrorAsync();
        }
        private IPreviewSink? _sink;
        public IPreviewSink? Sink { set => Volatile.Write(ref _sink, value); }
        public string? ReadyEncoder { get; set; }
        public string Path { get; }
        public Process Process { get; }
        public PreviewProtocolReader Reader { get; }
        public Task? LastRead { get; set; }
        public Task CleanupCompletion { get; private set; } = Task.CompletedTask;
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

        public async Task SendAsync<T>(T command, TimeSpan? timeout = null)
        {
            Task send = SendCoreAsync(command);
            if (!await ProcessSupport.JoinAsync(send, _clock, timeout ?? _stopTimeout, "server.send", _pid).ConfigureAwait(false))
                throw new TimeoutException(CoreText.ServerCommandTimedOut);
        }

        private async Task SendCoreAsync<T>(T command)
        {
            // 期限切れ後も実書込が完了するまで所有権を保ち、後続送信との混在を防ぐ。
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command)).ConfigureAwait(false);
                await Process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
        }

        public async Task<string> ReadLogAsync(Task<string> log)
        {
            return await ProcessSupport.JoinAsync(log, _clock, _stopTimeout, "server.log", _pid).ConfigureAwait(false)
                ? await log.ConfigureAwait(false) : CoreText.StandardErrorTimedOut;
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
                        if (PerfTrace.Enabled) PerfTrace.Write("server.stderr", new { pid = _pid, id = _currentLog, line });
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

        public Task<bool> StopAsync(bool graceful = false, TimeSpan? timeout = null)
        {
            if (!graceful) _forceStop.TrySetResult();
            // 同期先頭のOS操作やパイプ操作を呼出し元のロック内で実行しない。
            lock (_sync) return _stopTask ??= Task.Run(() => StopCoreAsync(graceful, timeout ?? _stopTimeout));
        }

        private async Task<bool> StopCoreAsync(bool graceful, TimeSpan timeout)
        {
            var deadline = new CleanupDeadline(_clock, timeout);
            int? pid = null;
            try { pid = Process.Id; } catch (InvalidOperationException) { }
            void Stage(string stage)
            {
                if (!PerfTrace.Enabled) return;
                PerfTrace.Write("server.stop.stage", new { stage, pid });
                PerfTrace.Flush();
            }
            bool exited = false;
            if (graceful && !_forceStop.Task.IsCompleted)
            {
                Stage("quit_begin");
                try
                {
                    await SendAsync(new { cmd = "quit" }, deadline.Remaining).ConfigureAwait(false);
                    Stage("quit_sent");
                    Task exit = Process.WaitForExitAsync();
                    Task first = await Task.WhenAny(exit, _forceStop.Task)
                        .WaitAsync(deadline.Remaining, _clock).ConfigureAwait(false);
                    if (first == exit) { await exit.ConfigureAwait(false); exited = true; }
                    else ProcessSupport.Observe(exit);
                }
                catch (TimeoutException) { ProcessSupport.WarnTimeout("server.quit", pid); }
                catch (IOException) { }
                catch (InvalidOperationException) { }
            }
            Stage("reap_begin");
            if (!exited) exited = await ProcessSupport.ReapBoundedAsync(Process, _clock, deadline.Remaining).ConfigureAwait(false);
            Stage("reap_end");
            Stage("read_begin");
            Task read = LastRead ?? Task.CompletedTask;
            bool readDone;
            try { readDone = await ProcessSupport.JoinAsync(read, _clock, deadline.Remaining, "server.read", pid).ConfigureAwait(false); }
            catch { readDone = true; }
            Stage("read_end");
            Stage("stderr_begin");
            bool errorDone;
            try { errorDone = await ProcessSupport.JoinAsync(_stderrTask, _clock, deadline.Remaining, "server.stderr", pid).ConfigureAwait(false); }
            catch { errorDone = true; }
            Stage("stderr_end");
            // 取消経路のログ待ちもここで打ち切れるよう、現在までの内容を確定する。
            lock (_sync)
            {
                foreach (var log in _logs.Values) log.Completion.TrySetResult(log.Text.ToString());
                _logs.Clear();
            }
            CleanupCompletion = ProcessSupport.DisposeAfterAsync(Process, read, _stderrTask, Process.WaitForExitAsync());
            bool complete = exited && readDone && errorDone;
            Stage(complete ? "complete" : "incomplete");
            return complete;
        }

        private sealed class RequestLog
        {
            public StringBuilder Text { get; } = new();
            public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

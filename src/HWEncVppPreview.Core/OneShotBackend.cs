using System.Diagnostics;

namespace HWEncVppPreview.Core;

/// <summary>要求ごとにプロセスを起動し、キャンセル時はツリー全体を停止する。</summary>
public sealed class OneShotBackend : IPreviewBackend, IRawOwnedBackend
{
    private readonly object _sync = new();
    private readonly Dictionary<Process, ActiveRun> _processes = [];
    private bool _disposed;
    private readonly List<Task> _pendingCleanup = [];
    private readonly Action<ProcessStartInfo>? _configure;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _stopTimeout;

    public OneShotBackend(Action<ProcessStartInfo>? configureStartInfo = null,
        TimeProvider? timeProvider = null, TimeSpan? stopTimeout = null)
    {
        _configure = configureStartInfo;
        _clock = timeProvider ?? TimeProvider.System;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        if (_stopTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stopTimeout));
    }

    public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        => RunCoreAsync(request, sink, cancellationToken, null);
    Task<PreviewRunResult> IRawOwnedBackend.RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink, RawOwnershipScope scope, CancellationToken cancellationToken)
        => RunCoreAsync(request, sink, cancellationToken, scope);
    private async Task<PreviewRunResult> RunCoreAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken, RawOwnershipScope? rawScope)
    {
        long runId = rawScope?.NewRunId() ?? 0;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = execution.Token;
        token.ThrowIfCancellationRequested();
        Process process;
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            process = ProcessSupport.Start(request.EncoderPath, CommandBuilder.BuildOneShot(request), _configure);
            _processes.Add(process, new ActiveRun(execution, finished));
        }
        using var registration = token.Register(() => ProcessSupport.Observe(Task.Run(() => ProcessSupport.Kill(process))));
        var errorTask = process.StandardError.ReadToEndAsync();
        var readTask = Task.Run(async () =>
        {
            await PreviewReadGate.WaitAsync(sink, token).ConfigureAwait(false);
            using var stream = new BufferedStream(new ProgressReadStream(process.StandardOutput.BaseStream, sink.OnOutput), 1 << 16);
            string? line = Y4mReader.ReadLine(stream);
            if (line == null) return;
            var header = Y4mHeader.Parse(line);
            if (!token.IsCancellationRequested) sink.OnHeader(header);
            var reader = new Y4mReader(stream, header);
            while (true)
            {
                await PreviewReadGate.WaitAsync(sink, token).ConfigureAwait(false);
                using var raw = rawScope == null ? null : reader.ReadOwnedFrame(rawScope, runId);
                var frame = rawScope == null ? reader.ReadFrame() : raw?.Frame;
                if (frame == null)
                    break;
                if (!token.IsCancellationRequested)
                    sink.OnFrame(frame);
            }
        });
        CleanupDeadline? cleanup = null;
        TimeSpan Remaining() => (cleanup ??= new CleanupDeadline(_clock, _stopTimeout)).Remaining;
        try
        {
            try { await readTask.WaitAsync(token).ConfigureAwait(false); }
            catch when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                await ProcessSupport.ReapBoundedAsync(process, _clock, Remaining()).ConfigureAwait(false);
                string diagnostics = await ReadErrorAsync(errorTask, Remaining()).ConfigureAwait(false);
                throw new PreviewBackendException(CoreText.OneShotReadFailed, diagnostics, exception);
            }
            bool exited = await ProcessSupport.JoinAsync(process.WaitForExitAsync(), _clock, Remaining(),
                "oneshot.exit", process.Id).ConfigureAwait(false);
            string error = await ReadErrorAsync(errorTask, Remaining()).ConfigureAwait(false);
            if (!exited && !token.IsCancellationRequested)
                throw new PreviewBackendException(CoreText.OneShotExitTimedOut, error, new TimeoutException());
            return new PreviewRunResult(exited ? process.ExitCode : -12, error, token.IsCancellationRequested);
        }
        finally
        {
            bool complete = true;
            // pipeがEOFを返さない場合も、各段階の期限で打ち切り未回収を記録する。
            try
            {
                complete &= await ProcessSupport.ReapBoundedAsync(process, _clock, Remaining()).ConfigureAwait(false);
                try { complete &= await ProcessSupport.JoinAsync(readTask, _clock, Remaining(), "oneshot.read", process.Id).ConfigureAwait(false); }
                catch { }
                try { complete &= await ProcessSupport.JoinAsync(errorTask, _clock, Remaining(), "oneshot.stderr", process.Id).ConfigureAwait(false); }
                catch { }
            }
            finally
            {
                Task resources = ProcessSupport.DisposeAfterAsync(process, readTask, errorTask, process.WaitForExitAsync());
                lock (_sync)
                {
                    _processes.Remove(process);
                    _pendingCleanup.Add(resources);
                }
                finished.TrySetResult(complete);
            }
        }
    }

    private async Task<string> ReadErrorAsync(Task<string> error, TimeSpan timeout) =>
        await ProcessSupport.JoinAsync(error, _clock, timeout, "oneshot.diagnostics").ConfigureAwait(false)
            ? await error.ConfigureAwait(false) : CoreText.StandardErrorTimedOut;

    public async ValueTask AbortAsync()
    {
        var deadline = new CleanupDeadline(_clock, _stopTimeout);
        var cancellations = new List<Task>();
        KeyValuePair<Process, ActiveRun>[] processes;
        lock (_sync) processes = _processes.ToArray();
        foreach (var entry in processes)
        {
            // プロセスを止めるだけでは、意図的に閉じたread gateから戻れない。
            try { cancellations.Add(entry.Value.Cancellation.CancelAsync()); }
            catch (ObjectDisposedException) { }
            ProcessSupport.Observe(Task.Run(() => ProcessSupport.Kill(entry.Key)));
        }
        bool cancelled = await ProcessSupport.JoinAsync(Task.WhenAll(cancellations), _clock,
            deadline.Remaining, "oneshot.abort_cancel").ConfigureAwait(false);
        // 終了通知はRunAsyncのfinallyがパイプを回収してから出す。
        bool joined = await ProcessSupport.JoinAsync(Task.WhenAll(processes.Select(entry => entry.Value.Finished.Task)),
            _clock, deadline.Remaining, "oneshot.abort").ConfigureAwait(false);
        Task[] pending;
        lock (_sync)
        {
            _pendingCleanup.RemoveAll(task => task.IsCompletedSuccessfully);
            pending = _pendingCleanup.ToArray();
        }
        // 終了待機の過去の期限切れではなく、実際のexit/read/stderrとDisposeの完了を確認する。
        bool recovered = await ProcessSupport.JoinAsync(Task.WhenAll(pending), _clock,
            deadline.Remaining, "oneshot.pending_cleanup").ConfigureAwait(false);
        if (recovered)
            lock (_sync) _pendingCleanup.RemoveAll(task => task.IsCompletedSuccessfully);
        if (!cancelled || !joined || !recovered) throw new TimeoutException(CoreText.OneShotCleanupTimedOut);
    }

    private sealed record ActiveRun(CancellationTokenSource Cancellation, TaskCompletionSource<bool> Finished);

    public async ValueTask DisposeAsync()
    {
        lock (_sync) _disposed = true;
        await AbortAsync().ConfigureAwait(false);
    }
}

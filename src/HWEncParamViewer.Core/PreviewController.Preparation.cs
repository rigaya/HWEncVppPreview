namespace HWEncParamViewer.Core;

public sealed partial class PreviewController
{
    private readonly CancellationTokenSource _auxiliaryLifetime = new();

    /// <summary>両方の所有backendの初期化を登録する。表示・cache・Failed通知には流さない。</summary>
    public Task InitializeEncoderAsync(string encoder, CancellationToken token = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Task[] initialization = new[] { _preferred, _backgroundPreferred }.Distinct().Select(InitializeAsync).ToArray();
            return Task.WhenAll(initialization);
        }

        async Task InitializeAsync(IPreviewBackend backend)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _auxiliaryLifetime.Token);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = new BackendRun();
            lock (_sync)
            {
                _tasks.Add(completion.Task);
                if (!_backendRuns.TryGetValue(backend, out var runs)) _backendRuns.Add(backend, runs = []);
                runs.Add(running);
            }
            try
            {
                Task task = backend.InitializeEncoderAsync(encoder, cancellation.Token);
                lock (_sync) running.Task = task;
                await task.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (PerfTrace.Enabled) PerfTrace.Write("core.initialize.failed", new { encoder, error = exception.Message });
            }
            finally
            {
                completion.TrySetResult();
                lock (_sync)
                {
                    _tasks.Remove(completion.Task);
                    if (_backendRuns.TryGetValue(backend, out var runs))
                    { runs.Remove(running); if (runs.Count == 0) _backendRuns.Remove(backend); }
                }
            }
        }
    }

    /// <summary>Controllerが所有する背景backendを入力検査中だけ貸す。画像通知とcache登録は行わない。</summary>
    public async Task<InputInfo> ProbeInputAsync(InputProbe probe, string encoder, string input,
        IReadOnlyList<string> inputArgs, PreviewCapabilities capabilities, CancellationToken token = default)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _auxiliaryLifetime.Token);
            _tasks.Add(completed.Task);
        }
        bool entered = false;
        try
        {
            await ExecutionFor(true).WaitAsync(cancellation.Token).ConfigureAwait(false);
            entered = true;
            IPreviewBackend backend;
            lock (_sync) backend = _unsupported.Contains(encoder) ? _backgroundFallback : _backgroundPreferred;
            try
            {
                return await probe.ProbeAsync(encoder, input, inputArgs, cancellation.Token, capabilities,
                    new ProbeBackendLease(this, backend)).ConfigureAwait(false);
            }
            catch (PreviewServerNotSupportedException) when (_enableFallback && ReferenceEquals(backend, _backgroundPreferred))
            {
                lock (_sync) _unsupported.Add(encoder);
                cancellation.Token.ThrowIfCancellationRequested();
                return await probe.ProbeAsync(encoder, input, inputArgs, cancellation.Token, capabilities,
                    new ProbeBackendLease(this, _backgroundFallback)).ConfigureAwait(false);
            }
        }
        finally
        {
            if (entered) ExecutionFor(true).Release();
            cancellation.Dispose();
            completed.TrySetResult();
            lock (_sync) _tasks.Remove(completed.Task);
        }
    }

    // 入力検査のRunも終了期限の追跡対象に入れ、借用側のDisposeで所有backendを破棄しない。
    private sealed class ProbeBackendLease(PreviewController owner, IPreviewBackend backend) : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => owner.RunTrackedBackendAsync(backend, request, sink, token, forceLegacy: true);
        public ValueTask AbortAsync() => backend.AbortAsync();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

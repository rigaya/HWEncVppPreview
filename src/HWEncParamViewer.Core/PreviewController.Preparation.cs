namespace HWEncParamViewer.Core;

public sealed partial class PreviewController
{
    private readonly CancellationTokenSource _auxiliaryLifetime = new();

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
            => owner.RunTrackedBackendAsync(backend, request, sink, token);
        public ValueTask AbortAsync() => backend.AbortAsync();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

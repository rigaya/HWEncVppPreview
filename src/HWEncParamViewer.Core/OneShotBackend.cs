using System.Diagnostics;

namespace HWEncParamViewer.Core;

/// <summary>要求ごとにプロセスを起動し、キャンセル時はツリー全体を停止する。</summary>
public sealed class OneShotBackend(Action<ProcessStartInfo>? configureStartInfo = null) : IPreviewBackend
{
    private readonly object _sync = new();
    private readonly Dictionary<Process, TaskCompletionSource> _processes = [];
    private bool _disposed;

    public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process process;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            process = ProcessSupport.Start(request.EncoderPath, CommandBuilder.BuildOneShot(request), configureStartInfo);
            _processes.Add(process, finished);
        }
        using var registration = cancellationToken.Register(() => ProcessSupport.Kill(process));
        var errorTask = process.StandardError.ReadToEndAsync();
        var readTask = Task.Run(() =>
        {
            using var stream = new BufferedStream(new ProgressReadStream(process.StandardOutput.BaseStream, sink.OnOutput), 1 << 16);
            string? line = Y4mReader.ReadLine(stream);
            if (line == null) return;
            var header = Y4mHeader.Parse(line);
            if (!cancellationToken.IsCancellationRequested) sink.OnHeader(header);
            var reader = new Y4mReader(stream, header);
            while (reader.ReadFrame() is { } frame)
            {
                if (!cancellationToken.IsCancellationRequested) sink.OnFrame(frame);
            }
        });
        try
        {
            try { await readTask.ConfigureAwait(false); }
            catch when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                await ProcessSupport.ReapAsync(process).ConfigureAwait(false);
                string diagnostics = await errorTask.ConfigureAwait(false);
                throw new PreviewBackendException("都度起動の出力を読み取れませんでした", diagnostics, exception);
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            return new PreviewRunResult(process.ExitCode, error, cancellationToken.IsCancellationRequested);
        }
        finally
        {
            // 例外経路も含めてプロセス終了とパイプ読み取り完了を待ち、孤児を残さない。
            try
            {
                await ProcessSupport.ReapAsync(process).ConfigureAwait(false);
                try { await readTask.ConfigureAwait(false); } catch { }
                await errorTask.ConfigureAwait(false);
            }
            finally
            {
                lock (_sync) _processes.Remove(process);
                process.Dispose();
                finished.TrySetResult();
            }
        }
    }

    public async ValueTask AbortAsync()
    {
        KeyValuePair<Process, TaskCompletionSource>[] processes;
        lock (_sync) processes = _processes.ToArray();
        foreach (var entry in processes)
        {
            try { ProcessSupport.Kill(entry.Key); }
            catch (InvalidOperationException) { }
        }
        // 終了通知はRunAsyncのfinallyがパイプを回収してから出す。
        await Task.WhenAll(processes.Select(entry => entry.Value.Task)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) _disposed = true;
        await AbortAsync().ConfigureAwait(false);
    }
}

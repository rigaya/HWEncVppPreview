using System.Diagnostics;

namespace HWEncParamViewer.Core;

/// <summary>バックエンドの読み取りスレッドから通知する。呼び出し側で必要なスレッドへ転送する。</summary>
public interface IPreviewSink
{
    void OnHeader(Y4mHeader header);
    void OnFrame(Y4mFrame frame);
    /// <summary>stdoutのバイトを受信した。フレーム途中の進捗も含む。</summary>
    void OnOutput();
    /// <summary>次の制御行またはフレームを読む許可を待つ。既存の有限要求は待機しない。</summary>
    ValueTask BeforeReadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <summary>意図した読取待機の前はfalse、読取再開の直前はtrueを通知する。</summary>
    void OnReadStateChanged(bool reading) { }
}

public sealed record PreviewRunResult(int ExitCode, string StandardError, bool IsCancelled = false);

public interface IPreviewBackend : IAsyncDisposable
{
    /// <summary>実際に受信したREADYのencoder。常駐未対応や都度起動はnullとし、パス名から推定しない。</summary>
    Task<string?> GetReadyEncoderAsync(string encoderPath, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
    Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default);
    /// <summary>watchdog等で回復不能になった実行を、プロセスツリーごと停止する。</summary>
    ValueTask AbortAsync();
}

public sealed class PreviewServerNotSupportedException : Exception
{
    public PreviewServerNotSupportedException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>プロトコル破損等の異常と、その要求の標準エラーログ。</summary>
public sealed class PreviewBackendException : Exception
{
    public PreviewBackendException(string message, string standardError, Exception? innerException = null)
        : base(message, innerException) => StandardError = standardError;
    public string StandardError { get; }
}

/// <summary>需要待ちの通知と中断可能な待機を、両バックエンドでそろえる。</summary>
internal static class PreviewReadGate
{
    public static async ValueTask WaitAsync(IPreviewSink sink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValueTask permission = sink.BeforeReadAsync(cancellationToken);
        if (!permission.IsCompleted)
        {
            sink.OnReadStateChanged(false);
            // sinkが中断を無視しても、Abort/Disposeで読取タスクを必ず回収できるようにする。
            await permission.AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else await permission.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        sink.OnReadStateChanged(true);
    }
}

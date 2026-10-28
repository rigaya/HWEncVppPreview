using System.Diagnostics;

namespace HWEncParamViewer.Core;

/// <summary>バックエンドの読み取りスレッドから通知する。呼び出し側で必要なスレッドへ転送する。</summary>
public interface IPreviewSink
{
    void OnHeader(Y4mHeader header);
    void OnFrame(Y4mFrame frame);
    /// <summary>stdoutのバイトを受信した。フレーム途中の進捗も含む。</summary>
    void OnOutput();
}

public sealed record PreviewRunResult(int ExitCode, string StandardError, bool IsCancelled = false);

public interface IPreviewBackend : IAsyncDisposable
{
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

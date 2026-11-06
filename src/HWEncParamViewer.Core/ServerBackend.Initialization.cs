namespace HWEncParamViewer.Core;

public sealed partial class ServerBackend
{
    private static long _nextInitializationCorrelation = long.MinValue;
    private string? _initializingEncoder;
    private Task? _initialization;
    private CancellationTokenSource? _initializationCancellation;
    private static readonly Lazy<Task<string>> WarmupInput = new(CreateWarmupInputAsync);

    /// <summary>サーバの最初のGPU初期化を、表示しない合成入力で前倒しする。</summary>
    public Task InitializeEncoderAsync(string encoderPath, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initializingEncoder == encoderPath && _initialization != null) return _initialization;
            CancelInitializationCore();
            _initializingEncoder = encoderPath;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _initializationCancellation = cancellation;
            return _initialization = InitializeCoreAsync(encoderPath, cancellation);
        }
    }

    private void CancelInitializationCore()
    {
        if (_initialization is not { IsCompleted: false }) return;
        try { _initializationCancellation?.Cancel(); }
        catch (ObjectDisposedException) { } // 完了直前に自身の取消源を破棄した場合。
    }

    private async Task WaitForInitializationAsync(string encoderPath, CancellationToken token)
    {
        Task? initialization;
        lock (_sync) initialization = _initializingEncoder == encoderPath ? _initialization : null;
        if (initialization != null) await initialization.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync(string encoder, CancellationTokenSource cancellation)
    {
        // 登録を終えてから実行し、実要求が初期化の後ろへ並ぶ順序を確定する。
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        using (cancellation)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), _timeProvider))
        using (var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, timeout.Token))
        {
            long correlationId = Interlocked.Increment(ref _nextInitializationCorrelation);
            try
            {
                string input = await WarmupInput.Value.WaitAsync(execution.Token).ConfigureAwait(false);
                if (PerfTrace.Enabled) PerfTrace.Write("server.initialize.begin", new { encoder, correlationId });
                var result = await RunCoreAsync(new PreviewRequest
                {
                    // 並列初期化を通常要求と取り違えず、時系列を1本ずつ追跡できる専用ID。
                    CorrelationId = correlationId,
                    EncoderPath = encoder, InputFile = input, InputArgs = ["--y4m"],
                    Fps = 1, TargetSeconds = 0, PreMarginSeconds = 0, PostMarginSeconds = 0,
                }, new InitializationSink(), execution.Token).ConfigureAwait(false);
                if (PerfTrace.Enabled) PerfTrace.Write("server.initialize.end", new { encoder, correlationId, result.ExitCode, result.IsCancelled });
            }
            catch (Exception exception)
            {
                // 初期化に失敗しても実要求のfallbackや通常の診断へ任せ、VMへエラーを通知しない。
                if (PerfTrace.Enabled) PerfTrace.Write("server.initialize.failed", new { encoder, correlationId, error = exception.Message });
            }
        }
    }

    private static async Task<string> CreateWarmupInputAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "HWEncParamViewer");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "warmup-64x64.y4m");
        byte[] header = System.Text.Encoding.ASCII.GetBytes("YUV4MPEG2 W64 H64 F30:1 Ip A1:1 C420jpeg\nFRAME\n");
        byte[] data = new byte[header.Length + 64 * 64 * 3 / 2];
        header.CopyTo(data, 0);
        Array.Fill(data, (byte)16, header.Length, 64 * 64);
        Array.Fill(data, (byte)128, header.Length + 64 * 64, 64 * 64 / 2);
        if (HasExpectedSize()) return path;
        // 固定名を使い、複数アプリでも作成途中の内容を読ませず、異常終了後の残留を抑える。
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data).ConfigureAwait(false);
            try { File.Move(temporary, path, overwrite: true); }
            catch (IOException) when (HasExpectedSize()) { } // 別プロセスの完成品を優先する。
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        bool HasExpectedSize() => File.Exists(path) && new FileInfo(path).Length == data.Length;
        return path;
    }

    private sealed class InitializationSink : IPreviewSink
    {
        public void OnHeader(Y4mHeader header) { }
        public void OnFrame(Y4mFrame frame) { }
        public void OnOutput() { }
    }
}

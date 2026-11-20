using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

// 常駐要求を観測し、実際のサーバ実行はそのまま通す。
internal sealed class RecordingProbeBackend : IPreviewBackend
{
    private readonly ServerBackend _server = TestProcesses.CreateServerBackend();
    internal List<string[]> Commands { get; } = [];
    public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
    {
        Commands.Add(CommandBuilder.BuildServer(request));
        return _server.RunAsync(request, sink, token);
    }
    public ValueTask AbortAsync() => _server.AbortAsync();
    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

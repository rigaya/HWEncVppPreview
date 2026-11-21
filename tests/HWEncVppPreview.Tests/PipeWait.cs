using System.IO.Pipes;
using HWEncVppPreview.Core;
using HWEncVppPreview.Testing;

namespace HWEncVppPreview.Tests;

internal static class PipeWait
{
    // 接続前に対象処理が終われば、結果または例外を付けて直ちに失敗する。
    internal static async Task UntilConnectedAsync(NamedPipeServerStream pipe, string name, Task operation)
    {
        using var cancellation = new CancellationTokenSource();
        Task connect = pipe.WaitForConnectionAsync(cancellation.Token);
        Task first = await Task.WhenAny(connect, operation);
        if (first == operation && !connect.IsCompleted)
        {
            cancellation.Cancel();
            string detail;
            try
            {
                await operation;
                detail = operation is Task<PreviewRunResult> run ? run.Result.ToString() : "正常終了";
            }
            catch (Exception error)
            {
                string stderr = error switch
                {
                    InputProbeException probe => probe.StandardError,
                    InputProbeTimeoutException probe => probe.StandardError,
                    PreviewBackendException backend => backend.StandardError,
                    _ => "",
                };
                detail = error + " " + stderr;
            }
            throw new InvalidOperationException($"pipe {name}の接続前に対象処理が終了: {detail}");
        }
        await connect;
    }
}

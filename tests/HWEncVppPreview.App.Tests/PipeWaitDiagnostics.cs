using System.IO.Pipes;
using HWEncVppPreview.App.ViewModels;

namespace HWEncVppPreview.App.Tests;

// 子を所有しない試験では既存の終了Taskだけを監視し、取得できない診断を推測しない。
internal static class PipeWaitDiagnostics
{
    internal static string State(MainWindowViewModel? vm) => vm == null ? "VM未設定"
        : $"状態={vm.StatusText}, エラー={vm.ErrorText ?? "なし"}, ログ={vm.LogText}";

    private static async Task<T> WaitAsync<T>(Func<CancellationToken, Task<T>> operation, string name,
        string stage, Func<string> diagnostics, Task? terminal)
    {
        if (terminal == null) return await operation(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        Task<T> signal = operation(cancel.Token);
        if (await Task.WhenAny(signal, terminal) == signal || signal.IsCompleted) return await signal;

        string result = terminal.IsFaulted ? terminal.Exception?.ToString() ?? "不明な例外"
            : terminal.IsCanceled ? "取消" : "正常終了";
        // 処理が先に終わったら、残ったパイプI/Oを取り消してから診断を返す。
        cancel.Cancel();
        Exception? cancellationError = null;
        try { await signal; }
        catch (Exception exception) { cancellationError = exception; }
        throw new InvalidOperationException($"pipe '{name}' の{stage}前に対象処理が終了: {result}。{diagnostics()}", cancellationError);
    }

    internal static Task<bool> ConnectAsync(NamedPipeServerStream pipe, string name,
        Func<string> diagnostics, Task? terminal = null) => WaitAsync(async token =>
    {
        await pipe.WaitForConnectionAsync(token);
        return true;
    }, name, "接続", diagnostics, terminal);

    internal static Task<string?> ReadLineAsync(StreamReader reader, string name,
        Func<string> diagnostics, Task? terminal = null)
        => WaitAsync(token => reader.ReadLineAsync(token).AsTask(), name, "開始通知", diagnostics, terminal);
}

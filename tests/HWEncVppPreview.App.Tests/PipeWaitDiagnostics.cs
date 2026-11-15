using System.IO.Pipes;
using HWEncVppPreview.App.ViewModels;

namespace HWEncVppPreview.App.Tests;

// 子を所有しない試験では既存の終了Taskだけを監視し、取得できない診断を推測しない。
internal static class PipeWaitDiagnostics
{
    internal static string State(MainWindowViewModel? vm) => vm == null ? "VM未設定"
        : $"状態={vm.StatusText}, エラー={vm.ErrorText ?? "なし"}, ログ={vm.LogText}";

    internal static async Task<T> WaitAsync<T>(Func<CancellationToken, Task<T>> operation, string name,
        string stage, TimeSpan timeout, Func<string> diagnostics, Task? terminal = null)
    {
        using var cancel = new CancellationTokenSource(timeout);
        Task<T>? signal = null;
        try
        {
            signal = operation(cancel.Token);
            if (terminal == null) return await signal.WaitAsync(cancel.Token);
            Task completed = await Task.WhenAny(signal, terminal).WaitAsync(cancel.Token);
            if (signal.IsCompleted) return await signal;
            string result = terminal.IsFaulted ? terminal.Exception?.ToString() ?? "不明な例外"
                : terminal.IsCanceled ? "取消" : "正常終了";
            throw new InvalidOperationException($"pipe '{name}' の{stage}前に対象処理が終了: {result}。{diagnostics()}");
        }
        catch (OperationCanceledException exception) when (cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"pipe '{name}' の{stage}を待機中に期限切れ。{diagnostics()}", exception);
        }
        finally
        {
            cancel.Cancel();
            if (signal != null && !signal.IsCompleted)
                _ = signal.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal static Task<bool> ConnectAsync(NamedPipeServerStream pipe, string name, TimeSpan timeout,
        Func<string> diagnostics, Task? terminal = null) => WaitAsync(async token =>
    {
        await pipe.WaitForConnectionAsync(token);
        return true;
    }, name, "接続", timeout, diagnostics, terminal);

    internal static Task<string?> ReadLineAsync(StreamReader reader, string name, TimeSpan timeout,
        Func<string> diagnostics, Task? terminal = null)
        => WaitAsync(token => reader.ReadLineAsync(token).AsTask(), name, "開始通知", timeout, diagnostics, terminal);
}

using Avalonia.Threading;

namespace HWEncVppPreview.App.Tests;

/// <summary>条件を満たすまでUIを進め、実時間の期限でポーリング自体を終了する。</summary>
internal static class UiTestWait
{
    internal static async Task UntilAsync(Func<bool> condition, TimeSpan timeout,
        DispatcherPriority? priority = null)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            while (!condition())
            {
                // 呼出側のWaitAsyncだけでは元のループが残るため、反復自身にも期限を渡す。
                deadline.Token.ThrowIfCancellationRequested();
                // 優先度を省略していた待ちは、InvokeAsyncの既定の優先度をそのまま使う。
                var operation = priority is { } requested
                    ? Dispatcher.UIThread.InvokeAsync(() => { }, requested, deadline.Token)
                    : Dispatcher.UIThread.InvokeAsync(() => { });
                await operation.GetTask().WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(TestTimeouts.DescribeTimeout("UIの条件待ちが期限を超えました。"), exception);
        }
    }
}

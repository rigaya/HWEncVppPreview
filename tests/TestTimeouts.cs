global using HWEncVppPreview.Testing;

namespace HWEncVppPreview.Testing;

internal static class TestTimeouts
{
    // 共有CIランナーではtesthostのプロセスが十数秒止まることがある。
    // 2026-11-10のCIではCore側が約16秒停滞し、同時に動いた別のtesthostは進行していた。
    // 停止検知だけの待ちは60秒にそろえ、速さや仮想時計の期限を検証する待ちとは分ける。
    internal static readonly TimeSpan Hang = TimeSpan.FromSeconds(60);

    internal static string DescribeTimeout(string message) =>
        $"{message} ThreadPool.ThreadCount={ThreadPool.ThreadCount}, " +
        $"ThreadPool.PendingWorkItemCount={ThreadPool.PendingWorkItemCount}, " +
        $"ThreadPool.CompletedWorkItemCount={ThreadPool.CompletedWorkItemCount}";

    // 待ち自身の期限だけを変換し、製品が返した例外の型と内容はそのまま通す。
    internal static async Task WaitWithDiagnosticsAsync(this Task task, TimeSpan timeout, TimeProvider? clock = null)
    {
        using var deadline = new CancellationTokenSource(timeout, clock ?? TimeProvider.System);
        try { await task.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && exception.CancellationToken == deadline.Token)
        {
            throw new TimeoutException(DescribeTimeout(exception.Message), exception);
        }
    }

    internal static async Task<T> WaitWithDiagnosticsAsync<T>(this Task<T> task, TimeSpan timeout, TimeProvider? clock = null)
    {
        using var deadline = new CancellationTokenSource(timeout, clock ?? TimeProvider.System);
        try { return await task.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && exception.CancellationToken == deadline.Token)
        {
            throw new TimeoutException(DescribeTimeout(exception.Message), exception);
        }
    }
}

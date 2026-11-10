using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace HWEncVppPreview.App.Tests;

public sealed class UiTestWaitTests
{
    [AvaloniaFact]
    public async Task TimeoutStopsThePollingTaskItself()
    {
        bool release = false;
        int checks = 0;
        Task? polling = null;
        var safety = TestTimeouts.Hang;
        try
        {
            polling = UiTestWait.UntilAsync(() => { ++checks; return release; }, TimeSpan.FromMilliseconds(20));
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => polling.WaitWithDiagnosticsAsync(safety));
            Assert.Contains("ThreadPool.ThreadCount=", exception.Message);
            Assert.Contains("ThreadPool.PendingWorkItemCount=", exception.Message);
            Assert.Contains("ThreadPool.CompletedWorkItemCount=", exception.Message);
            // 旧実装は待ち手だけがtimeoutし、元のポーリングは無期限に残った。期限でポーリング自体が終わることを確認する。
            Assert.True(polling.IsCompleted);
            int stoppedAt = checks;
            await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitWithDiagnosticsAsync(safety);
            Assert.Equal(stoppedAt, checks);
        }
        finally
        {
            // 退行時も無期限ループをテスト間へ持ち越さない。
            release = true;
            if (polling != null)
            {
                try { await polling.WaitWithDiagnosticsAsync(safety); }
                catch (TimeoutException) { }
            }
        }
    }
}

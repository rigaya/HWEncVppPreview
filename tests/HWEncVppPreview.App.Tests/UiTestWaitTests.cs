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
        var safety = TimeSpan.FromSeconds(5);
        try
        {
            polling = UiTestWait.UntilAsync(() => { ++checks; return release; }, TimeSpan.FromMilliseconds(20));
            await Assert.ThrowsAsync<TimeoutException>(() => polling.WaitAsync(safety));
            // 旧実装は待ち手だけがtimeoutし、元のポーリングは無期限に残った。期限でポーリング自体が終わることを確認する。
            Assert.True(polling.IsCompleted);
            int stoppedAt = checks;
            await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitAsync(safety);
            Assert.Equal(stoppedAt, checks);
        }
        finally
        {
            // 退行時も無期限ループをテスト間へ持ち越さない。
            release = true;
            if (polling != null)
            {
                try { await polling.WaitAsync(safety); }
                catch (TimeoutException) { }
            }
        }
    }
}

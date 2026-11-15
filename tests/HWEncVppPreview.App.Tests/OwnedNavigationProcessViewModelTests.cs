using Avalonia.Headless.XUnit;

namespace HWEncVppPreview.App.Tests;

public sealed class OwnedNavigationProcessViewModelTests
{
    [AvaloniaFact]
    public async Task 順方向移動後も正しい画像に戻れる()
    {
        await using var session = OwnedProcessSession.Create(filterText: "--vpp-nlmeans d=0");
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        byte[] initial = session.ImageBytes();
        session.Vm.CurrentSeconds = 11;
        await session.WaitIdleAsync();
        byte[] moved = session.ImageBytes();
        session.Vm.CurrentSeconds = 10;
        await session.WaitIdleAsync();
        Assert.Equal(10, session.Vm.CurrentSeconds, 5);
        Assert.Equal(initial, session.ImageBytes());
        // 入力時刻と偽画素から独立に算出した各時刻の先頭画素を確かめる。
        Assert.Equal(new byte[] { 150, 130, 147, 255 }, initial[..4]);
        Assert.Equal(new byte[] { 245, 129, 230, 255 }, moved[..4]);
        Assert.Null(session.Vm.ErrorText);
        await session.Vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Single(session.CompletedRuns(), args => args.Contains("--vpp-nlmeans"));
    }

    [AvaloniaFact]
    public async Task 逆方向取得後の大きな移動でも画像を表示する()
    {
        await using var session = OwnedProcessSession.Create(filterText: "--vpp-nlmeans d=0");
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        session.Vm.CurrentSeconds = 8;
        await session.WaitIdleAsync();
        byte[] previous = session.ImageBytes();
        session.Vm.CurrentSeconds = 40;
        await session.WaitIdleAsync();
        Assert.Equal(40, session.Vm.CurrentSeconds, 5);
        Assert.False(previous.SequenceEqual(session.ImageBytes()));
        Assert.Null(session.Vm.ErrorText);
        await session.Vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        var request = session.CompletedRuns().Last(args => args.Contains("--vpp-nlmeans"));
        int seek = Array.IndexOf(request, "--seek");
        Assert.True(seek >= 0);
        Assert.Equal("0:00:39.000", request[seek + 1]);
    }
}

using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.ViewModels;

namespace HWEncVppPreview.App.Tests;

public sealed class OwnedEvictionOrderingTests
{
    [AvaloniaFact]
    public async Task 画面配送前にcacheを消しても前後画像が正しく回復する()
    {
        await using var session = OwnedProcessSession.Create(inputArgs: "--avhw --fake-input-end 11",
            filterText: "--vpp-tweak brightness=0.01");
        session.Vm.CompareMode = PreviewCompareMode.Split;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        byte[] expectedAfter = session.ImageBytes();
        byte[] expectedBefore = session.ImageBytes(before: true);
        session.HoldPosts();
        try
        {
            session.Vm.RefreshCommand.Execute(null);
            await session.WaitForPostAsync();
            session.Cache.Clear();
        }
        finally { session.ReleasePosts(); }
        await session.WaitIdleAsync();
        Assert.Null(session.Vm.ErrorText);
        Assert.Equal(expectedAfter, session.ImageBytes());
        Assert.Equal(expectedBefore, session.ImageBytes(before: true));
        // 偽入力の時刻10秒の画素を、色変換式から別途算出した値と照合する。
        Assert.Equal(new byte[] { 0, 134, 0, 255 }, session.ImageBytes()[..4]);
        Assert.Equal(new byte[] { 0, 135, 0, 255 }, session.ImageBytes(before: true)[..4]);
        await session.Vm.DisposeAsync().AsTask();
        var runs = session.CompletedRuns();
        Assert.Equal(2, runs.Count(args => args.Contains("--vpp-tweak")));
        Assert.Equal(2, runs.Count(args => !args.Contains("--vpp-tweak")));
    }
}

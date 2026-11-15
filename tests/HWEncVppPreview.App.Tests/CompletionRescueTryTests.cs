using Avalonia.Headless.XUnit;

namespace HWEncVppPreview.App.Tests;

public sealed class CompletionRescueTryTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 配送待ちのキャッシュ消去と目標変更後に最新画像へ回復する(bool before)
    {
        await using var session = OwnedProcessSession.Create("--avhw --fake-duration 00:01:00.00",
            filterText: "--vpp-nlmeans", target: 10, beforeEnabled: before);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        byte[] oldImage = session.ImageBytes(before);
        session.HoldPosts();
        try
        {
            session.Vm.CurrentSeconds = 12;
            await session.WaitForPostAsync();
            session.Cache.Clear();
        }
        finally { session.ReleasePosts(); }
        await session.WaitIdleAsync();
        byte[] finalImage = session.ImageBytes(before);
        Assert.Equal(12, session.Vm.CurrentSeconds, 6);
        Assert.False(session.Vm.IsBusy);
        Assert.Null(session.Vm.ErrorText);
        Assert.NotEqual(oldImage, finalImage);
        await session.DisposeAsync();
        Assert.True(session.Scope.Capture().IsEmpty);
    }
}

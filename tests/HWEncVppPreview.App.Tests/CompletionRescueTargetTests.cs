using Avalonia.Headless.XUnit;

namespace HWEncVppPreview.App.Tests;

public sealed class CompletionRescueTargetTests
{
    [AvaloniaTheory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task 完了通知の配送中に目標が変わっても最新の元画像を表示し回収する(
        bool nearEnd, bool clearBeforeOperation, bool clearWhilePosted)
    {
        string inputArgs = nearEnd
            ? "--avhw --fake-duration 00:00:12.10 --fake-input-end 12.1"
            : "--avhw --fake-duration 00:01:00.00";
        await using var session = OwnedProcessSession.Create(inputArgs, filterText: "--vpp-nlmeans", target: 10);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        byte[] oldBefore = session.ImageBytes(before: true);
        if (clearBeforeOperation) session.Cache.Clear();
        session.HoldPosts();
        try
        {
            session.Vm.CurrentSeconds = 12;
            await session.WaitForPostAsync();
            if (clearWhilePosted) session.Cache.Clear();
        }
        finally { session.ReleasePosts(); }
        await session.WaitIdleAsync();
        byte[] finalBefore = session.ImageBytes(before: true);
        Assert.Equal(12, session.Vm.CurrentSeconds, 6);
        Assert.False(session.Vm.IsBeforeBusy);
        Assert.Null(session.Vm.ErrorText);
        Assert.NotEqual(oldBefore, finalBefore);
        await session.DisposeAsync();
        Assert.True(session.Scope.Capture().IsEmpty);
    }
}

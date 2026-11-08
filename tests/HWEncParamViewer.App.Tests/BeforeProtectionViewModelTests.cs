using Avalonia.Headless.XUnit;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.Tests;

public sealed class BeforeProtectionViewModelTests
{
    [AvaloniaFact]
    public async Task 元画像の置換と比較の開閉で保護区間を一つだけ更新する()
    {
        await using var session = OwnedProcessSession.Create(filterText: "--vpp-tweak brightness=0.01");
        var vm = session.Vm;
        vm.CompareMode = PreviewCompareMode.Split;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        Assert.NotNull(vm.BeforeImage);
        long first = Assert.IsType<long>(OwnedVmCheck.Field(session.Cache, "_displayedBeforeSegmentId"));
        Assert.Equal(Assert.IsType<PreviewSegmentSnapshot>(OwnedVmCheck.Field(vm, "_beforeIntervalSegment")).SegmentId, first);

        vm.CurrentSeconds = 11;
        session.Clock.Advance(TimeSpan.FromMilliseconds(150));
        await session.WaitUntilAsync(() => vm.IsBeforeBusy
            && OwnedVmCheck.Field(vm, "_beforeSelectionRequest") is PreviewRequest request && request.TargetSeconds == 11);
        // 比較表示では静止待ちを解除しないため、要求の登録後に偽時計で待機を終える。
        session.Clock.Advance(TimeSpan.FromMilliseconds(300));
        await session.WaitIdleAsync();
        long second = Assert.IsType<long>(OwnedVmCheck.Field(session.Cache, "_displayedBeforeSegmentId"));
        Assert.NotEqual(first, second);
        Assert.Equal(Assert.IsType<PreviewSegmentSnapshot>(OwnedVmCheck.Field(vm, "_beforeIntervalSegment")).SegmentId, second);

        vm.CompareMode = PreviewCompareMode.Single;
        Assert.Null(OwnedVmCheck.Field(session.Cache, "_displayedBeforeSegmentId"));
        vm.IsShowingBefore = true;
        Assert.Equal(second, OwnedVmCheck.Field(session.Cache, "_displayedBeforeSegmentId"));
        vm.IsShowingBefore = false;
        Assert.Null(OwnedVmCheck.Field(session.Cache, "_displayedBeforeSegmentId"));
        vm.CompareMode = PreviewCompareMode.SideBySide;
        Assert.Equal(second, OwnedVmCheck.Field(session.Cache, "_displayedBeforeSegmentId"));
        vm.BeforeImage = null;
        Assert.Null(OwnedVmCheck.Field(session.Cache, "_displayedBeforeSegmentId"));
    }

    [AvaloniaFact]
    public async Task フィルタなしの共有画像は元画像用の追加保護を作らない()
    {
        await using var session = OwnedProcessSession.Create();
        session.Vm.CompareMode = PreviewCompareMode.Split;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        Assert.NotNull(session.Vm.BeforeImage);
        Assert.Null(OwnedVmCheck.Field(session.Cache, "_displayedBeforeSegmentId"));
        session.Vm.CompareMode = PreviewCompareMode.Single;
        session.Vm.IsShowingBefore = true;
        Assert.Null(OwnedVmCheck.Field(session.Cache, "_displayedBeforeSegmentId"));
    }
}

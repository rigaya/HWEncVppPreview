using System.Reflection;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.Core;
using HWEncVppPreview.App.ViewModels;

namespace HWEncVppPreview.App.Tests;

public sealed class EofRecoveryViewModelTests
{
    [AvaloniaFact]
    public async Task ConfirmedEndLimitsLaterInputInfoAndResetsWithInputArguments()
    {
        await using var session = OwnedProcessSession.Create(
            inputArgs: "--avhw --fake-duration 00:00:20.00 --fake-input-end 10", target: 0, beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        var vm = session.Vm;
        vm.GoToEndCommand.Execute(null);
        await session.WaitIdleAsync();
        Assert.Null(vm.ErrorText);
        Assert.Equal(10, vm.DurationSeconds, 5);
        var info = (InputInfo)OwnedVmCheck.Field(vm, "_inputInfo")!;
        typeof(MainWindowViewModel).GetMethod("ApplyInputInfo", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [info with { Duration = TimeSpan.FromSeconds(20) }]);
        Assert.Equal(10, vm.DurationSeconds, 5);
        vm.InputArgsText = "--avhw --fake-duration 00:00:30.00 --fake-input-end 30";
        vm.CurrentSeconds = 0;
        await session.WaitIdleAsync();
        Assert.Null(vm.ErrorText);
        Assert.Equal(30, vm.DurationSeconds);
        Assert.Equal(0, vm.CurrentSeconds);
    }

    [AvaloniaTheory]
    [InlineData(20)]
    [InlineData(10.5)]
    public async Task OverstatedDurationSelectsActualLastFrameAndCorrectsTime(double target)
    {
        await using var session = OwnedProcessSession.Create(
            inputArgs: "--avhw --fake-duration 00:00:20.00 --fake-input-end 10", target: 0, beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        var vm = session.Vm;
        Assert.Equal(20, vm.DurationSeconds);
        if (target == 20) vm.GoToEndCommand.Execute(null);
        else vm.CurrentSeconds = target;
        await session.WaitIdleAsync();
        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.PreviewImage);
        Assert.False(vm.IsBusy);
        Assert.Equal(10, vm.DurationSeconds, 5);
        Assert.Equal(10 - 1.0 / 30, vm.CurrentSeconds, 5);
        var segment = (PreviewSegmentSnapshot)OwnedVmCheck.Field(vm, "_intervalSegment")!;
        Assert.Equal(segment.Frames[^1].Xts, session.DisplayedFrame()!.Xts);
        Assert.Equal("0:00:10", vm.DurationText);
    }
}

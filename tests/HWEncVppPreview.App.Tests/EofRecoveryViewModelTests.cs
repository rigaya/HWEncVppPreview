using System.IO.Pipes;
using System.Text;
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
        vm.InputArgsText = "--avhw --fake-duration 00:00:30.00 --fake-input-end 30";
        vm.CurrentSeconds = 0;
        await session.WaitIdleAsync();
        Assert.Null(vm.ErrorText);
        Assert.Equal(30, vm.DurationSeconds);
        Assert.Equal(0, vm.CurrentSeconds);
    }

    [AvaloniaFact]
    public async Task 遅着した入力長は実終端を上書きしない()
    {
        string name = "late-duration-" + Guid.NewGuid().ToString("N");
        await using var session = OwnedProcessSession.Create(
            inputArgs: "--avhw --fake-duration 00:00:20.00 --fake-duration-at-exit --fake-input-end 10",
            target: 0, beforeEnabled: false, durationPipe: name);
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await session.InitializeAsync();
        await PipeWaitDiagnostics.ConnectAsync(pipe, name, () => PipeWaitDiagnostics.State(session.Vm));
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        Assert.Equal("ready", await PipeWaitDiagnostics.ReadLineAsync(reader, name,
            () => PipeWaitDiagnostics.State(session.Vm)));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(session.Vm.DurationSeconds)) reached.TrySetResult();
        }
        session.Vm.PropertyChanged += Changed;
        try
        {
            session.Vm.CurrentSeconds = 20;
            await reached.Task;
        }
        finally { session.Vm.PropertyChanged -= Changed; }
        await writer.WriteLineAsync("release");
        await session.WaitIdleAsync();
        Assert.Equal(10, session.Vm.DurationSeconds, 5);
        Assert.Equal(10 - 1.0 / 30, session.Vm.CurrentSeconds, 5);
        Assert.Null(session.Vm.ErrorText);
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
        Assert.Equal("0:00:10", vm.DurationText);
    }
}

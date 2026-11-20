using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class NavigationTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;

    [AvaloniaFact]
    public async Task HeldFrameNavigationEndsOnTheLatestFrame()
    {
        await using var fixture = await Fixture.StartAsync();
        for (int index = 0; index < 12; index++) fixture.Vm.StepFrameCommand.Execute(1);
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        AssertDisplayed(fixture.Vm, filtered: false);
        Assert.True(fixture.Vm.CurrentSeconds > 0);
    }

    [AvaloniaFact]
    public async Task FrameStepTargetDoesNotRegressWhileANeighborRefetchIsPending()
    {
        await using var fixture = await Fixture.StartAsync();
        var pending = fixture.Backend.HoldNext();
        fixture.Vm.CurrentSeconds = 2;
        await pending.Started.Task.WaitWithDiagnosticsAsync(Safety);
        for (int index = 0; index < 5; index++) fixture.Vm.StepFrameCommand.Execute(1);
        pending.Release.TrySetResult();
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.True(fixture.Vm.CurrentSeconds >= 2);
        AssertDisplayed(fixture.Vm, filtered: false);
    }

    [AvaloniaFact]
    public async Task SeekDragPublishesAFrameBeforeTheDragEnds()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Vm.BeginSeekDrag();
        fixture.Vm.CurrentSeconds = 1;
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(1, fixture.Vm.CurrentSeconds, 7);
        AssertDisplayed(fixture.Vm, filtered: false);
        fixture.Vm.EndSeekDrag(1);
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        AssertDisplayed(fixture.Vm, filtered: false);
    }

    [AvaloniaFact]
    public async Task CachedFrameNavigationDisplaysTheLatestFrameAfterConversion()
    {
        await using var fixture = await Fixture.StartAsync();
        var pending = fixture.Converter.HoldNext();
        fixture.Vm.StepFrameCommand.Execute(1);
        await pending.Started.Task.WaitWithDiagnosticsAsync(Safety);
        for (int index = 0; index < 5; index++) fixture.Vm.StepFrameCommand.Execute(1);
        pending.Release.TrySetResult();
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        AssertDisplayed(fixture.Vm, filtered: false);
        Assert.True(fixture.Vm.CurrentSeconds > 0);
    }

    [AvaloniaFact]
    public async Task EditingRejectsTheOldFilteredImageEvenWhenItsConversionFinishesLater()
    {
        await using var fixture = await Fixture.StartAsync();
        var line = new FilterLineViewModel();
        fixture.Vm.FilterLines.Add(line);
        var pending = fixture.Converter.HoldNext();
        fixture.Vm.StepFrameCommand.Execute(1);
        await pending.Started.Task.WaitWithDiagnosticsAsync(Safety);
        line.Text = "--vpp-unsharp";
        await fixture.Vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        pending.Release.TrySetResult();
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        AssertDisplayed(fixture.Vm, filtered: true);
    }

    [AvaloniaFact]
    public async Task NavigationWithinAnUnfilteredIntervalDisplaysMatchingBefore()
    {
        await using var fixture = await Fixture.StartAsync(beforeEnabled: true);
        int beforeStarts = fixture.Backend.Requests.Count(request => request.CorrelationId < 0);
        fixture.Vm.StepFrameCommand.Execute(1);
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        AssertDisplayed(fixture.Vm, filtered: false);
        Assert.NotNull(fixture.Vm.BeforeImage);
        Assert.Equal(Pixel(fixture.Vm.PreviewImage!), Pixel(fixture.Vm.BeforeImage!));
        Assert.Equal(beforeStarts, fixture.Backend.Requests.Count(request => request.CorrelationId < 0));
    }

    [AvaloniaFact]
    public async Task AConversionFinishingAfterFailureDoesNotReplaceTheErrorStatus()
    {
        await using var fixture = await Fixture.StartAsync();
        var pending = fixture.Converter.HoldNext();
        fixture.Backend.FailNext = true;
        Task failureNotified = ObserveErrorNotification(fixture.Vm);
        fixture.Vm.CurrentSeconds = 1;
        await pending.Started.Task.WaitWithDiagnosticsAsync(Safety);
        fixture.Backend.ContinueAfterFrames.TrySetResult();
        await failureNotified.WaitWithDiagnosticsAsync(Safety);
        pending.Release.TrySetResult();
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.NotNull(fixture.Vm.ErrorText);
        Assert.Equal("エラー", fixture.Vm.StatusText);
        Assert.False(fixture.Vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task NavigationDuringInitialProbeReceivesTheProbeFailureAndClearsBusy()
    {
        string pipeName = "navigation-probe-" + Guid.NewGuid().ToString("N");
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut,
            1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        await using var backend = new ControlledBackend();
        await using var probe = TestProcesses.CreateInputProbe();
        await using var vm = TestViewModels.Create(new Services(), backend,
            probe, new FrameCache(0), ControlledConverter.ConvertNow);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "navigation probe input.ts";
        vm.InputArgsText = "--frames 0 --fake-rc 23 --fake-frame-pipe " + pipeName;
        Task initialize = vm.InitializeAsync(false);
        await PipeWaitDiagnostics.ConnectAsync(pipe, pipeName, Safety, () => PipeWaitDiagnostics.State(vm), initialize);
        using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        string? ready = await PipeWaitDiagnostics.ReadLineAsync(reader, pipeName, Safety, () => PipeWaitDiagnostics.State(vm), initialize);
        if (ready != "ready") throw new InvalidDataException("待機開始の通知が不正です。");
        vm.StepFrameCommand.Execute(1);
        await writer.WriteLineAsync("release");
        await initialize.WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.False(vm.IsBusy);
        Assert.Equal("エラー", vm.StatusText);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("23", vm.LogText);
        Assert.Equal(0, vm.CurrentSeconds, 7);
    }

    [AvaloniaFact]
    public async Task 初期検査がpipe接続前に失敗したら診断付きで待機を終える()
    {
        string pipeName = "navigation-failed-probe-" + Guid.NewGuid().ToString("N");
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut,
            1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        await using var backend = new ControlledBackend();
        await using var probe = TestProcesses.CreateInputProbe();
        await using var vm = TestViewModels.Create(new Services(), backend,
            probe, new FrameCache(0), ControlledConverter.ConvertNow);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "navigation failed probe.ts";
        vm.InputArgsText = "--frames 0 --fake-rc 23";
        Task initialize = vm.InitializeAsync(false);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PipeWaitDiagnostics.ConnectAsync(pipe, pipeName, Safety, () => PipeWaitDiagnostics.State(vm), initialize));
        Assert.Contains(pipeName, exception.Message);
        Assert.Contains("接続", exception.Message);
        Assert.Contains("23", exception.Message);
    }

    private static void AssertDisplayed(MainWindowViewModel vm, bool filtered)
    {
        int index = (int)Math.Round(vm.CurrentSeconds * 30);
        byte expected = unchecked((byte)(16 + (index + (filtered ? 80 : 0)) % 200));
        Assert.Equal(expected, Pixel(vm.PreviewImage!));
        Assert.Null(vm.ErrorText);
    }

    private static byte Pixel(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        using var locked = Assert.IsType<WriteableBitmap>(bitmap).Lock();
        return Marshal.ReadByte(locked.Address);
    }

    private static Task ObserveErrorNotification(MainWindowViewModel vm)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName != nameof(vm.ErrorText)) return;
            vm.PropertyChanged -= handler;
            ready.TrySetResult();
        };
        vm.PropertyChanged += handler;
        return ready.Task;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SyntheticInputProbe _input = new(TimeProvider.System);
        internal ControlledBackend Backend { get; } = new();
        internal ControlledConverter Converter { get; } = new();
        internal MainWindowViewModel Vm { get; }

        private Fixture(bool beforeEnabled)
        {
            Vm = new MainWindowViewModel(new Services(), TimeProvider.System, Backend,
                _input.Probe, new FrameCache(0), Converter.ConvertAsync, beforeEnabled: beforeEnabled,
                debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync)
            { EncoderPath = "fake", InputPath = "navigation input.ts", InputArgsText = "--fake-duration 00:00:10.00" };
        }

        internal static async Task<Fixture> StartAsync(bool beforeEnabled = false)
        {
            var fixture = new Fixture(beforeEnabled);
            try
            {
                await fixture.Vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
                await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            Converter.ReleaseHeld();
            Backend.ReleaseHeld();
            await Vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
            await _input.DisposeAsync();
        }
    }

    private sealed class Pending
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledBackend : IPreviewBackend
    {
        private Pending? _held, _active;
        internal System.Collections.Concurrent.ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        internal TaskCompletionSource ContinueAfterFrames { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailNext { get; set; }
        internal Pending HoldNext() => _held = new Pending();
        internal void ReleaseHeld() { _held?.Release.TrySetResult(); _active?.Release.TrySetResult(); ContinueAfterFrames.TrySetResult(); }
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Enqueue(request);
            Pending? pending = Interlocked.Exchange(ref _held, null);
            if (pending != null)
            {
                _active = pending;
                pending.Started.TrySetResult();
                try { await pending.Release.Task.WaitAsync(token); }
                finally { _active = null; }
            }
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 Ip A1:1 C420jpeg");
            sink.OnHeader(header);
            var plan = CommandBuilder.Plan(request);
            int start = (int)Math.Round((plan.SeekSeconds ?? 0) * 30);
            bool filtered = request.VppArgs.Count != 0;
            for (int index = 0; index < Math.Max(31, plan.FrameCount); index++)
            {
                token.ThrowIfCancellationRequested();
                byte value = unchecked((byte)(16 + (start + index + (filtered ? 80 : 0)) % 200));
                sink.OnFrame(new Y4mFrame(header, index / 30.0, 1.0 / 30, [value, value, value, value, 128, 128]));
            }
            if (FailNext)
            {
                FailNext = false;
                await ContinueAfterFrames.Task.WaitAsync(token);
                return new PreviewRunResult(-29, "処理失敗");
            }
            return new PreviewRunResult(0, "");
        }
        public ValueTask AbortAsync() { ReleaseHeld(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => AbortAsync();
    }

    private sealed class ControlledConverter
    {
        private Pending? _held, _active;
        internal Pending HoldNext() => _held = new Pending();
        internal void ReleaseHeld() { _held?.Release.TrySetResult(); _active?.Release.TrySetResult(); }
        internal async Task<byte[]> ConvertAsync(Y4mFrame frame, CancellationToken token)
        {
            Pending? pending = Interlocked.Exchange(ref _held, null);
            if (pending != null)
            {
                _active = pending;
                pending.Started.TrySetResult();
                // 古い変換が取消を無視して終わる場合も最終表示を守る。
                try { await pending.Release.Task; }
                finally { _active = null; }
            }
            return await ConvertNow(frame, token);
        }
        internal static Task<byte[]> ConvertNow(Y4mFrame frame, CancellationToken token)
            => Task.FromResult(Enumerable.Repeat(frame.Data[0], frame.Header.Width * frame.Header.Height * 4).ToArray());
    }

    private static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "FakeEncoder", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }

    private sealed class Services : IViewModelServices
    {
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "viewer-navigation-" + Guid.NewGuid().ToString("N"), "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
}

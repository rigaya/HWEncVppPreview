using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HWEncVppPreview.App.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.App.Views;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class ComparisonTests
{
    private static readonly TimeSpan SafetyTimeout = TestTimeouts.Hang;

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActualSizeRemovesSarAndKeepsSharedCenter(bool sideBySide, bool keyboard)
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        await using var vm = Create(services, clock, backend);
        vm.InputArgsText += " --fake-sar 4:3";
        await InitializeAsync(vm, clock);
        vm.CompareMode = sideBySide ? PreviewCompareMode.SideBySide : PreviewCompareMode.Single;
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        window.Show();
        try
        {
            var main = window.FindControl<ZoomPanViewer>("MainViewer")!;
            var before = window.FindControl<ZoomPanViewer>("BeforeViewer")!;
            var after = window.FindControl<ZoomPanViewer>("AfterViewer")!;
            main.Zoom = new ZoomState(false, 2, new Point(vm.DisplayWidth * 0.6, 12));
            double oldWidth = vm.DisplayWidth;
            vm.UseSar = false;
            Assert.Equal(0.6, main.Zoom.Center.X / vm.DisplayWidth, 8);
            vm.UseSar = true;
            Assert.Equal(oldWidth, vm.DisplayWidth, 8);
            Assert.Equal(0.6, main.Zoom.Center.X / vm.DisplayWidth, 8);
            if (keyboard)
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D0, KeyModifiers = KeyModifiers.Control });
            else
                window.FindControl<Button>("ZoomActualButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(vm.UseSar);
            Assert.False(main.Zoom.IsFit);
            Assert.Equal(1, main.Zoom.Scale);
            Assert.Equal(0.6, main.Zoom.Center.X / vm.DisplayWidth, 8);
            Assert.Equal(12, main.Zoom.Center.Y);
            Assert.Equal(main.Zoom, before.Zoom);
            Assert.Equal(main.Zoom, after.Zoom);
            var active = sideBySide ? after : main;
            var rect = ZoomMath.ContentRect(active.Zoom, active.Bounds.Size, new Size(vm.DisplayWidth, vm.DisplayHeight));
            Assert.Equal(vm.PreviewImage!.PixelSize.Width, rect.Width, 8);
            Assert.Equal(vm.PreviewImage.PixelSize.Height, rect.Height, 8);
            Assert.Single(backend.Runs);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BeforeStartsAfterSuccessfulAfterAndCannotOverwriteItsImageOrStatus()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend { HoldFirstBefore = true };
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans sigma=0.1" });
        await PrepareAsync(vm, clock);
        var after = await backend.NextAsync();
        var before = await backend.NextAsync();
        Assert.False(after.IsBefore);
        Assert.True(before.IsBefore);
        Assert.True(after.Completed.Task.IsCompletedSuccessfully);
        Assert.True(vm.IsBeforeBusy);
        await WaitUntilAsync(vm, () => vm.PreviewImage != null && !vm.IsBusy);
        string afterImage = ImageHash(vm.PreviewImage!);
        string status = vm.StatusText, log = vm.LogText, elapsed = vm.ElapsedText, mode = vm.BackendModeText;
        Assert.Contains("AFTER", log);
        clock.Advance(TimeSpan.FromSeconds(1));
        before.Release.TrySetResult();
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.NotNull(vm.BeforeImage);
        Assert.NotEqual(afterImage, ImageHash(vm.BeforeImage!));
        Assert.Equal(afterImage, ImageHash(vm.PreviewImage!));
        Assert.Equal(status, vm.StatusText);
        Assert.Equal(log, vm.LogText);
        Assert.Equal(elapsed, vm.ElapsedText);
        Assert.Equal(mode, vm.BackendModeText);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsBeforeBusy);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task FilterChangeReusesTheUnfilteredIntervalAndKeepsAfterFrameStepping()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        await using var vm = Create(services, clock, backend);
        var line = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.1" };
        vm.FilterLines.Add(line);
        await InitializeAsync(vm, clock);
        Assert.Equal(2, backend.Runs.Count);
        string before = ImageHash(vm.BeforeImage!);
        string after = ImageHash(vm.PreviewImage!);
        line.Text = "--vpp-nlmeans sigma=0.2";
        await RunPreparedAsync(vm, clock);
        Assert.Equal(3, backend.Runs.Count);
        Assert.Equal(before, ImageHash(vm.BeforeImage!));
        Assert.NotEqual(after, ImageHash(vm.PreviewImage!));
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(10 / 30.0, vm.CurrentSeconds, 7);
        Assert.Equal(3, backend.Runs.Count);
        Assert.Contains("キャッシュ", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task SarToggleRecomputesBothSizesWithoutEncodingAndResizeKeepsOriginalBeforeSize()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        await using var vm = Create(services, clock, backend);
        vm.InputArgsText += " --fake-sar 4:3";
        vm.FilterLines.Add(new() { Text = "--vpp-resize algo=bilinear --output-res 32x18" });
        await InitializeAsync(vm, clock);
        Assert.Equal(32, vm.PreviewImage!.PixelSize.Width);
        Assert.Equal(18, vm.PreviewImage.PixelSize.Height);
        Assert.Equal(64, vm.BeforeImage!.PixelSize.Width);
        Assert.Equal(36, vm.BeforeImage.PixelSize.Height);
        Assert.Equal(32 * 4 / 3.0, vm.DisplayWidth, 7);
        Assert.Equal(64 * 4 / 3.0, vm.BeforeDisplayWidth, 7);
        vm.UseSar = false;
        Assert.Equal(32, vm.DisplayWidth);
        Assert.Equal(18, vm.DisplayHeight);
        Assert.Equal(64, vm.BeforeDisplayWidth);
        Assert.Equal(36, vm.BeforeDisplayHeight);
        vm.UseSar = true;
        Assert.Equal(32 * 4 / 3.0, vm.DisplayWidth, 7);
        Assert.Equal(64 * 4 / 3.0, vm.BeforeDisplayWidth, 7);
        Assert.Equal(2, backend.Runs.Count);
    }

    [AvaloniaFact]
    public async Task DecimatedAndUnfilteredStreamsSelectTheirOwnFramesAtTheSameTarget()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-decimate" });
        await InitializeAsync(vm, clock);
        var runs = backend.Runs.ToArray();
        var after = Assert.Single(runs, run => !run.IsBefore);
        var before = Assert.Single(runs, run => run.IsBefore);
        Assert.Equal(after.Request.TargetSeconds, before.Request.TargetSeconds);
        Assert.Equal(CommandBuilder.Plan(after.Request), CommandBuilder.Plan(before.Request));
        Assert.True(after.Frames.Count < before.Frames.Count);
        var selectedAfter = SelectIndependent(after);
        var selectedBefore = SelectIndependent(before);
        Assert.NotEqual(selectedAfter.Xts, selectedBefore.Xts);
        Assert.Equal(FrameHash(selectedAfter), ImageHash(vm.PreviewImage!));
        Assert.Equal(FrameHash(selectedBefore), ImageHash(vm.BeforeImage!));
        Assert.Equal(0.3, vm.CurrentSeconds, 7);
    }

    [AvaloniaFact]
    public async Task BeforeFailureDoesNotReplaceSuccessfulAfterDiagnostics()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend { FailBefore = true };
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
        await InitializeAsync(vm, clock);
        Assert.NotNull(vm.PreviewImage);
        Assert.Contains("AFTER", vm.LogText);
        Assert.DoesNotContain("BEFORE_FAILURE", vm.LogText);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsBeforeBusy);
    }

    [AvaloniaFact]
    public async Task ObsoleteBeforeResultCannotReplaceImagesAfterInputAndTimeChange()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend { HoldFirstBefore = true, IgnoreHeldBeforeCancellation = true };
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
        await PrepareAsync(vm, clock);
        await backend.NextAsync();
        var oldBefore = await backend.NextAsync();
        Assert.True(oldBefore.IsBefore);
        vm.CurrentSeconds = 1.2;
        vm.InputPath = "new input.ts";
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await oldBefore.Cancelled.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        oldBefore.Release.TrySetResult();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        var latestAfter = backend.Runs.Last(run => !run.IsBefore);
        var latestBefore = backend.Runs.Last(run => run.IsBefore);
        Assert.Equal("new input.ts", latestAfter.Request.InputFile);
        Assert.Equal("new input.ts", latestBefore.Request.InputFile);
        Assert.Equal(1.2, vm.CurrentSeconds, 7);
        Assert.Equal(FrameHash(SelectIndependent(latestAfter)), ImageHash(vm.PreviewImage!));
        Assert.Equal(FrameHash(SelectIndependent(latestBefore)), ImageHash(vm.BeforeImage!));
        Assert.False(vm.IsBeforeBusy);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task DisposeWhileBeforeIsRunningReapsItsProcess()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend { HangFirstBefore = true };
        var vm = Create(services, clock, backend);
        try
        {
            vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
            await PrepareAsync(vm, clock);
            await backend.NextAsync();
            var before = await backend.NextAsync();
            int pid = await before.ProcessId.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.True(vm.IsBeforeBusy);
            await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
            AssertStopped(pid);
            Assert.Null(vm.PreviewImage);
            Assert.Null(vm.BeforeImage);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task EmptyFilterUsesOneEncodedIntervalForBothImages()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        await using var vm = Create(services, clock, backend);
        await InitializeAsync(vm, clock);
        Assert.Single(backend.Runs);
        Assert.NotNull(vm.BeforeImage);
        Assert.Equal(ImageHash(vm.PreviewImage!), ImageHash(vm.BeforeImage!));
    }

    [AvaloniaFact]
    public async Task BeforeWaitsUntilTheLastMovementHasBeenQuietFor300Milliseconds()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
        await InitializeAndDrainAsync(vm, clock, backend);
        await MoveAndWaitForAfterAsync(vm, backend, 1.2);
        Assert.True(vm.IsBeforeBusy);
        Assert.Single(backend.Runs, run => run.IsBefore);
        clock.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Single(backend.Runs, run => run.IsBefore);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var before = await backend.NextAsync();
        Assert.True(before.IsBefore);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(FrameHash(SelectIndependent(before, vm.CurrentSeconds)), ImageHash(vm.BeforeImage!));
        Assert.False(vm.IsBeforeBusy);
    }

    [AvaloniaFact]
    public async Task DroppedRepeatedStepsStillPostponeTheBeforeRequest()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
        await InitializeAndDrainAsync(vm, clock, backend);
        backend.HoldNextAfter = true;
        vm.CurrentSeconds = 1.2;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        var held = await backend.NextAsync();
        Assert.False(held.IsBefore);
        vm.StepFrameCommand.Execute(1);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        // 隣フレームの要求が待機中なので、この反復は表示目標へ加算されない。
        vm.StepFrameCommand.Execute(1);
        Assert.Equal(1.2, vm.CurrentSeconds, 7);
        held.Release.TrySetResult();
        await WaitUntilAsync(vm, () => !vm.IsBusy);
        clock.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Single(backend.Runs, run => run.IsBefore);
        vm.IsShowingBefore = true;
        var before = await backend.NextAsync();
        Assert.True(before.IsBefore);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(FrameHash(SelectIndependent(before, vm.CurrentSeconds)), ImageHash(vm.BeforeImage!));
    }

    [AvaloniaFact]
    public async Task MovementAtTheQuietBoundaryKeepsTheBeforePendingUntilSpace()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
        await InitializeAndDrainAsync(vm, clock, backend);
        await MoveAndWaitForAfterAsync(vm, backend, 1.2);
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 期限で予約された背景Postより先に、新しい移動意図をUIで処理する。
        Dispatcher.UIThread.Post(() =>
        {
            vm.CurrentSeconds = vm.CurrentSeconds;
            moved.TrySetResult();
        }, DispatcherPriority.Send);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        await moved.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Single(backend.Runs, run => run.IsBefore);
        vm.IsShowingBefore = true;
        Assert.True((await backend.NextAsync()).IsBefore);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
    }

    [AvaloniaFact]
    public async Task SpaceStartsAPendingBeforeWithoutWaitingAndDoesNotDuplicateIt()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
        await InitializeAndDrainAsync(vm, clock, backend);
        await MoveAndWaitForAfterAsync(vm, backend, 1.2);
        long time = clock.GetTimestamp();
        vm.IsShowingBefore = true;
        var before = await backend.NextAsync();
        Assert.True(before.IsBefore);
        Assert.Equal(time, clock.GetTimestamp());
        vm.IsShowingBefore = false;
        vm.IsShowingBefore = true;
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(2, backend.Runs.Count(run => run.IsBefore));
        Assert.NotNull(vm.BeforeImage);
    }

    [AvaloniaFact]
    public async Task SameIntervalStepKeepsTheBeforeRunAndSelectsTheLatestTimeOnCompletion()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend { HoldFirstBefore = true };
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
        await PrepareAsync(vm, clock);
        await backend.NextAsync();
        var before = await backend.NextAsync();
        Assert.True(before.IsBefore);
        // Beforeの開始はAfterの完了後だが、Afterの区間がVMへ入るのはUIでの反映後。
        // 反映前に1枚送りすると取り直しになり、CurrentSecondsが同期的に進まない(2コアで再現)。
        await WaitUntilAsync(vm, () => vm.PreviewImage != null && !vm.IsBusy);
        vm.StepFrameCommand.Execute(1);
        Assert.Equal(10 / 30.0, vm.CurrentSeconds, 7);
        Assert.False(before.Cancelled.Task.IsCompleted);
        Assert.Single(backend.Runs, run => run.IsBefore);
        before.Release.TrySetResult();
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.False(before.Cancelled.Task.IsCompleted);
        Assert.Single(backend.Runs, run => run.IsBefore);
        Assert.Equal(FrameHash(SelectIndependent(before, vm.CurrentSeconds)), ImageHash(vm.BeforeImage!));
        Assert.NotEqual(FrameHash(SelectIndependent(before)), ImageHash(vm.BeforeImage!));
    }

    [AvaloniaFact]
    public async Task MovementToADifferentIntervalCancelsTheOldBefore()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend { HoldFirstBefore = true };
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
        await PrepareAsync(vm, clock);
        await backend.NextAsync();
        var old = await backend.NextAsync();
        vm.CurrentSeconds = 1.2;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        await old.Cancelled.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        var after = await backend.NextAsync();
        Assert.False(after.IsBefore);
        await WaitUntilAsync(vm, () => !vm.IsBusy);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        var before = await backend.NextAsync();
        Assert.True(before.IsBefore);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(FrameHash(SelectIndependent(before, vm.CurrentSeconds)), ImageHash(vm.BeforeImage!));
    }

    [AvaloniaFact]
    public async Task EditingCancelsTheBeforeEvenWhenItsIntervalDoesNotChange()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend { HoldFirstBefore = true };
        await using var vm = Create(services, clock, backend);
        var line = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.1" };
        vm.FilterLines.Add(line);
        await PrepareAsync(vm, clock);
        await backend.NextAsync();
        var old = await backend.NextAsync();
        line.Text = "--vpp-nlmeans sigma=0.2";
        await old.Cancelled.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(2, backend.Runs.Count(run => run.IsBefore));
        Assert.NotNull(vm.BeforeImage);
    }

    [AvaloniaFact]
    public async Task DisposeReapsAPendingBeforeWithoutAdvancingTheClock()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        var vm = Create(services, clock, backend);
        try
        {
            vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
            await InitializeAndDrainAsync(vm, clock, backend);
            await MoveAndWaitForAfterAsync(vm, backend, 1.2);
            Assert.True(vm.IsBeforeBusy);
            await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.Single(backend.Runs, run => run.IsBefore);
            Assert.Null(vm.BeforeImage);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task AllComparisonModesRenderWithoutChangingTheAfterImage()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var backend = new RecordingBackend();
        await using var vm = Create(services, clock, backend);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans" });
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            await InitializeAsync(vm, clock);
            string after = ImageHash(vm.PreviewImage!);
            foreach (var mode in Enum.GetValues<PreviewCompareMode>())
            {
                vm.SetCompareModeCommand.Execute(mode);
                Assert.Equal(mode == PreviewCompareMode.Toggle ? PreviewCompareMode.Single : mode, vm.CompareMode);
                vm.IsShowingBefore = mode == PreviewCompareMode.Toggle;
                vm.SplitPosition = 0.4;
                using var rendered = window.CaptureRenderedFrame();
                Assert.NotNull(rendered);
                Assert.True(rendered.PixelSize.Width > 0);
                Assert.Equal(after, ImageHash(vm.PreviewImage!));
            }
            vm.IsShowingBefore = false;
            Assert.Equal(2, backend.Runs.Count);
        }
        finally { window.Close(); }
    }

    private static MainWindowViewModel Create(TestServices services, FakeTimeProvider clock, RecordingBackend backend) =>
        new(services, clock, preferred: backend, fallback: backend)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "fake input.ts", CurrentSeconds = 0.3,
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };

    private static async Task InitializeAndDrainAsync(MainWindowViewModel vm, FakeTimeProvider clock, RecordingBackend backend)
    {
        await InitializeAsync(vm, clock);
        Assert.False((await backend.NextAsync()).IsBefore);
        Assert.True((await backend.NextAsync()).IsBefore);
    }
    private static async Task MoveAndWaitForAfterAsync(MainWindowViewModel vm, RecordingBackend backend, double seconds)
    {
        vm.CurrentSeconds = seconds;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.False((await backend.NextAsync()).IsBefore);
        await WaitUntilAsync(vm, () => !vm.IsBusy);
    }
    private static async Task PrepareAsync(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
    }
    private static async Task InitializeAsync(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        await PrepareAsync(vm, clock);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
    }
    private static async Task RunPreparedAsync(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
    }
    private static async Task WaitUntilAsync(MainWindowViewModel vm, Func<bool> condition)
    {
        if (condition()) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, _) => { if (condition()) ready.TrySetResult(); };
        vm.PropertyChanged += changed;
        try { await ready.Task.WaitWithDiagnosticsAsync(SafetyTimeout); }
        finally { vm.PropertyChanged -= changed; }
    }
    private static Y4mFrame SelectIndependent(Invocation invocation, double? targetSeconds = null)
    {
        var plan = CommandBuilder.Plan(invocation.Request);
        double target = targetSeconds is double seconds ? seconds - (plan.SeekSeconds ?? 0) : plan.ActualPreMargin;
        return invocation.Frames.Where(frame => frame.Xts!.Value <= target + frame.Xdur!.Value / 2)
            .OrderByDescending(frame => frame.Xts).First();
    }
    private static string FrameHash(Y4mFrame frame) => Convert.ToHexStringLower(SHA256.HashData(YuvToBgra.Convert(frame)));
    private static string ImageHash(Bitmap bitmap)
    {
        var writable = Assert.IsType<WriteableBitmap>(bitmap);
        using var frame = writable.Lock();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var row = new byte[writable.PixelSize.Width * 4];
        for (int y = 0; y < writable.PixelSize.Height; ++y)
        {
            Marshal.Copy(frame.Address + y * frame.RowBytes, row, 0, row.Length);
            hash.AppendData(row);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "FakeEncoder", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }
    private static void AssertStopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited, $"子プロセス{pid}が残っています"); }
        catch (ArgumentException) { }
    }

    private sealed class RecordingBackend : IPreviewBackend
    {
        private readonly Channel<Invocation> _started = Channel.CreateUnbounded<Invocation>();
        private readonly List<Invocation> _runs = new();
        private readonly OneShotBackend _inner;
        private Invocation? _current;
        private int _beforeCount;
        public bool HoldFirstBefore { get; init; }
        public bool IgnoreHeldBeforeCancellation { get; init; }
        public bool HangFirstBefore { get; init; }
        public bool FailBefore { get; init; }
        public bool HoldNextAfter { get; set; }
        public RecordingBackend() => _inner = new OneShotBackend(info =>
        {
            if (_current is { IsBefore: true } && HangFirstBefore && _beforeCount == 1)
            { info.ArgumentList.Add("--fake-hang-after"); info.ArgumentList.Add("0"); }
        });
        public IReadOnlyList<Invocation> Runs { get { lock (_runs) return _runs.ToArray(); } }
        public Task<Invocation> NextAsync() => _started.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(SafetyTimeout);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            var invocation = new Invocation(request);
            _current = invocation;
            int beforeNumber = invocation.IsBefore ? ++_beforeCount : 0;
            lock (_runs) _runs.Add(invocation);
            using var registration = token.Register(() => invocation.Cancelled.TrySetResult());
            _started.Writer.TryWrite(invocation);
            try
            {
                bool held = invocation.IsBefore ? beforeNumber == 1 && HoldFirstBefore : HoldNextAfter;
                if (held && !invocation.IsBefore) HoldNextAfter = false;
                bool ignoreCancellation = invocation.IsBefore && held && IgnoreHeldBeforeCancellation;
                if (held) await invocation.Release.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : token);
                if (invocation.Aborted) return new PreviewRunResult(-12, "", true);
                if (invocation.IsBefore && FailBefore)
                    return new PreviewRunResult(17, "BEFORE_FAILURE");
                var result = await _inner.RunAsync(request, new CaptureSink(sink, invocation),
                    ignoreCancellation ? CancellationToken.None : token);
                return result with { StandardError = (invocation.IsBefore ? "BEFORE\n" : "AFTER\n") + result.StandardError };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // WaitAsyncの取消継続が先に登録を破棄しても、実際に中断した事実を取りこぼさない。
                invocation.Cancelled.TrySetResult();
                throw;
            }
            finally { invocation.Completed.TrySetResult(); }
        }
        public ValueTask AbortAsync()
        {
            foreach (var run in Runs) { run.Aborted = true; run.Release.TrySetResult(); }
            return _inner.AbortAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await AbortAsync();
            await _inner.DisposeAsync();
        }
        private sealed class CaptureSink(IPreviewSink inner, Invocation invocation) : IPreviewSink
        {
            public void OnHeader(Y4mHeader header)
            {
                invocation.ProcessId.TrySetResult(int.Parse(header.ExtTags["XPID"]));
                inner.OnHeader(header);
            }
            public void OnFrame(Y4mFrame frame) { invocation.Frames.Add(frame); inner.OnFrame(frame); }
            public void OnOutput() => inner.OnOutput();
        }
    }
    private sealed class Invocation(PreviewRequest request)
    {
        public PreviewRequest Request { get; } = request;
        public bool IsBefore => Request.VppArgs.Count == 0;
        public bool Aborted { get; set; }
        public List<Y4mFrame> Frames { get; } = new();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> ProcessId { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class TestServices : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-compare-{Guid.NewGuid():N}");
        public TestServices() => Directory.CreateDirectory(_directory);
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}

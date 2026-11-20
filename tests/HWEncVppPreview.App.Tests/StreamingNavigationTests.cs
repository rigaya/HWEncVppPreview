using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class StreamingNavigationTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;

    [AvaloniaFact]
    public async Task CachedReverseTargetBeforeStreamFirstFrameFinishesIdle()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var backend = new GapNavigationBackend();
        await using var vm = TestViewModels.Create(services, backend, null, null,
            (frame, token) => Task.FromResult(Enumerable.Repeat(frame.Data[0], frame.Header.Width * frame.Header.Height * 4).ToArray()));
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "gap-navigation.ts";
        vm.CurrentSeconds = 103;
        vm.InputArgsText = "--avhw";
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        vm.CurrentSeconds = 100;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Null(vm.ErrorText);
        vm.CurrentSeconds = 101;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        // 順逆の移動を経て、先頭位置へ戻した最終表示を確認する。
        vm.CurrentSeconds = 100;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(100, vm.CurrentSeconds);
        var image = Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(vm.PreviewImage);
        using var pixels = image.Lock();
        Assert.Equal((byte)239, System.Runtime.InteropServices.Marshal.ReadByte(pixels.Address));
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaTheory]
    [InlineData("", 0, 9)]
    [InlineData("--vpp-tweak brightness=0.1", 0, 9)]
    [InlineData("--vpp-nlmeans", 2, 7)]
    [InlineData("--vpp-kfm", 2, 7)]
    public async Task WarmupPolicyKeepsBeforeIndependentAndRetainsCachedPreviousFrame(string filter, double warmup, double seek)
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var foreground = new ConcurrentQueue<PreviewRequest>();
        var background = new ConcurrentQueue<PreviewRequest>();
        var first = new RecordingBackend(foreground);
        var second = new RecordingBackend(background);
        await using var vm = TestViewModels.Create(services, first, input.Probe, null, null, second,
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "warmup.ts";
        vm.CurrentSeconds = 10;
        vm.InputArgsText = "--avhw";
        vm.IsShowingBefore = true;
        if (filter.Length > 0) vm.FilterLines.Add(new FilterLineViewModel { Text = filter });
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        var after = Assert.Single(foreground);
        Assert.Equal(warmup, after.WarmupSeconds);
        Assert.Equal(seek, CommandBuilder.Plan(after).SeekSeconds);
        Assert.True(vm.ErrorText == null, vm.ErrorText + "\n" + vm.LogText);
        Assert.NotNull(vm.PreviewImage);
        if (filter.Length > 0)
        {
            // 時間方向のAfterでも、空フィルタのBeforeへ暖機を引き継がない。
            var before = Assert.Single(background);
            Assert.Empty(before.VppArgs);
            Assert.Equal(0, before.WarmupSeconds);
            Assert.Equal(9, CommandBuilder.Plan(before).SeekSeconds);
        }
        // 暖機0でもGOP用の前余白は残り、直後の1枚戻りを追加取得なしで表示する。
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Single(foreground);
        Assert.True(vm.CurrentSeconds < 10);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task ForwardDemandUsesOnePhysicalStreamAndPrefetchDoesNotChangeDisplayedTarget()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var requests = new ConcurrentQueue<PreviewRequest>();
        var displayed = new ConcurrentQueue<double?>();
        var backend = new RecordingBackend(requests);
        await using var vm = TestViewModels.Create(services, backend, input.Probe, null,
            (frame, token) => { displayed.Enqueue(frame.Xts); return Task.FromResult(YuvToBgra.Convert(frame)); },
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "stream.ts";
        vm.CurrentSeconds = 10;
        vm.InputArgsText = "--avhw --fake-duration 00:01:00.00";
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.PreviewImage);
        Assert.Single(requests);
        Assert.True(requests.Single().Continuous);
        Assert.Equal(2, requests.Single().PreMarginSeconds);
        Assert.Equal(3, CommandBuilder.Plan(requests.Single()).ActualPreMargin);
        Assert.Equal(7, CommandBuilder.Plan(requests.Single()).SeekSeconds);
        Assert.Equal(new PreviewCapabilities(true, true), requests.Single().Capabilities);
        Assert.Equal(1000, requests.Single().TimestampOriginSeconds);
        Assert.Equal(1010, displayed.Last()!.Value, 5);
        Assert.False(vm.IsBusy);
        vm.CurrentSeconds = 11;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(1011, displayed.Last()!.Value, 5);
        vm.CurrentSeconds = 15;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(1015, displayed.Last()!.Value, 5);
        Assert.Single(requests);
        int beforeStep = requests.Count;
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(15 - 1.0 / 30, vm.CurrentSeconds, 6);
        Assert.Equal(beforeStep, requests.Count);
        Assert.Equal(1000 + vm.CurrentSeconds, displayed.Last()!.Value, 5);
        vm.CurrentSeconds = 1;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, requests.Count);
        // 大きく戻るシークも通常の前方計画を使い、逆コマ用の過去窓を作らない。
        Assert.True(requests.Last().Continuous);
        Assert.Equal(2, requests.Last().PreMarginSeconds);
        Assert.Equal(1, requests.Last().PostMarginSeconds);
        Assert.Equal(1001, displayed.Last()!.Value, 5);
    }

    [AvaloniaFact]
    public async Task EncoderWithoutAdvertisedCapabilitiesKeepsFiniteLegacyRequests()
    {
        using var capabilities = new CapabilityScope(false);
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var requests = new ConcurrentQueue<PreviewRequest>();
        var backend = new RecordingBackend(requests);
        await using var vm = TestViewModels.Create(services, backend, input.Probe, null, null,
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "legacy.ts";
        vm.CurrentSeconds = 15;
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.NotNull(vm.PreviewImage);
        Assert.Equal(2, requests.Single().PreMarginSeconds);
        vm.CurrentSeconds = 1;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, requests.Count);
        Assert.Equal(2, requests.Last().PreMarginSeconds);
        Assert.Null(vm.ErrorText);
        Assert.All(requests, request =>
        {
            Assert.False(request.Continuous);
            Assert.Equal(new PreviewCapabilities(), request.Capabilities);
            Assert.Null(request.TimestampOriginSeconds);
        });
    }

    [AvaloniaFact]
    public async Task UnfilteredComparisonUsesLiveAfterCacheWithoutCompetingFiniteRun()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var foreground = new ConcurrentQueue<PreviewRequest>();
        var background = new ConcurrentQueue<PreviewRequest>();
        var first = new RecordingBackend(foreground);
        var second = new RecordingBackend(background);
        await using var vm = TestViewModels.Create(services, first, input.Probe, null, null, second,
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "unfiltered-concurrent.ts";
        vm.CurrentSeconds = 5;
        vm.InputArgsText = "--avhw --fake-duration 00:01:00.00";
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.NotNull(vm.PreviewImage);
        Assert.NotNull(vm.BeforeImage);
        Assert.NotSame(vm.PreviewImage, vm.BeforeImage);
        Assert.Single(foreground);
        Assert.Empty(background);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBeforeBusy);
    }

    [AvaloniaFact]
    public async Task FilteredBeforeUsesIndependentFiniteBackendAfterAfterDemandSettles()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var foreground = new ConcurrentQueue<PreviewRequest>();
        var background = new ConcurrentQueue<PreviewRequest>();
        var first = new RecordingBackend(foreground);
        var second = new RecordingBackend(background);
        await using var vm = TestViewModels.Create(services, first, input.Probe, null, null, second,
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "before.ts";
        vm.CurrentSeconds = 5;
        vm.InputArgsText = "--avhw --fake-duration 00:01:00.00";
        vm.IsShowingBefore = true;
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.01" });
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
        Assert.Single(foreground);
        Assert.Single(background);
        Assert.True(foreground.Single().Continuous);
        Assert.False(background.Single().Continuous);
        Assert.Empty(background.Single().VppArgs);
        Assert.Equal(1000, background.Single().TimestampOriginSeconds);
        Assert.False(vm.IsBeforeBusy);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaTheory]
    [InlineData("--vpp-nlmeans sigma=0.01", null, 30)]
    [InlineData("--vpp-decimate", null, 15)]
    [InlineData("--vpp-nlmeans sigma=0.01", "double", 60)]
    [InlineData("--vpp-nlmeans sigma=0.01", "film", 24)]
    public async Task RepeatedForwardStepsRefreshBeforeBeyondItsActuallyReceivedWindow(string filter, string? outputRate, double afterFps)
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var foreground = new ConcurrentQueue<PreviewRequest>();
        var background = new ConcurrentQueue<PreviewRequest>();
        var beforeFrames = new ConcurrentDictionary<Y4mFrame, byte>();
        var beforeDisplay = new ConcurrentQueue<double>();
        var first = new RecordingBackend(foreground);
        var second = new RecordingBackend(background, frame => beforeFrames.TryAdd(frame, 0));
        await using var vm = TestViewModels.Create(services, first, input.Probe, null,
            (frame, token) =>
            {
                if (beforeFrames.ContainsKey(frame) && frame.Xts is double timestamp) beforeDisplay.Enqueue(timestamp - 1000);
                return Task.FromResult(YuvToBgra.Convert(frame));
            }, second, debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "before-window.ts";
        vm.CurrentSeconds = 10;
        vm.InputArgsText = "--avhw --fake-duration 00:01:00.00"
                + (outputRate == null ? "" : " --fake-output-rate " + outputRate);
        vm.IsShowingBefore = true;
        vm.FilterLines.Add(new FilterLineViewModel { Text = filter });
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Single(background);
        // 入力枚数制限と出力レートが違っても、最初の実取得末尾より先まで小刻みに移動する。
        int steps = Math.Max(90, (int)Math.Ceiling((beforeFrames.Keys.Max(frame => frame.Xts!.Value) - 1000 - 10 + 1) * afterFps));
        for (int i = 0; i < steps; ++i)
        {
            vm.StepFrameCommand.Execute(1);
            await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            Assert.Null(vm.ErrorText);
            Assert.True(Math.Abs(beforeDisplay.Last() - vm.CurrentSeconds) <= 1.0 / (2 * (outputRate == "double" ? 60 : outputRate == "film" ? 24 : 30)) + 1e-6,
                $"step={i}, target={vm.CurrentSeconds:R}, before={beforeDisplay.Last():R}, requests={background.Count}");
            Assert.False(vm.IsBeforeBusy);
        }
        Assert.True(background.Count > 1, "実取得窓の外では元画像の追加要求が必要");
        Assert.Single(foreground);
        Assert.Equal(10 + steps / afterFps, vm.CurrentSeconds, 5);
    }

    [AvaloniaFact]
    public async Task UiDisposeReapsFakeServersWithAfterPausedAndBeforeCompleted()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var foreground = new DisposalProbeBackend();
        var background = new DisposalProbeBackend();
        var vm = TestViewModels.Create(services, foreground, null, null, null, background,
            debounceOverride: TimeSpan.Zero);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "dispose-paused.ts";
        vm.CurrentSeconds = 60;
        vm.InputArgsText = "--avhw --fake-duration 00:02:00.00";
        vm.IsShowingBefore = true;
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-kfm" });
        var window = new HWEncVppPreview.App.Views.MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
            await foreground.Paused.Task.WaitWithDiagnosticsAsync(Safety);
            await background.Finished.Task.WaitWithDiagnosticsAsync(Safety);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            Assert.Null(vm.ErrorText);
            Assert.NotNull(vm.PreviewImage);
            Assert.NotNull(vm.BeforeImage);
            window.Close();
            await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
            foreach (int pid in foreground.ProcessIds.Concat(background.ProcessIds).Distinct())
            {
                try { using var process = System.Diagnostics.Process.GetProcessById(pid); Assert.True(process.HasExited, $"偽エンコーダが残っています: {pid}"); }
                catch (ArgumentException) { }
            }
        }
        finally { window.Close(); await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task VmDisposeDoesNotWaitAgainForBackendLeftAfterControllerDeadline()
    {
        using var capabilities = new CapabilityScope(false);
        using var services = new Services();
        var clock = new VmShutdownClock();
        await using var input = new SyntheticInputProbe(clock);
        var backend = new UnresponsiveVmBackend();
        var vm = new MainWindowViewModel(services, clock, backend, input.Probe, null, null, readTextOverride: SyntheticInputProbe.ReadTextAsync)
        { EncoderPath = FakeEncoderPath(), InputPath = "dispose-unresponsive.ts" };
        Task? dispose = null;
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
            dispose = vm.DisposeAsync().AsTask();
            await clock.GraceArmed.Task.WaitWithDiagnosticsAsync(Safety);
            clock.Advance(TimeSpan.FromSeconds(2));
            await backend.Aborted.Task.WaitWithDiagnosticsAsync(Safety);
            await clock.RemainingArmed.Task.WaitWithDiagnosticsAsync(Safety);
            clock.Advance(TimeSpan.FromSeconds(3));
            // Coreが期限で放棄したRunをVMのIdle待ちから再び無期限に待たない。
            await dispose.WaitWithDiagnosticsAsync(Safety);
            Assert.False(backend.Running.IsCompleted);
            Assert.Null(vm.PreviewImage);
        }
        finally
        {
            backend.Release();
            if (dispose != null) await dispose.WaitWithDiagnosticsAsync(Safety);
            else await vm.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task VmDisposeBoundsConverterThatIgnoresItsCancellationToken()
    {
        using var capabilities = new CapabilityScope(false);
        using var services = new Services();
        var clock = new VmShutdownClock();
        await using var input = new SyntheticInputProbe(clock);
        var backend = new SingleFrameVmBackend();
        var converting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var converted = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Y4mFrame? pending = null;
        var vm = new MainWindowViewModel(services, clock, backend, input.Probe, null,
            (frame, token) => { pending = frame; converting.TrySetResult(); return converted.Task; }, readTextOverride: SyntheticInputProbe.ReadTextAsync)
        { EncoderPath = FakeEncoderPath(), InputPath = "dispose-converter.ts" };
        Task? dispose = null;
        string? firstFailedStage = null;
        try
        {
            await WaitStageAsync(vm.InitializeAsync(false), "初期化");
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await WaitStageAsync(converting.Task, "変換開始");
            dispose = vm.DisposeAsync().AsTask();
            await WaitStageAsync(backend.Disposed.Task, "backend.Disposed");
            // この通知は他の5秒タイマーでも成立するため、VMの総期限だけを識別した証拠にはしない。
            await WaitStageAsync(clock.FullDeadlineArmed.Task, "clock.FullDeadlineArmed");
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitStageAsync(dispose, "Dispose完了");
            Assert.False(converted.Task.IsCompleted);
            Assert.Null(vm.PreviewImage);
        }
        finally
        {
            if (pending != null) converted.TrySetResult(YuvToBgra.Convert(pending));
            else converted.TrySetCanceled();
            if (dispose != null) await WaitStageAsync(dispose, "finallyのDispose完了");
            else await WaitStageAsync(vm.DisposeAsync().AsTask(), "finallyのDispose開始・完了");
        }
        await WaitStageAsync(vm.WaitForIdleAsync(), "WaitForIdle完了");
        Assert.Null(vm.PreviewImage);

        async Task WaitStageAsync(Task task, string stage)
        {
            try { await task.WaitWithDiagnosticsAsync(Safety); }
            catch (Exception exception)
            {
                firstFailedStage ??= stage;
                // 同じSafetyで止まっても、どの待ちと未完了処理かを失敗ログだけで区別できるようにする。
                throw new InvalidOperationException(
                    $"取消を無視する変換の終了検査: 最初の失敗段階={firstFailedStage}、今回の待機段階={stage}。"
                    + $"backend.Disposed={backend.Disposed.Task.Status}, "
                    + $"FullDeadlineArmed={clock.FullDeadlineArmed.Task.Status}, "
                    + $"Dispose={dispose?.Status.ToString() ?? "未開始"}, "
                    + $"converter={converted.Task.Status}, fakeTimestamp={clock.GetTimestamp()}", exception);
            }
        }
    }

    [AvaloniaFact]
    public async Task VmDisposeBoundsFinalCleanupWhenUiPumpIsStopped()
    {
        using var capabilities = new CapabilityScope(false);
        using var services = new Services();
        var clock = new VmShutdownClock();
        await using var input = new SyntheticInputProbe(clock);
        var backend = new SingleFrameVmBackend();
        var vm = new MainWindowViewModel(services, clock, backend, input.Probe, null, null, readTextOverride: SyntheticInputProbe.ReadTextAsync)
        { EncoderPath = FakeEncoderPath(), InputPath = "dispose-ui-pump.ts" };
        Task? dispose = null;
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            Assert.NotNull(vm.PreviewImage);
            // UIを同期的に占有しても、終了期限の監視はThreadPool上で進み続ける。
            Task advance = Task.Run(async () =>
            {
                await backend.Disposed.Task.WaitWithDiagnosticsAsync(Safety);
                await clock.FullDeadlineArmed.Task.WaitWithDiagnosticsAsync(Safety);
                clock.Advance(TimeSpan.FromSeconds(5));
            });
            dispose = vm.DisposeAsync().AsTask();
            dispose.WaitWithDiagnosticsAsync(Safety).GetAwaiter().GetResult();
            await advance.WaitWithDiagnosticsAsync(Safety);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Null(vm.PreviewImage);
        }
        finally { if (dispose != null) await dispose.WaitWithDiagnosticsAsync(Safety); else await vm.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task KnownInputBoundariesFinishDemandWithoutLeavingBusy(double target)
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var requests = new ConcurrentQueue<PreviewRequest>();
        var backend = new RecordingBackend(requests);
        await using var vm = TestViewModels.Create(services, backend, input.Probe, null, null,
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "end.ts";
        vm.CurrentSeconds = target;
        vm.InputArgsText = "--avhw --fake-duration 00:00:03.00 --fake-input-end 3";
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
        Assert.Equal(target == 0 ? 0 : 3 - 1.0 / 30, vm.CurrentSeconds, 5);
        Assert.Equal(3, requests.Single().InputDurationSeconds);
    }

    [AvaloniaFact]
    public async Task EditCancelsBeforeThatIsRunningWhileAfterIsPaused()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var foreground = new RecordingBackend(new());
        var background = new BlockingFirstBeforeBackend();
        await using var vm = TestViewModels.Create(services, foreground, input.Probe, null, null, background,
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "before-cancel.ts";
        vm.CurrentSeconds = 5;
        vm.InputArgsText = "--avhw --fake-duration 00:01:00.00";
        vm.IsShowingBefore = true;
        var filter = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.01" };
        vm.FilterLines.Add(filter);
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await background.Started.Task.WaitWithDiagnosticsAsync(Safety);
        // 前景の需要が確定しても、実行中の元画像の取消源は維持される。
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.True(vm.IsBeforeBusy);
        filter.Text = "--vpp-nlmeans sigma=0.02";
        await background.Cancelled.Task.WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsBeforeBusy);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task ShiftedOverlappingSegmentsStepEveryPixelOnceAndIgnoreWarmDuplicates()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        await using var input = new SyntheticInputProbe(clock);
        var cache = new FrameCache();
        var request = new PreviewRequest
        {
            EncoderPath = FakeEncoderPath(), InputFile = "phase.ts", Fps = 30,
            TargetSeconds = 1, Capabilities = new(true, true), TimestampOriginSeconds = 1000,
        };
        var first = Enumerable.Range(23, 6).Select(index => PhaseFrame(index, 0, index == 28 ? (byte)228 : null)).ToArray();
        var second = Enumerable.Range(24, 7).Select(index => PhaseFrame(index, 0.00834, index == 24 ? (byte)224 : null)).ToArray();
        // 位相の違う27番も共通safe域へ含める。28番の暖機画像228は引き続き範囲外。
        Assert.NotNull(cache.StoreSegment(request, first, new(1000, 0, true), 1, 27.0 / 24 + 0.00834));
        Assert.NotNull(cache.StoreSegment(request with { TargetSeconds = 28.0 / 24 }, second,
            new(1000, 0, true), 1.04, 30.0 / 24 + 0.00834));
        var requests = new ConcurrentQueue<PreviewRequest>();
        var converted = new ConcurrentQueue<byte>();
        var backend = new PhasePrefetchBackend(requests);
        await using var vm = new MainWindowViewModel(services, clock, backend, input.Probe, cache,
            (frame, token) => { converted.Enqueue(frame.Data[0]); return Task.FromResult(YuvToBgra.Convert(frame)); }, readTextOverride: SyntheticInputProbe.ReadTextAsync)
        { EncoderPath = FakeEncoderPath(), InputPath = request.InputFile, CurrentSeconds = 1 };
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal((byte)24, converted.Last());
        var shown = new List<byte> { converted.Last() };
        foreach (int expected in Enumerable.Range(25, 6))
        {
            vm.StepFrameCommand.Execute(1);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            Assert.Equal((byte)expected, converted.Last());
            shown.Add(converted.Last());
        }
        foreach (int expected in Enumerable.Range(24, 6).Reverse())
        {
            vm.StepFrameCommand.Execute(-1);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            Assert.Equal((byte)expected, converted.Last());
            shown.Add(converted.Last());
        }
        Assert.DoesNotContain((byte)224, shown);
        Assert.DoesNotContain((byte)228, shown);
        Assert.Equal(13, shown.Count);
        // 終端の順先読みと、逆境界の従来先読みを別々に検証する。
        Assert.Equal(2, requests.Count);
        var forward = Assert.Single(requests, request => request.CorrelationId > 0);
        Assert.True(forward.Continuous);
        Assert.True(forward.Capabilities.TimestampPassthrough);
        Assert.Equal(30.0 / 24 + 0.00834, forward.TargetSeconds, 6);
        var prefetch = Assert.Single(requests, request => request.CorrelationId < long.MinValue / 2);
        Assert.False(prefetch.Continuous);
        Assert.Equal(1, prefetch.TargetSeconds, 6);
        Assert.Equal(5, prefetch.PreMarginSeconds);
        Assert.Equal(1, prefetch.PostMarginSeconds);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaTheory]
    [InlineData(0.00834, true)]
    [InlineData(0.033, false)]
    public async Task RejectedPixelsOrClockShiftCannotRestoreDiscardedNavigationAnchor(double phase, bool changePixels)
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var cache = new FrameCache();
        var request = new PreviewRequest
        {
            EncoderPath = FakeEncoderPath(), InputFile = "reject-phase.ts", Fps = 30, TargetSeconds = 1,
            Capabilities = new(true, true), TimestampOriginSeconds = 1000,
        };
        var first = Assert.IsType<PreviewSegmentSnapshot>(cache.StoreSegment(request,
            Enumerable.Range(23, 9).Select(index => PhaseFrame(index, 0)).ToArray(), new(1000, 0, true), 1, 30.0 / 24));
        var requests = new ConcurrentQueue<PreviewRequest>();
        var backend = new RecordingBackend(requests);
        await using var vm = TestViewModels.Create(services, backend, input.Probe, cache, null,
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = request.InputFile;
        vm.CurrentSeconds = 1;
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Empty(requests);
        var rejected = Assert.IsType<PreviewSegmentSnapshot>(cache.StoreSegment(request with { TargetSeconds = 1.2 },
            Enumerable.Range(24, 9).Select(index => PhaseFrame(index, phase, changePixels ? (byte)(index + 100) : null)).ToArray(),
            new(1000, 0, true), 1, 32.0 / 24 + phase));
        Assert.True(rejected.Clock.TrustedAbsolute);
        Assert.False(rejected.CanStitch);
        Assert.Null(cache.GetSegment(first.SegmentId));
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        // 破棄された区間の隣へ即時移動せず、要求単位で取り直す。
        Assert.NotEmpty(requests);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task LateOverlapPendingStreamNeverRestoresTheOldNavigationWindow()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var cache = new FrameCache();
        var request = new PreviewRequest
        {
            EncoderPath = FakeEncoderPath(), InputFile = "late-overlap.ts", Fps = 30, TargetSeconds = 10,
            Capabilities = new(true, true), TimestampOriginSeconds = 1000,
        };
        var header = new Y4mHeader { Width = 64, Height = 36, FpsNum = 30, FpsDen = 1 };
        byte[] seed = System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>());
        var frames = Enumerable.Range(270, 91).Select(index =>
        {
            byte[] data = new byte[header.FrameBytes];
            for (int i = 0; i < data.Length; ++i) data[i] = (byte)(seed[i % seed.Length] + index);
            return new Y4mFrame(header, 1000 + index / 30.0, 1.0 / 30, data);
        }).ToArray();
        var old = Assert.IsType<PreviewSegmentSnapshot>(cache.StoreSegment(request, frames, new(1000, 0, true), 9, 12));
        var requests = new ConcurrentQueue<PreviewRequest>();
        var backend = new RecordingBackend(requests);
        await using var vm = TestViewModels.Create(services, backend, input.Probe, cache, null,
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = request.InputFile;
        vm.CurrentSeconds = 10;
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Empty(requests);
        vm.CurrentSeconds = 13;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Single(requests);
        // 重なりは12秒の1枚だけで、2枚以上の画素照合が揃うまでは新物理区間だけを使う。
        var pending = cache.GetNavigationFrames(request with { TargetSeconds = 13 });
        Assert.NotEmpty(pending);
        Assert.All(pending, frame =>
        {
            Assert.NotEqual(old.SegmentId, frame.SegmentId);
            Assert.False(frame.Segment.CanStitch);
            Assert.True(frame.Clock.TrustedAbsolute);
        });
        for (int i = 0; i < 30; ++i)
        {
            vm.StepFrameCommand.Execute(-1);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        }
        Assert.Equal(12, vm.CurrentSeconds, 6);
        Assert.Single(requests);
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.True(requests.Count > 1);
        Assert.Equal(12 - 1.0 / 30, vm.CurrentSeconds, 6);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaTheory]
    [InlineData("--fake-clock-backstep-at", 1000, false, null)]
    [InlineData("--fake-clock-jump-at", 1000, false, null)]
    [InlineData("--fake-clock-backstep-at", 0, false, null)]
    [InlineData("--fake-clock-backstep-at", 1000, true, null)]
    [InlineData("--fake-clock-backstep-at", 1000, false, "input")]
    [InlineData("--fake-clock-backstep-at", 1000, false, "vpp")]
    public async Task ReplayReplacesSameGenerationAndNeverAppliesTheOldConversion(string anomaly, double origin, bool oldConversionThrows, string? explicitLocation)
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        await using var input = new SyntheticInputProbe(clock);
        var backend = new ReplaySignalingBackend();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int conversions = 0, applied = 0;
        await using var vm = new MainWindowViewModel(services, clock, backend, input.Probe, null,
            async (frame, token) =>
            {
                if (Interlocked.Increment(ref conversions) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                    if (oldConversionThrows) throw new InvalidOperationException("取り直し前の古い変換エラー");
                }
                return YuvToBgra.Convert(frame);
            }, readTextOverride: SyntheticInputProbe.ReadTextAsync)
        { EncoderPath = FakeEncoderPath(), InputPath = "replay.ts", CurrentSeconds = 10,
            InputArgsText = "--avhw " + anomaly + " 125 --fake-origin-seconds "
                + origin.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (explicitLocation == "input" ? " --timestamp-passthrough" : "") };
        if (explicitLocation == "vpp") vm.FilterLines.Add(new FilterLineViewModel { Text = "--timestamp-passthrough" });
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.PreviewImage)) ++applied; };
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await firstStarted.Task.WaitWithDiagnosticsAsync(Safety);
        backend.ContinueAfterFirstConversion.TrySetResult();
        await backend.ReplaySelected.Task.WaitWithDiagnosticsAsync(Safety);
        releaseFirst.TrySetResult();
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(1, applied);
        var winning = Assert.IsType<Y4mFrame>(backend.ReplayTargetFrame);
        var bitmap = Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(vm.PreviewImage);
        byte[] actual = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        using (var locked = bitmap.Lock())
            for (int row = 0; row < bitmap.PixelSize.Height; ++row)
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(locked.Address, row * locked.RowBytes),
                    actual, row * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
        Assert.Equal(YuvToBgra.Convert(winning), actual);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
        Assert.Equal(10, vm.CurrentSeconds);
        // Beforeは負のCorrelationIdで同じqueueに記録されるため、前景の同世代replayだけを検査する。
        var foregroundRequests = backend.Requests.Where(request => request.CorrelationId > 0).ToArray();
        var initialRequest = Assert.Single(foregroundRequests, request => request.Continuous);
        var replayRequest = Assert.Single(foregroundRequests, request =>
            request.CorrelationId == initialRequest.CorrelationId && !request.Continuous);
        Assert.Equal(2, foregroundRequests.Length);
        Assert.True(initialRequest.Capabilities.TimestampPassthrough);
        Assert.False(replayRequest.Capabilities.TimestampPassthrough);
        Assert.False(replayRequest.Continuous);
        Assert.DoesNotContain("--timestamp-passthrough", replayRequest.InputArgs);
        Assert.DoesNotContain("--timestamp-passthrough", replayRequest.VppArgs);
        int replayRequests = foregroundRequests.Length;
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(10 + 1.0 / 30, vm.CurrentSeconds, 6);
        Assert.Equal(replayRequests, backend.Requests.Count(request => request.CorrelationId > 0));
        vm.CurrentSeconds = 20;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        var laterForegroundRequests = backend.Requests.Where(request => request.CorrelationId > 0).Skip(replayRequests).ToArray();
        Assert.NotEmpty(laterForegroundRequests);
        Assert.All(laterForegroundRequests, request => Assert.False(request.Capabilities.TimestampPassthrough));
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReplayCannotReviveOldImageOrOldConversionError(bool oldConversionThrows)
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        await using var input = new SyntheticInputProbe(clock);
        var backend = new ReplaySignalingBackend(failReplay: true);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int conversions = 0;
        await using var vm = new MainWindowViewModel(services, clock, backend, input.Probe, null,
            async (frame, token) =>
            {
                if (Interlocked.Increment(ref conversions) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                    if (oldConversionThrows) throw new InvalidOperationException("古い変換エラー");
                }
                return YuvToBgra.Convert(frame);
            }, readTextOverride: SyntheticInputProbe.ReadTextAsync)
        { EncoderPath = FakeEncoderPath(), InputPath = "replay-failed.ts", CurrentSeconds = 10,
            InputArgsText = "--avhw --fake-clock-backstep-at 125" };
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await firstStarted.Task.WaitWithDiagnosticsAsync(Safety);
        Task failureNotified = ObserveErrorNotification(vm);
        backend.ContinueAfterFirstConversion.TrySetResult();
        await backend.ReplayFailed.Task.WaitWithDiagnosticsAsync(Safety);
        await failureNotified.WaitWithDiagnosticsAsync(Safety);
        string? failure = vm.ErrorText;
        releaseFirst.TrySetResult();
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Null(vm.PreviewImage);
        Assert.Equal(failure, vm.ErrorText);
        Assert.Contains("23", vm.ErrorText!);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsBeforeBusy);
        Assert.Equal("エラー", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task NearWrapOriginKeepsMetadataAndStartsWithRelativeFiniteClock()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var requests = new ConcurrentQueue<PreviewRequest>();
        var backend = new RecordingBackend(requests);
        await using var vm = TestViewModels.Create(services, backend, input.Probe, null, null,
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "near-wrap.ts";
        vm.CurrentSeconds = 10;
        vm.InputArgsText = "--avhw --fake-origin-seconds 95443.5";
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
        Assert.All(requests, request =>
        {
            Assert.False(request.Continuous);
            Assert.False(request.Capabilities.TimestampPassthrough);
            Assert.False(request.TimestampOriginTrusted);
            Assert.Equal(95443.5, request.TimestampOriginSeconds);
        });
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(10 + 1.0 / 30, vm.CurrentSeconds, 6);
        Assert.Null(vm.ErrorText);
        vm.CurrentSeconds = 1;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(3, requests.Last().PreMarginSeconds);
        Assert.False(requests.Last().Capabilities.TimestampPassthrough);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeforeOnlyReplayAppendsItsReasonWithoutReplacingAfterImageStatusOrLog(bool protocolFailure)
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        await using var input = new SyntheticInputProbe(TimeProvider.System);
        var foregroundRequests = new ConcurrentQueue<PreviewRequest>();
        var foreground = new RecordingBackend(foregroundRequests);
        var background = new BeforeFaultBackend(protocolFailure);
        var afterImage = new TaskCompletionSource<Avalonia.Media.Imaging.Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vm = TestViewModels.Create(services, foreground, input.Probe, null, null, background,
            debounceOverride: TimeSpan.Zero, readTextOverride: SyntheticInputProbe.ReadTextAsync);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "before-only.ts";
        vm.CurrentSeconds = 10;
        vm.IsShowingBefore = true;
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.01" });
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.PreviewImage) && vm.PreviewImage != null)
                afterImage.TrySetResult(vm.PreviewImage);
        };
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        var original = await afterImage.Task.WaitWithDiagnosticsAsync(Safety);
        await background.Started.Task.WaitWithDiagnosticsAsync(Safety);
        vm.LogText = "after-log-marker";
        background.Allow.TrySetResult();
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        if (protocolFailure) Assert.Null(vm.BeforeImage);
        else Assert.NotNull(vm.BeforeImage);
        Assert.Same(original, vm.PreviewImage);
        Assert.StartsWith("完了", vm.StatusText);
        Assert.StartsWith("after-log-marker", vm.LogText);
        Assert.Contains("[preview] timestamp fallback: ", vm.LogText);
        Assert.Null(vm.ErrorText);
    }

    // 時刻異常と読取待機を実プロセスなしで再現し、試験の時計と独立させる。
    private sealed class SyntheticStreamingEncoder(int forceBackstep = -1) : IPreviewBackend
    {
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            string[] args = request.InputArgs.ToArray();
            string Value(string name, string fallback)
            {
                int index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
            }
            double origin = double.Parse(Value("--fake-origin-seconds", "1000"), System.Globalization.CultureInfo.InvariantCulture);
            int backstep = forceBackstep >= 0 ? forceBackstep : int.Parse(Value("--fake-clock-backstep-at", "-1"));
            int jump = int.Parse(Value("--fake-clock-jump-at", "-1"));
            var plan = CommandBuilder.Plan(request);
            int start = (int)Math.Round((plan.SeekSeconds ?? 0) * 30);
            var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420jpeg");
            byte[] seed = System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>());
            sink.OnHeader(header);
            double previous = 0;
            int count = request.Continuous ? int.MaxValue : plan.FrameCount;
            try
            {
                for (int index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    ValueTask permission = sink.BeforeReadAsync(token);
                    if (!permission.IsCompleted)
                    {
                        sink.OnReadStateChanged(false);
                        await permission.AsTask().WaitAsync(token);
                    }
                    else await permission.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    sink.OnReadStateChanged(true);
                    double timestamp = request.Capabilities.TimestampPassthrough ? origin + (start + index) / 30.0 : index / 30.0;
                    if (request.Capabilities.TimestampPassthrough && jump > 0 && index >= jump) timestamp += 10;
                    if (request.Capabilities.TimestampPassthrough && index == backstep) timestamp = previous - 2.0 / 30;
                    previous = timestamp;
                    byte[] pixels = new byte[header.FrameBytes];
                    for (int offset = 0; offset < pixels.Length; offset++) pixels[offset] = unchecked((byte)(seed[offset % seed.Length] + start + index));
                    sink.OnOutput();
                    sink.OnFrame(new Y4mFrame(header, timestamp, 1.0 / 30, pixels));
                }
                return new PreviewRunResult(0, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new PreviewRunResult(-12, "", true);
            }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BeforeFaultBackend(bool protocolFailure = false) : IPreviewBackend
    {
        private readonly SyntheticStreamingEncoder _inner = new(forceBackstep: 5);
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Allow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            Started.TrySetResult();
            await Allow.Task.WaitAsync(cancellationToken);
            if (protocolFailure && !request.Capabilities.TimestampPassthrough)
                throw new PreviewBackendException("replay protocol failure", "protocol stderr");
            return await _inner.RunAsync(request, sink, cancellationToken);
        }
        public ValueTask AbortAsync() => _inner.AbortAsync();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class ReplaySignalingBackend(bool failReplay = false) : IPreviewBackend
    {
        private readonly SyntheticStreamingEncoder _inner = new();
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public TaskCompletionSource ReplaySelected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueAfterFirstConversion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Y4mFrame? ReplayTargetFrame { get; private set; }
        public TaskCompletionSource ReplayFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (failReplay && !request.Capabilities.TimestampPassthrough)
            {
                ReplayFailed.TrySetResult();
                return Task.FromResult(new PreviewRunResult(23, "replay failed before output"));
            }
            return _inner.RunAsync(request, new ReplaySink(request, sink, this), cancellationToken);
        }
        public ValueTask AbortAsync() => _inner.AbortAsync();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        private sealed class ReplaySink(PreviewRequest request, IPreviewSink sink, ReplaySignalingBackend owner) : IPreviewSink
        {
            private int _frames;
            public void OnHeader(Y4mHeader header) => sink.OnHeader(header);
            public void OnOutput() => sink.OnOutput();
            public async ValueTask BeforeReadAsync(CancellationToken token)
            {
                await sink.BeforeReadAsync(token);
                if (request.Capabilities.TimestampPassthrough && _frames >= 124)
                    await owner.ContinueAfterFirstConversion.Task.WaitAsync(token);
            }
            public void OnReadStateChanged(bool reading) => sink.OnReadStateChanged(reading);
            public void OnFrame(Y4mFrame frame)
            {
                ++_frames;
                sink.OnFrame(frame);
                double target = CommandBuilder.Plan(request).ActualPreMargin;
                if (!request.Capabilities.TimestampPassthrough && frame.Xts is double time)
                {
                    if (Math.Abs(time - target) < 1e-6) owner.ReplayTargetFrame = frame;
                    if (time >= target + 0.1) owner.ReplaySelected.TrySetResult();
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task NavigationLookupCannotAdoptAnotherKeeperWhenItPurgesTheRequestedInterval()
    {
        using var services = new Services();
        var cache = new FrameCache();
        var request = new PreviewRequest { EncoderPath = "encoder", InputFile = "ambiguous.ts", Fps = 24,
            TargetSeconds = 1.008, Capabilities = new(true, true), TimestampOriginSeconds = 1000 };
        var header = new Y4mHeader { Width = 64, Height = 36, FpsNum = 24, FpsDen = 1 };
        Y4mFrame Frame(double time) => new(header, 1000 + time, null, new byte[header.FrameBytes]);
        // 先頭実frameでsafeStartが繰り上がっても、曖昧な密VFR部分が共通safe域内に残るようにする。
        var old = Assert.IsType<PreviewSegmentSnapshot>(cache.StoreSegment(request,
            new[] { Frame(0.95), Frame(1.008), Frame(1.05), Frame(1.09) }, new(1000, 0, true), 0.9, 1.1));
        var current = Assert.IsType<PreviewSegmentSnapshot>(cache.StoreSegment(request,
            new[] { Frame(1), Frame(1.004), Frame(1.008), Frame(1.05), Frame(1.09) }, new(1000, 0, true), 0.9, 1.1));
        Assert.True(current.CanStitch);
        await using var vm = new MainWindowViewModel(services, cache: cache);
        Assert.Empty(vm.NavigationFrames(request, old));
        Assert.Null(cache.GetSegment(old.SegmentId));
        Assert.False(cache.GetSegment(current.SegmentId)!.CanStitch);
        // 明示的にcurrentを採用してからなら、その物理区間だけの参照を利用できる。
        var unit = vm.NavigationFrames(request, cache.GetSegment(current.SegmentId));
        Assert.Equal(5, unit.Count);
        Assert.All(unit, frame => Assert.Equal(current.SegmentId, frame.SegmentId));
    }

    private static Y4mFrame PhaseFrame(int index, double phase, byte? pixel = null)
    {
        var header = new Y4mHeader { Width = 64, Height = 36, FpsNum = 24, FpsDen = 1 };
        return new(header, 1000 + index / 24.0 + phase, null,
            Enumerable.Repeat(pixel ?? (byte)index, header.FrameBytes).ToArray());
    }

    private sealed class PhasePrefetchBackend(ConcurrentQueue<PreviewRequest> requests) : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            requests.Enqueue(request);
            if (request.CorrelationId >= long.MinValue / 2)
                throw new InvalidOperationException("位相cache内の移動で通常の前景要求を起動してはいけません");
            // 手作りcacheと同じ24fps/画素を返し、先読みだけが不整合な実Fake画像を混ぜない。
            var plan = CommandBuilder.Plan(request);
            double seek = plan.SeekSeconds ?? 0;
            double end = seek + plan.ActualPreMargin + request.PostMarginSeconds;
            sink.OnHeader(PhaseFrame(0, 0).Header);
            for (int i = (int)Math.Ceiling(seek * 24 - 1e-8); i <= (int)Math.Floor(end * 24 + 1e-8); ++i)
            {
                token.ThrowIfCancellationRequested();
                sink.OnOutput();
                sink.OnFrame(PhaseFrame(i, 0));
            }
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingFirstBeforeBackend : IPreviewBackend
    {
        private readonly ServerBackend _inner = TestProcesses.CreateServerBackend();
        private int _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstToken { get; private set; }
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) != 1)
                return await _inner.RunAsync(request, sink, cancellationToken);
            FirstToken = cancellationToken;
            Started.TrySetResult();
            try { await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Cancelled.TrySetResult(); }
            return new(-12, "", true);
        }
        public ValueTask AbortAsync() => _inner.AbortAsync();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class RecordingBackend(ConcurrentQueue<PreviewRequest> requests, Action<Y4mFrame>? observe = null) : IPreviewBackend
    {
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken cancellationToken = default)
            // 試験専用の追加入力指定は未監査として、従来の2秒fixtureを維持する。
            => Task.FromResult<bool?>(request.InputArgs.Any(value => value.StartsWith("--fake-", StringComparison.Ordinal))
                || request.VppArgs.Any(value => value is "--vpp-kfm" or "--vpp-nlmeans"));

        private readonly ServerBackend _inner = TestProcesses.CreateServerBackend(configureStartInfo: info =>
        {
            info.Environment["FAKE_ENCODER_PROBE_SUPPORTED"] = "1";
            info.Environment["FAKE_ENCODER_PROBE_HISTORY"] = "0";
        });
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        { requests.Enqueue(request); return _inner.RunAsync(request, observe == null ? sink : new ObservingSink(sink, observe), cancellationToken); }
        public ValueTask AbortAsync() => _inner.AbortAsync();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class VmShutdownClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        public TaskCompletionSource GraceArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RemainingArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FullDeadlineArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public void Advance(TimeSpan delta) => _clock.Advance(delta);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(2)) GraceArmed.TrySetResult();
            if (dueTime == TimeSpan.FromSeconds(3)) RemainingArmed.TrySetResult();
            if (dueTime == TimeSpan.FromSeconds(5)) FullDeadlineArmed.TrySetResult();
            return timer;
        }
    }

    private sealed class SingleFrameVmBackend : IPreviewBackend
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
            sink.OnHeader(header);
            sink.OnFrame(new(header, 0, 1.0 / 30, new byte[6]));
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private sealed class UnresponsiveVmBackend : IPreviewBackend
    {
        private readonly TaskCompletionSource<PreviewRunResult> _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewRunResult> Running => _running.Task;
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        { Started.TrySetResult(); return _running.Task; }
        public ValueTask AbortAsync() { Aborted.TrySetResult(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Release() => _running.TrySetResult(new(-12, "", true));
    }

    private sealed class DisposalProbeBackend : IPreviewBackend
    {
        private readonly ServerBackend _inner = TestProcesses.CreateServerBackend();
        private int _activeRuns;
        private int _readPaused;
        public int ActiveRuns => Volatile.Read(ref _activeRuns);
        public bool IsReadPaused => Volatile.Read(ref _readPaused) != 0;
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public ConcurrentQueue<PreviewRunResult> Results { get; } = new();
        public ConcurrentQueue<int> ProcessIds { get; } = new();
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Enqueue(request);
            Interlocked.Increment(ref _activeRuns);
            try
            {
                var result = await _inner.RunAsync(request, new ProbeSink(this, sink), token).ConfigureAwait(false);
                Results.Enqueue(result);
                return result;
            }
            finally { Interlocked.Decrement(ref _activeRuns); Finished.TrySetResult(); }
        }
        public ValueTask AbortAsync() => _inner.AbortAsync();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        private sealed class ProbeSink(DisposalProbeBackend owner, IPreviewSink inner) : IPreviewSink
        {
            public void OnHeader(Y4mHeader header)
            {
                owner.ProcessIds.Enqueue(int.Parse(header.ExtTags["XPID"], System.Globalization.CultureInfo.InvariantCulture));
                inner.OnHeader(header);
            }
            public void OnFrame(Y4mFrame frame) => inner.OnFrame(frame);
            public void OnOutput() => inner.OnOutput();
            public ValueTask BeforeReadAsync(CancellationToken token) => inner.BeforeReadAsync(token);
            public void OnReadStateChanged(bool reading)
            {
                Volatile.Write(ref owner._readPaused, reading ? 0 : 1);
                if (!reading) owner.Paused.TrySetResult();
                inner.OnReadStateChanged(reading);
            }
        }
    }

    private sealed class ObservingSink(IPreviewSink inner, Action<Y4mFrame> observe) : IPreviewSink
    {
        public void OnHeader(Y4mHeader header) => inner.OnHeader(header);
        public void OnFrame(Y4mFrame frame) { observe(frame); inner.OnFrame(frame); }
        public void OnOutput() => inner.OnOutput();
        public ValueTask BeforeReadAsync(CancellationToken token) => inner.BeforeReadAsync(token);
        public void OnReadStateChanged(bool reading) => inner.OnReadStateChanged(reading);
    }

    private sealed class GapNavigationBackend : IPreviewBackend
    {
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<bool?>(false);

        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Enqueue(request);
            var header = new Y4mHeader { Width = 64, Height = 36, FpsNum = 30, FpsDen = 1 };
            sink.OnHeader(header);
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            // InputProbeと同じ30fpsで、streamは3枚、逆有限窓は9枚後ろへseek着地させる。
            int start = (int)Math.Round(seek * 30) + (request.Continuous ? 3 : 9);
            int count = request.Continuous ? int.MaxValue : CommandBuilder.Plan(request).FrameCount;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var permission = sink.BeforeReadAsync(token);
                    if (!permission.IsCompleted) sink.OnReadStateChanged(false);
                    await permission;
                    sink.OnReadStateChanged(true);
                    sink.OnOutput();
                    int tick = start + i;
                    byte pixel = (byte)(tick % 251);
                    sink.OnFrame(new(header, 1000 + tick / 30.0, 1.0 / 30,
                        Enumerable.Repeat(pixel, header.FrameBytes).ToArray()));
                }
                return new(0, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(-12, "", true); }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class CapabilityScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        public CapabilityScope(bool enabled = true) => Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", enabled ? "1" : null);
        public void Dispose() => Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", _previous);
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

    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-stream-{Guid.NewGuid():N}");
        public Services() => Directory.CreateDirectory(_directory);
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose() => Directory.Delete(_directory, true);
    }
}

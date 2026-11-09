using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Headless.XUnit;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.App.Tests;

public sealed class StreamingNavigationTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public async Task CachedReverseTargetBeforeStreamFirstFrameFinishesIdle()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new GapNavigationBackend();
        await using var vm = new MainWindowViewModel(services, clock, backend, backend)
        { EncoderPath = FakeEncoderPath(), InputPath = "gap-navigation.ts", CurrentSeconds = 103, InputArgsText = "--avhw" };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Single(backend.Requests);
        // 時刻setterはシーク専用になったため、隣不足の位置だけをfixtureとして設定し、逆コマで有限窓を作る。
        typeof(MainWindowViewModel).GetMethod("SetTimeOnly", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { 100d });
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Null(vm.ErrorText);
        vm.CurrentSeconds = 101;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Null(vm.ErrorText);
        Assert.Equal(3, backend.Requests.Count);
        Assert.False(backend.Requests.ElementAt(1).Continuous);
        Assert.Equal(5, backend.Requests.ElementAt(1).PreMarginSeconds);
        Assert.True(backend.Requests.ElementAt(2).Continuous);
        // 旧有限区間だけに100秒の画像がある。新streamの先頭100.1秒へ需要を再結合してはいけない。
        vm.CurrentSeconds = 100;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, backend.Requests.Count);
        Assert.Equal(100, vm.CurrentSeconds);
        Assert.NotNull(vm.PreviewImage);
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
        var clock = new FakeTimeProvider();
        var foreground = new ConcurrentQueue<PreviewRequest>();
        var background = new ConcurrentQueue<PreviewRequest>();
        var first = new RecordingBackend(foreground);
        var second = new RecordingBackend(background);
        await using var vm = new MainWindowViewModel(services, clock, first, first, null, null, null, second, second)
        { EncoderPath = FakeEncoderPath(), InputPath = "warmup.ts", CurrentSeconds = 10,
            InputArgsText = "--avhw", IsShowingBefore = true };
        if (filter.Length > 0) vm.FilterLines.Add(new FilterLineViewModel { Text = filter });
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        async Task WaitForBeforeAsync()
        {
            await UiTestWait.UntilAsync(() => vm.BeforeImage != null, Safety);
        }
        await WaitForBeforeAsync();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        var after = Assert.Single(foreground);
        Assert.Equal(warmup, after.WarmupSeconds);
        Assert.Equal(seek, CommandBuilder.Plan(after).SeekSeconds);
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
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
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        var clock = new FakeTimeProvider();
        var requests = new ConcurrentQueue<PreviewRequest>();
        var displayed = new ConcurrentQueue<double?>();
        var backend = new RecordingBackend(requests);
        await using var vm = new MainWindowViewModel(services, clock, backend, backend, null, null,
            (frame, token) => { displayed.Enqueue(frame.Xts); return Task.FromResult(YuvToBgra.Convert(frame)); })
        { EncoderPath = FakeEncoderPath(), InputPath = "stream.ts", CurrentSeconds = 10,
            InputArgsText = "--avhw --fake-duration 00:01:00.00" };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(1011, displayed.Last()!.Value, 5);
        vm.CurrentSeconds = 15;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(1015, displayed.Last()!.Value, 5);
        Assert.Single(requests);
        int beforeStep = requests.Count;
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(15 - 1.0 / 30, vm.CurrentSeconds, 6);
        Assert.Equal(beforeStep, requests.Count);
        Assert.Equal(1000 + vm.CurrentSeconds, displayed.Last()!.Value, 5);
        vm.CurrentSeconds = 1;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        var clock = new FakeTimeProvider();
        var requests = new ConcurrentQueue<PreviewRequest>();
        var backend = new RecordingBackend(requests);
        await using var vm = new MainWindowViewModel(services, clock, backend, backend)
        { EncoderPath = FakeEncoderPath(), InputPath = "legacy.ts", CurrentSeconds = 15 };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.NotNull(vm.PreviewImage);
        Assert.Equal(2, requests.Single().PreMarginSeconds);
        vm.CurrentSeconds = 1;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        var clock = new FakeTimeProvider();
        var foreground = new ConcurrentQueue<PreviewRequest>();
        var background = new ConcurrentQueue<PreviewRequest>();
        var first = new RecordingBackend(foreground);
        var second = new RecordingBackend(background);
        await using var vm = new MainWindowViewModel(services, clock, first, first, null, null, null, second, second)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "unfiltered-concurrent.ts", CurrentSeconds = 5,
            InputArgsText = "--avhw --fake-duration 00:01:00.00"
        };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        var clock = new FakeTimeProvider();
        var foreground = new ConcurrentQueue<PreviewRequest>();
        var background = new ConcurrentQueue<PreviewRequest>();
        var first = new RecordingBackend(foreground);
        var second = new RecordingBackend(background);
        await using var vm = new MainWindowViewModel(services, clock, first, first, null, null, null, second, second)
        { EncoderPath = FakeEncoderPath(), InputPath = "before.ts", CurrentSeconds = 5,
            InputArgsText = "--avhw --fake-duration 00:01:00.00", IsShowingBefore = true };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.01" });
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        async Task WaitForBeforeAsync()
        {
            await UiTestWait.UntilAsync(() => vm.BeforeImage != null, Safety);
        }
        await WaitForBeforeAsync();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        var clock = new FakeTimeProvider();
        var foreground = new ConcurrentQueue<PreviewRequest>();
        var background = new ConcurrentQueue<PreviewRequest>();
        var beforeFrames = new ConcurrentDictionary<Y4mFrame, byte>();
        var beforeDisplay = new ConcurrentQueue<double>();
        var first = new RecordingBackend(foreground);
        var second = new RecordingBackend(background, frame => beforeFrames.TryAdd(frame, 0));
        await using var vm = new MainWindowViewModel(services, clock, first, first, null, null,
            (frame, token) =>
            {
                if (beforeFrames.ContainsKey(frame) && frame.Xts is double timestamp) beforeDisplay.Enqueue(timestamp - 1000);
                return Task.FromResult(YuvToBgra.Convert(frame));
            }, second, second)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-window.ts", CurrentSeconds = 10,
            InputArgsText = "--avhw --fake-duration 00:01:00.00"
                + (outputRate == null ? "" : " --fake-output-rate " + outputRate), IsShowingBefore = true };
        vm.FilterLines.Add(new FilterLineViewModel { Text = filter });
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        async Task WaitForBeforeAsync()
        {
            if (vm.BeforeImage != null && !vm.IsBeforeBusy) return;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            System.ComponentModel.PropertyChangedEventHandler changed = (_, _) =>
            {
                if (vm.BeforeImage != null && !vm.IsBeforeBusy) completion.TrySetResult();
            };
            // 空のUI操作を回し続けず、タイムアウト時にも購読を必ず解除する。
            vm.PropertyChanged += changed;
            try
            {
                if (vm.BeforeImage != null && !vm.IsBeforeBusy) completion.TrySetResult();
                await completion.Task.WaitAsync(Safety);
            }
            finally { vm.PropertyChanged -= changed; }
        }
        await WaitForBeforeAsync();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Single(background);
        // 入力枚数制限と出力レートが違っても、最初の実取得末尾より先まで小刻みに移動する。
        int steps = Math.Max(90, (int)Math.Ceiling((beforeFrames.Keys.Max(frame => frame.Xts!.Value) - 1000 - 10 + 1) * afterFps));
        for (int i = 0; i < steps; ++i)
        {
            vm.StepFrameCommand.Execute(1);
            await vm.WaitForPreparationAsync().WaitAsync(Safety);
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            await WaitForBeforeAsync();
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            Assert.Null(vm.ErrorText);
            Assert.True(Math.Abs(beforeDisplay.Last() - vm.CurrentSeconds) <= 1.0 / (2 * (outputRate == "double" ? 60 : outputRate == "film" ? 24 : 30)) + 1e-6,
                $"step={i}, target={vm.CurrentSeconds:R}, before={beforeDisplay.Last():R}, requests={background.Count}");
            Assert.False(vm.IsBeforeBusy);
        }
        Assert.True(background.Count > 1, "実取得窓の外では元画像の追加要求が必要");
        Assert.Single(foreground);
        Assert.Equal(10 + steps / afterFps, vm.CurrentSeconds, 5);
    }

    [AvaloniaTheory]
    [InlineData(0.49, 1)]
    [InlineData(0.51, 2)]
    public async Task ActualBeforeTailUsesHalfOutputDurationForCacheHit(double tailFraction, int expectedRequests)
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var foreground = new RecordingBackend(new());
        var requests = new ConcurrentQueue<PreviewRequest>();
        var received = new ConcurrentQueue<Y4mFrame>();
        var background = new RecordingBackend(requests, received.Enqueue);
        await using var vm = new MainWindowViewModel(services, clock, foreground, foreground, null, null, null, background, background)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-tail.ts", CurrentSeconds = 10,
            InputArgsText = "--avhw --fake-duration 00:01:00.00 --fake-output-rate double", IsShowingBefore = true };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-nlmeans" });
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        async Task DrainAsync()
        {
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            await UiTestWait.UntilAsync(() => vm.BeforeImage != null && !vm.IsBeforeBusy, Safety);
            await vm.WaitForIdleAsync().WaitAsync(Safety);
        }
        await DrainAsync().WaitAsync(Safety);
        var last = received.Last();
        double duration = FrameSelector.Duration(last, 30);
        vm.CurrentSeconds = last.Xts!.Value - 1000 + duration * tailFraction;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await DrainAsync().WaitAsync(Safety);
        Assert.Equal(expectedRequests, requests.Count);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBeforeBusy);
        Assert.NotNull(vm.BeforeImage);
    }

    [AvaloniaFact]
    public async Task BeforeRoundTripWithinActualWindowDoesNotStartAnotherRequest()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var foregroundRequests = new ConcurrentQueue<PreviewRequest>();
        var foreground = new RecordingBackend(foregroundRequests);
        var backgroundRequests = new ConcurrentQueue<PreviewRequest>();
        var background = new RecordingBackend(backgroundRequests);
        await using var vm = new MainWindowViewModel(services, clock, foreground, foreground, null, null, null, background, background)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-roundtrip.ts", CurrentSeconds = 10,
            InputArgsText = "--avhw --fake-duration 00:01:00.00", IsShowingBefore = true };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-nlmeans" });
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        async Task DrainAsync()
        {
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            await UiTestWait.UntilAsync(() => vm.BeforeImage != null && !vm.IsBeforeBusy, Safety);
            await vm.WaitForIdleAsync().WaitAsync(Safety);
        }
        await DrainAsync().WaitAsync(Safety);
        var original = vm.BeforeImage;
        foreach (double target in new[] { 10.2, 10.0 })
        {
            vm.CurrentSeconds = target;
            await vm.WaitForPreparationAsync().WaitAsync(Safety);
            await DrainAsync().WaitAsync(Safety);
            Assert.Single(backgroundRequests);
            Assert.Null(vm.ErrorText);
            Assert.False(vm.IsBeforeBusy);
            Assert.NotNull(vm.BeforeImage);
        }
        Assert.NotSame(original, vm.BeforeImage);
        Assert.Single(foregroundRequests);
    }

    [AvaloniaFact]
    public async Task UiDisposeReapsFakeServersWithAfterPausedAndBeforeCompleted()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var foreground = new DisposalProbeBackend(clock);
        var background = new DisposalProbeBackend(clock);
        var fallback = new OneShotBackend();
        var backgroundFallback = new OneShotBackend();
        var vm = new MainWindowViewModel(services, clock, foreground, fallback, null, null, null, background, backgroundFallback)
        { EncoderPath = FakeEncoderPath(), InputPath = "dispose-paused.ts", CurrentSeconds = 60,
            InputArgsText = "--avhw --fake-duration 00:02:00.00", IsShowingBefore = true };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-kfm" });
        var window = new HWEncParamViewer.App.Views.MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        try
        {
            await vm.InitializeAsync(false).WaitAsync(Safety);
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await foreground.Paused.Task.WaitAsync(Safety);
            await background.Finished.Task.WaitAsync(Safety);
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            // M1の右キー連打と同じ操作経路で、同じAfter物理要求を前へ進める。
            for (int i = 0; i < 150; ++i)
            {
                vm.StepFrameCommand.Execute(1);
                await vm.WaitForPreparationAsync().WaitAsync(Safety);
                await vm.WaitForIdleAsync().WaitAsync(Safety);
            }
            async Task WaitForFinalPauseAsync()
            {
                await UiTestWait.UntilAsync(() => foreground.IsReadPaused && background.ActiveRuns == 0 && !vm.IsBeforeBusy, Safety);
            }
            await WaitForFinalPauseAsync();
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            Assert.True(Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
            Assert.True(foreground.IsReadPaused);
            Assert.Equal(1, foreground.ActiveRuns);
            Assert.Equal(0, background.ActiveRuns);
            Assert.Single(foreground.Requests);
            Assert.NotEmpty(background.Results);
            Assert.All(background.Results, result =>
            {
                Assert.Equal(0, result.ExitCode);
                Assert.False(result.IsCancelled);
                Assert.Contains("RGYPREVIEW END", result.StandardError);
            });
            Assert.Null(vm.ErrorText);
            Assert.NotNull(vm.PreviewImage);
            Assert.NotNull(vm.BeforeImage);
            // 実時間のsleepやFake時計の追加進行なしで、UI文脈からの終了とOS子プロセス回収を確かめる。
            window.Close();
            await vm.DisposeAsync().AsTask().WaitAsync(Safety);
            Assert.Equal(0, foreground.ActiveRuns);
            foreach (int pid in foreground.ProcessIds.Concat(background.ProcessIds).Distinct())
            {
                try { using var process = System.Diagnostics.Process.GetProcessById(pid); Assert.True(process.HasExited, $"FakeEncoder子プロセス{pid}が残っている"); }
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
        var backend = new UnresponsiveVmBackend();
        var vm = new MainWindowViewModel(services, clock, backend, backend)
        { EncoderPath = FakeEncoderPath(), InputPath = "dispose-unresponsive.ts" };
        Task? dispose = null;
        try
        {
            await vm.InitializeAsync(false).WaitAsync(Safety);
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await backend.Started.Task.WaitAsync(Safety);
            dispose = vm.DisposeAsync().AsTask();
            await clock.GraceArmed.Task.WaitAsync(Safety);
            clock.Advance(TimeSpan.FromSeconds(2));
            await backend.Aborted.Task.WaitAsync(Safety);
            await clock.RemainingArmed.Task.WaitAsync(Safety);
            clock.Advance(TimeSpan.FromSeconds(3));
            // Coreが期限で放棄したRunをVMのIdle待ちから再び無期限に待たない。
            await dispose.WaitAsync(Safety);
            Assert.False(backend.Running.IsCompleted);
            Assert.Null(vm.PreviewImage);
        }
        finally
        {
            backend.Release();
            if (dispose != null) await dispose.WaitAsync(Safety);
            else await vm.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task VmDisposeBoundsConverterThatIgnoresItsCancellationToken()
    {
        using var capabilities = new CapabilityScope(false);
        using var services = new Services();
        var clock = new VmShutdownClock();
        var backend = new SingleFrameVmBackend();
        var converting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var converted = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Y4mFrame? pending = null;
        var vm = new MainWindowViewModel(services, clock, backend, backend, null, null,
            (frame, token) => { pending = frame; converting.TrySetResult(); return converted.Task; })
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
            try { await task.WaitAsync(Safety); }
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
        var backend = new SingleFrameVmBackend();
        var vm = new MainWindowViewModel(services, clock, backend, backend)
        { EncoderPath = FakeEncoderPath(), InputPath = "dispose-ui-pump.ts" };
        Task? dispose = null;
        try
        {
            await vm.InitializeAsync(false).WaitAsync(Safety);
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            Assert.NotNull(vm.PreviewImage);
            // UIを同期的に占有しても、終了期限の監視はThreadPool上で進み続ける。
            Task advance = Task.Run(async () =>
            {
                await backend.Disposed.Task.WaitAsync(Safety);
                await clock.FullDeadlineArmed.Task.WaitAsync(Safety);
                clock.Advance(TimeSpan.FromSeconds(5));
            });
            dispose = vm.DisposeAsync().AsTask();
            dispose.WaitAsync(Safety).GetAwaiter().GetResult();
            await advance.WaitAsync(Safety);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Null(vm.PreviewImage);
        }
        finally { if (dispose != null) await dispose.WaitAsync(Safety); else await vm.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task KnownInputBoundariesFinishDemandWithoutLeavingBusy(double target)
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var requests = new ConcurrentQueue<PreviewRequest>();
        var backend = new RecordingBackend(requests);
        await using var vm = new MainWindowViewModel(services, clock, backend, backend)
        { EncoderPath = FakeEncoderPath(), InputPath = "end.ts", CurrentSeconds = target,
            InputArgsText = "--avhw --fake-duration 00:00:03.00 --fake-input-end 3" };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
        Assert.Equal(target, vm.CurrentSeconds);
        Assert.Equal(3, requests.Single().InputDurationSeconds);
    }

    [AvaloniaFact]
    public async Task EditCancelsBeforeThatIsRunningWhileAfterIsPaused()
    {
        using var capabilities = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var foreground = new RecordingBackend(new());
        var background = new BlockingFirstBeforeBackend();
        await using var vm = new MainWindowViewModel(services, clock, foreground, foreground, null, null, null, background, background)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-cancel.ts", CurrentSeconds = 5,
            InputArgsText = "--avhw --fake-duration 00:01:00.00", IsShowingBefore = true };
        var filter = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.01" };
        vm.FilterLines.Add(filter);
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await background.Started.Task.WaitAsync(Safety);
        // 前景の需要が確定しても、実行中の元画像の取消源は維持される。
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.False(background.FirstToken.IsCancellationRequested);
        Assert.True(vm.IsBeforeBusy);
        filter.Text = "--vpp-nlmeans sigma=0.02";
        await background.Cancelled.Task.WaitAsync(Safety);
        Assert.True(background.FirstToken.IsCancellationRequested);
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        async Task WaitForBeforeAsync()
        {
            await UiTestWait.UntilAsync(() => vm.BeforeImage != null, Safety);
        }
        await WaitForBeforeAsync();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        await using var vm = new MainWindowViewModel(services, clock, backend, backend, null, cache,
            (frame, token) => { converted.Enqueue(frame.Data[0]); return Task.FromResult(YuvToBgra.Convert(frame)); })
        { EncoderPath = FakeEncoderPath(), InputPath = request.InputFile, CurrentSeconds = 1 };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal((byte)24, converted.Last());
        var shown = new List<byte> { converted.Last() };
        foreach (int expected in Enumerable.Range(25, 6))
        {
            vm.StepFrameCommand.Execute(1);
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            Assert.Equal((byte)expected, converted.Last());
            shown.Add(converted.Last());
        }
        foreach (int expected in Enumerable.Range(24, 6).Reverse())
        {
            vm.StepFrameCommand.Execute(-1);
            await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        var clock = new FakeTimeProvider();
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
        await using var vm = new MainWindowViewModel(services, clock, backend, backend, null, cache)
        { EncoderPath = FakeEncoderPath(), InputPath = request.InputFile, CurrentSeconds = 1 };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Empty(requests);
        var rejected = Assert.IsType<PreviewSegmentSnapshot>(cache.StoreSegment(request with { TargetSeconds = 1.2 },
            Enumerable.Range(24, 9).Select(index => PhaseFrame(index, phase, changePixels ? (byte)(index + 100) : null)).ToArray(),
            new(1000, 0, true), 1, 32.0 / 24 + phase));
        Assert.True(rejected.Clock.TrustedAbsolute);
        Assert.False(rejected.CanStitch);
        Assert.Null(cache.GetSegment(first.SegmentId));
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        var clock = new FakeTimeProvider();
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
        await using var vm = new MainWindowViewModel(services, clock, backend, backend, null, cache)
        { EncoderPath = FakeEncoderPath(), InputPath = request.InputFile, CurrentSeconds = 10 };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Empty(requests);
        vm.CurrentSeconds = 13;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
            await vm.WaitForIdleAsync().WaitAsync(Safety);
        }
        Assert.Equal(12, vm.CurrentSeconds, 6);
        Assert.Single(requests);
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.True(requests.Count > 1);
        Assert.Equal(12 - 1.0 / 30, vm.CurrentSeconds, 6);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task PendingAnchorRefreshUsesCurrentApprovalAndFrameIdentityAfterPrefixRemoval()
    {
        var header = new Y4mHeader { Width = 64, Height = 36, FpsNum = 30, FpsDen = 1 };
        var cache = new FrameCache(header.FrameBytes * 3);
        var request = new PreviewRequest { EncoderPath = "encoder", InputFile = "rolling", Fps = 30,
            Capabilities = new(true, true), TimestampOriginSeconds = 1000 };
        long id = cache.BeginSegment(request, new(1000, 0, true), 0);
        cache.PinCurrent(id, 0);
        Y4mFrame Frame(int index) => new(header, 1000 + index * 0.5, 0.5, new byte[header.FrameBytes]);
        Assert.True(cache.Append(id, Frame(0)));
        var wanted = Frame(1);
        Assert.True(cache.Append(id, wanted));
        Assert.True(cache.Append(id, Frame(2)));
        var saved = new PreviewFrameReference(cache.GetSegment(id)! with { CanStitch = false }, 1);
        cache.PinCurrent(id, 0.5);
        Assert.True(cache.Append(id, Frame(3)));
        var current = cache.GetSegment(id)!;
        Assert.Same(wanted, current.Frames[0]);
        Assert.True(current.CanStitch);

        using var services = new Services();
        await using var vm = new MainWindowViewModel(services, new FakeTimeProvider(), cache: cache);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const double pendingTarget = 0.9;
        typeof(MainWindowViewModel).GetField("_navigationAnchor", flags)!.SetValue(vm, saved);
        typeof(MainWindowViewModel).GetField("_navigationAnchorTarget", flags)!.SetValue(vm, pendingTarget);
        var selectAnchor = typeof(MainWindowViewModel).GetMethod("NavigationAnchorIndex", flags)!;
        int AnchorIndex(IReadOnlyList<PreviewFrameReference> frames) =>
            (int)selectAnchor.Invoke(vm, new object[] { frames, pendingTarget, request.Fps })!;
        var direct = Enumerable.Range(0, current.Frames.Count)
            .Select(index => new PreviewFrameReference(current, index)).ToArray();
        // コマ送りで使う実経路を通し、削られた先頭の古い番号ではなく画像の同一性で辿る。
        Assert.Equal(0, AnchorIndex(direct));

        var peer = current with { SegmentId = id + 1, Frames = new[] { Frame(1), Frame(2), Frame(3) } };
        var equivalent = Enumerable.Range(0, peer.Frames.Count)
            .Select(index => new PreviewFrameReference(peer, index)).ToArray();
        Assert.NotSame(wanted, equivalent[0].Frame);
        Assert.False(FrameSelector.AreEquivalent(saved, equivalent[0]));
        // 別区間の候補では最新の接続許可も必要。古いfalseのままでは目標時刻へ戻ってしまう。
        Assert.Equal(0, AnchorIndex(equivalent));
        cache.RemoveSegment(id);
        // 実体がなくなったanchorは採らず、通常の時刻選択へ戻る。
        Assert.Equal(1, AnchorIndex(equivalent));
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
        var backend = new ReplaySignalingBackend();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int conversions = 0, applied = 0;
        await using var vm = new MainWindowViewModel(services, clock, backend, backend, null, null,
            async (frame, token) =>
            {
                if (Interlocked.Increment(ref conversions) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                    if (oldConversionThrows) throw new InvalidOperationException("取り直し前の古い変換エラー");
                }
                return YuvToBgra.Convert(frame);
            })
        { EncoderPath = FakeEncoderPath(), InputPath = "replay.ts", CurrentSeconds = 10,
            InputArgsText = "--avhw " + anomaly + " 125 --fake-origin-seconds "
                + origin.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (explicitLocation == "input" ? " --timestamp-passthrough" : "") };
        if (explicitLocation == "vpp") vm.FilterLines.Add(new FilterLineViewModel { Text = "--timestamp-passthrough" });
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.PreviewImage)) ++applied; };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await firstStarted.Task.WaitAsync(Safety);
        backend.ContinueAfterFirstConversion.TrySetResult();
        await backend.ReplaySelected.Task.WaitAsync(Safety);
        releaseFirst.TrySetResult();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(10 + 1.0 / 30, vm.CurrentSeconds, 6);
        Assert.Equal(replayRequests, backend.Requests.Count(request => request.CorrelationId > 0));
        vm.CurrentSeconds = 20;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        var backend = new ReplaySignalingBackend(failReplay: true);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int conversions = 0;
        await using var vm = new MainWindowViewModel(services, clock, backend, backend, null, null,
            async (frame, token) =>
            {
                if (Interlocked.Increment(ref conversions) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                    if (oldConversionThrows) throw new InvalidOperationException("古い変換エラー");
                }
                return YuvToBgra.Convert(frame);
            })
        { EncoderPath = FakeEncoderPath(), InputPath = "replay-failed.ts", CurrentSeconds = 10,
            InputArgsText = "--avhw --fake-clock-backstep-at 125" };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await firstStarted.Task.WaitAsync(Safety);
        backend.ContinueAfterFirstConversion.TrySetResult();
        await backend.ReplayFailed.Task.WaitAsync(Safety);
        async Task WaitForFailureAsync()
        {
            await UiTestWait.UntilAsync(() => vm.ErrorText != null, Safety);
        }
        await WaitForFailureAsync();
        string? failure = vm.ErrorText;
        releaseFirst.TrySetResult();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        var clock = new FakeTimeProvider();
        var requests = new ConcurrentQueue<PreviewRequest>();
        var backend = new RecordingBackend(requests);
        await using var vm = new MainWindowViewModel(services, clock, backend, backend)
        { EncoderPath = FakeEncoderPath(), InputPath = "near-wrap.ts", CurrentSeconds = 10,
            InputArgsText = "--avhw --fake-origin-seconds 95443.5" };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(10 + 1.0 / 30, vm.CurrentSeconds, 6);
        Assert.Null(vm.ErrorText);
        vm.CurrentSeconds = 1;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
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
        var clock = new FakeTimeProvider();
        var foregroundRequests = new ConcurrentQueue<PreviewRequest>();
        var foreground = new RecordingBackend(foregroundRequests);
        var background = new BeforeFaultBackend(protocolFailure);
        var afterImage = new TaskCompletionSource<Avalonia.Media.Imaging.Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vm = new MainWindowViewModel(services, clock, foreground, foreground, null, null, null, background, background)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-only.ts", CurrentSeconds = 10, IsShowingBefore = true };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.01" });
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.PreviewImage) && vm.PreviewImage != null)
                afterImage.TrySetResult(vm.PreviewImage);
        };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        var original = await afterImage.Task.WaitAsync(Safety);
        await background.Started.Task.WaitAsync(Safety);
        vm.LogText = "after-log-marker";
        background.Allow.TrySetResult();
        async Task WaitForBeforeAsync()
        {
            await UiTestWait.UntilAsync(() => protocolFailure
                ? background.Requests.Count >= 2 && !vm.IsBeforeBusy : vm.BeforeImage != null, Safety);
        }
        await WaitForBeforeAsync();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        if (protocolFailure) Assert.Null(vm.BeforeImage);
        else Assert.NotNull(vm.BeforeImage);
        Assert.Same(original, vm.PreviewImage);
        Assert.StartsWith("完了", vm.StatusText);
        Assert.StartsWith("after-log-marker", vm.LogText);
        Assert.Contains("[preview] timestamp fallback: ", vm.LogText);
        Assert.Null(vm.ErrorText);
        Assert.Single(foregroundRequests);
        Assert.True(foregroundRequests.Single().Capabilities.TimestampPassthrough);
        Assert.Equal(2, background.Requests.Count);
        Assert.False(background.Requests.Last().Capabilities.TimestampPassthrough);
    }

    private sealed class BeforeFaultBackend(bool protocolFailure = false) : IPreviewBackend
    {
        private readonly OneShotBackend _inner = new(info =>
        {
            info.ArgumentList.Add("--fake-clock-backstep-at");
            info.ArgumentList.Add("5");
        });
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
        private readonly OneShotBackend _inner = new();
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
        private readonly OneShotBackend _inner = new();
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
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
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

        private readonly OneShotBackend _inner = new();
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

    private sealed class DisposalProbeBackend(TimeProvider clock) : IPreviewBackend
    {
        private readonly ServerBackend _inner = new(clock);
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

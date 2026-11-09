using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.App.Tests;

public sealed class BeforeRecoveryTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(8);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LandingAfterLatestTargetRequestsOneRecovery(bool moveWhileRunning)
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(moveWhileRunning);
        await using var vm = new MainWindowViewModel(services, clock, backend, backend)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-recovery.ts", CurrentSeconds = 10, InputArgsText = "--avhw" };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        vm.IsShowingBefore = true;
        await backend.FirstBefore.Task.WaitAsync(Safety);
        if (moveWhileRunning)
        {
            await Until(() => !vm.IsBusy && vm.PreviewImage != null);
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            System.ComponentModel.PropertyChangedEventHandler applied = (_, change) =>
            {
                if (change.PropertyName != nameof(vm.PreviewImage) || vm.PreviewImage == null) return;
                var frame = typeof(MainWindowViewModel).GetField("_displayedFrame",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm) as Y4mFrame;
                if (frame?.Xts is double timestamp && Math.Abs(timestamp - 1009.3) < 1e-6) shown.TrySetResult();
            };
            vm.PropertyChanged += applied;
            try
            {
                for (int i = 0; i < 7; ++i)
                {
                    vm.StepFrameCommand.Execute(-1);
                    double expected = 10 - (i + 1) * 0.1;
                    await Until(() => Math.Abs(vm.CurrentSeconds - expected) < 1e-6);
                }
                // 目標更新は変換より先なので、最後のAfter表示を待ってからBeforeを解放し、描画との順序を競わせない。
                await shown.Task.WaitAsync(Safety);
                // PropertyChangedはsetter内で同期通知され、ApplyImage後半のQueueBeforeはまだ終わっていない。
                // UIを一巡させて同じApplyImageの残りを完了してから、保留中のBeforeを解放する。
                await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitAsync(Safety);
            }
            finally
            {
                vm.PropertyChanged -= applied;
            }
            Assert.Equal(9.3, vm.CurrentSeconds, 6);
        }
        backend.Release.TrySetResult();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        await Until(() => !vm.IsBeforeBusy);
        Assert.NotNull(vm.BeforeImage);
        Assert.True(backend.BeforeRequests.Count == 2, string.Join(" | ", backend.BeforeRequests.Select(request => $"target={request.TargetSeconds:R}, seek={CommandBuilder.Plan(request).SeekSeconds:R}, frames={CommandBuilder.Plan(request).FrameCount}, corr={request.CorrelationId}")));
        Assert.Equal(moveWhileRunning ? 9.3 : 10, backend.BeforeRequests.Last().TargetSeconds, 6);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task AfterWindowRecoveryNeverCarriesItsFilterIntoTheBeforeRequest()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(false, lateAfter: true);
        backend.Release.TrySetResult();
        await using var vm = new MainWindowViewModel(services, clock, backend, backend)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-recovery.ts", CurrentSeconds = 10, InputArgsText = "--avhw", IsShowingBefore = true };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        await vm.InitializeAsync(false).WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.PreviewImage);
        Assert.NotNull(vm.BeforeImage);
        Assert.True(backend.AfterRequests.Count >= 2);
        Assert.NotEmpty(backend.BeforeRequests);
        Assert.All(backend.BeforeRequests, request => Assert.Empty(request.VppArgs));
    }

    [AvaloniaFact]
    public async Task SameUnselectableTargetStopsAtTheWindowRecoveryLimit()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(false, alwaysLate: true);
        await using var vm = await Start(services, clock, backend);
        backend.Release.TrySetResult();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.True(backend.BeforeRequests.Count == 1 + PreviewWindowRecovery.MaximumRetries, string.Join(" | ", backend.BeforeRequests.Select(request => $"target={request.TargetSeconds:R}, seek={CommandBuilder.Plan(request).SeekSeconds:R}, frames={CommandBuilder.Plan(request).FrameCount}, corr={request.CorrelationId}")));
        Assert.False(vm.IsBeforeBusy);
        Assert.Null(vm.BeforeImage);
    }

    [AvaloniaFact]
    public async Task LatestTargetDuringRecoveryUsesTheCompletedSafeWindow()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(false, blockRecovery: true);
        await using var vm = await Start(services, clock, backend);
        backend.Release.TrySetResult();
        await backend.RecoveryStarted.Task.WaitAsync(Safety);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler applied = (_, change) =>
        {
            if (change.PropertyName != nameof(vm.PreviewImage) || vm.PreviewImage == null) return;
            var frame = typeof(MainWindowViewModel).GetField("_displayedFrame",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm) as Y4mFrame;
            if (frame?.Xts is double timestamp && Math.Abs(timestamp - 1009.3) < 1e-6) shown.TrySetResult();
        };
        vm.PropertyChanged += applied;
        try
        {
            for (int i = 0; i < 7; ++i)
            {
                vm.StepFrameCommand.Execute(-1);
                double expected = 10 - (i + 1) * 0.1;
                await Until(() => Math.Abs(vm.CurrentSeconds - expected) < 1e-6);
            }
            // 目標だけでなく最後のAfterのUI適用を待つ。早期Before1枚と競う順序はこの試験の対象外。
            await shown.Task.WaitAsync(Safety);
        }
        finally
        {
            vm.PropertyChanged -= applied;
        }
        backend.ReleaseRecovery.TrySetResult();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(9.3, vm.CurrentSeconds, 6);
        Assert.True(backend.BeforeRequests.Count == 2, string.Join(" | ", backend.BeforeRequests.Select(request => $"target={request.TargetSeconds:R}, seek={CommandBuilder.Plan(request).SeekSeconds:R}, frames={CommandBuilder.Plan(request).FrameCount}, corr={request.CorrelationId}")));
        Assert.NotNull(vm.BeforeImage);
        Assert.False(vm.IsBeforeBusy);
    }

    [AvaloniaFact]
    public async Task AfterUiFollowingEarlyBeforeFrameStartsTheSameThirdRequest()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(false, blockRecovery: true, stopAfterEarly: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAfter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<byte[]> ConvertFrame(Y4mFrame frame, CancellationToken token)
        {
            if (frame.Xts is double timestamp && Math.Abs(timestamp - 1009.3) < 1e-6)
            {
                entered.TrySetResult();
                await releaseAfter.Task.WaitAsync(token);
            }
            return YuvToBgra.Convert(frame);
        }
        await using var vm = await Start(services, clock, backend, ConvertFrame);
        try
        {
            backend.Release.TrySetResult();
            await backend.RecoveryStarted.Task.WaitAsync(Safety);
            for (int index = 0; index < 6; ++index)
            {
                vm.StepFrameCommand.Execute(-1);
                double expected = 10 - (index + 1) * 0.1;
                await Until(() => Math.Abs(vm.CurrentSeconds - expected) < 1e-6);
            }
            // 最後の入力は9.3へ確定済み。After画像のUI適用だけをconverterで保留する。
            vm.BeginSeekDrag();
            vm.EndSeekDrag(9.3);
            await entered.Task.WaitAsync(Safety);
            Assert.Equal(9.3, vm.CurrentSeconds, 6);
            backend.ReleaseRecovery.TrySetResult();
            await backend.EarlyOutsideCoreLock.Task.WaitAsync(Safety);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object context = typeof(MainWindowViewModel).GetField("_beforeExecutionContext", flags)!.GetValue(vm)!;
            var segment = (PreviewSegmentSnapshot)context.GetType().GetProperty("Segment")!.GetValue(context)!;
            Assert.Single(segment.Frames);
            Assert.Equal(1010, segment.Frames[0].Xts!.Value, 6);
            // OnFrameから戻った後なのでCore lockを持たない。実際のAfter適用→QueueBeforeを通す。
            releaseAfter.TrySetResult();
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            // 現在の余分な第3要求を固定する。後日の最適化でこの期待値は見直す。
            Assert.Equal(3, backend.BeforeRequests.Count);
            Assert.Equal(9.3, backend.BeforeRequests.Last().TargetSeconds, 6);
            var before = Assert.IsType<Y4mFrame>(typeof(MainWindowViewModel).GetField("_beforeDisplayedFrame", flags)!.GetValue(vm));
            Assert.Equal(1009.3, before.Xts!.Value, 6);
            Assert.NotNull(vm.BeforeImage);
            Assert.False(vm.IsBeforeBusy);
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F10:1 C420");
            byte[] expectedPixels = YuvToBgra.Convert(new Y4mFrame(header, 1009.3, 0.1, new byte[6]));
            byte[] actualPixels = new byte[16];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(actualPixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                vm.BeforeImage.CopyPixels(new Avalonia.PixelRect(vm.BeforeImage.PixelSize), handle.AddrOfPinnedObject(), actualPixels.Length, 8);
            }
            finally
            {
                handle.Free();
            }
            Assert.Equal(expectedPixels, actualPixels);
        }
        finally
        {
            releaseAfter.TrySetResult();
            backend.ReleaseRecovery.TrySetResult();
            backend.ReleaseEarlyTail.TrySetResult();
        }
    }

    [AvaloniaFact]
    public async Task DisposeCancelsUnfinishedBeforeWithoutRecovery()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(false);
        await using var vm = await Start(services, clock, backend);
        await vm.DisposeAsync().AsTask().WaitAsync(Safety);
        await backend.Cancelled.Task.WaitAsync(Safety);
        Assert.Single(backend.BeforeRequests);
        Assert.False(vm.IsBeforeBusy);
    }

    [AvaloniaFact]
    public async Task ExplicitRefreshCancelsOldBeforeAndDisplaysTheNewWindow()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(false);
        await using var vm = await Start(services, clock, backend);
        vm.RefreshCommand.Execute(null);
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await backend.Cancelled.Task.WaitAsync(Safety);
        backend.Release.TrySetResult();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.NotNull(vm.BeforeImage);
        Assert.Equal(2, backend.BeforeRequests.Count);
        Assert.False(vm.IsBeforeBusy);
    }

    [AvaloniaFact]
    public async Task UnreadBeforeUsesPhysicalWindowRatherThanLogicalWarmupMargin()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(false);
        await using var vm = await Start(services, clock, backend);
        Assert.Equal(9, CommandBuilder.Plan(backend.BeforeRequests.Single()).SeekSeconds);
        // 論理前余白2秒に入る8.5秒は拒否し、実seek以後の9.3秒だけを再利用する。
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var context = typeof(MainWindowViewModel).GetField("_beforeExecutionContext", flags)!.GetValue(vm)!;
        var method = typeof(MainWindowViewModel).GetMethod("ContainsBeforeTarget", flags)!;
        var request = backend.BeforeRequests.Single();
        Assert.Equal(false, method.Invoke(vm, new[] { context, request with { TargetSeconds = 8.5 } }));
        Assert.Equal(true, method.Invoke(vm, new[] { context, request with { TargetSeconds = 9.3 } }));
    }

    [AvaloniaTheory]
    [InlineData(true, 9.5, true)]
    [InlineData(false, 9.5, false)]
    [InlineData(true, 9.95, false)]
    public async Task NaturalEndSharesOnlyTheSafeFinalFrame(bool reachedEnd, double safeStart, bool expected)
    {
        using var services = new Services();
        var backend = new ControlledBackend(false);
        await using var vm = new MainWindowViewModel(services, new FakeTimeProvider(), backend, backend);
        var request = new PreviewRequest
        {
            EncoderPath = "encoder", InputFile = "end.ts", TargetSeconds = 10, Fps = 10,
            InputDurationSeconds = 10, Capabilities = new(true, true), TimestampOriginSeconds = 1000,
        };
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F10:1 C420");
        var frames = new[] { new Y4mFrame(header, 1009.9, 0.1, new byte[6]) };
        var segment = new PreviewSegmentSnapshot(1, request, new(1000, 9, true), frames,
            safeStart, 9.9, false, reachedEnd);
        // privateな再利用契約を直接確認し、UI専用のための公開APIは増やさない。
        var method = typeof(MainWindowViewModel).GetMethod("ContainsBeforeFrameTarget",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(expected, method.Invoke(vm, new object[] { request, frames, 10d, segment }));
    }

    private static async Task<MainWindowViewModel> Start(Services services, FakeTimeProvider clock, ControlledBackend backend,
        Func<Y4mFrame, CancellationToken, Task<byte[]>>? converter = null)
    {
        var vm = new MainWindowViewModel(services, clock, backend, backend, null, null, converter)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-recovery.ts", CurrentSeconds = 10, InputArgsText = "--avhw" };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        vm.IsShowingBefore = true;
        await backend.FirstBefore.Task.WaitAsync(Safety);
        await Until(() => vm.PreviewImage != null && !vm.IsBusy);
        return vm;
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Safety);
        while (!predicate()) await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitAsync(timeout.Token);
    }

    private sealed class ControlledBackend(bool moved, bool alwaysLate = false, bool blockRecovery = false, bool lateAfter = false, bool stopAfterEarly = false) : IPreviewBackend
    {
        public ConcurrentQueue<PreviewRequest> BeforeRequests { get; } = new();
        public ConcurrentQueue<PreviewRequest> AfterRequests { get; } = new();
        public TaskCompletionSource FirstBefore { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRecovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EarlyOutsideCoreLock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEarlyTail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<bool?>(false);
        private async Task WaitForReleaseAsync(Task release, CancellationToken cancellationToken)
        {
            try
            {
                await release.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // WaitAsyncの継続が先に登録を破棄しても、実際の取消終了を観測して通知する。
                Cancelled.TrySetResult();
                throw;
            }
        }

        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            // 逆方向先読みの極小負IDはBeforeの画像要求に数えない。
            bool before = request.CorrelationId < 0 && request.CorrelationId > long.MinValue / 2;
            int number = 0;
            if (!before) AfterRequests.Enqueue(request);
            if (before)
            {
                BeforeRequests.Enqueue(request);
                number = BeforeRequests.Count;
                using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
                if (number == 1)
                {
                    FirstBefore.TrySetResult();
                    await WaitForReleaseAsync(Release.Task, cancellationToken);
                }
                if (number == 2 && blockRecovery)
                {
                    RecoveryStarted.TrySetResult();
                    await WaitForReleaseAsync(ReleaseRecovery.Task, cancellationToken);
                }
            }
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F10:1 C420");
            sink.OnHeader(header);
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            double first = !before && lateAfter && AfterRequests.Count == 1 ? request.TargetSeconds + 0.5
                : before && alwaysLate ? request.TargetSeconds + 0.5
                : before && number == 1 ? (moved ? 9.5 : 10.5) : seek;
            // 回復で広げた物理窓も返す。固定16枚だと、正しく前へ広げるほど末尾が足りなくなる偽動作になる。
            int frames = before ? number == 1 ? 16
                : Math.Max(16, (int)Math.Ceiling((request.TargetSeconds - seek + request.PostMarginSeconds) * 10) + 1)
                : request.Continuous ? 31 : Math.Max(31, (int)Math.Ceiling((request.TargetSeconds - seek + request.PostMarginSeconds) * 10) + 1);
            for (int i = 0; i < frames; ++i)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await sink.BeforeReadAsync(cancellationToken);
                var frame = new Y4mFrame(header, 1000 + first + i * 0.1, 0.1, new byte[6]);
                sink.OnFrame(frame);
                if (before && number == 2 && stopAfterEarly && !EarlyOutsideCoreLock.Task.IsCompleted
                    && frame.Xts >= 1000 + request.TargetSeconds + 0.1 - 1e-6)
                {
                    EarlyOutsideCoreLock.TrySetResult();
                    await WaitForReleaseAsync(ReleaseEarlyTail.Task, cancellationToken);
                }
            }
            return new(0, "");
        }
    }
    private sealed class CapabilityScope : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        public CapabilityScope() => Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
        public void Dispose() => Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", _old);
    }
    private static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "FakeEncoder", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }
    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-c0b-{Guid.NewGuid():N}");
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

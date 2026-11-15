using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class BeforeRecoveryTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LandingAfterLatestTargetRequestsOneRecovery(bool moveWhileRunning)
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = TimeProvider.System;
        var backend = new ControlledBackend(moveWhileRunning);
        await using var vm = new MainWindowViewModel(services, clock, backend, backend, null, null, EncodeTime, debounceOverride: TimeSpan.Zero)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-recovery.ts", CurrentSeconds = 10, InputArgsText = "--avhw" };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        Task afterApplied = ObserveAfterApplied(vm);
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        vm.IsShowingBefore = true;
        await backend.FirstBefore.Task.WaitWithDiagnosticsAsync(Safety);
        await afterApplied.WaitWithDiagnosticsAsync(Safety);
        if (moveWhileRunning)
        {
            for (int i = 0; i < 7; ++i)
                vm.StepFrameCommand.Execute(-1);
        }
        backend.Release.TrySetResult();
        // 最初の窓が不足した後、回復要求の登録を待ってから全処理の静止を待つ。
        await backend.RecoveryStarted.Task.WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.False(vm.IsBeforeBusy);
        Assert.NotNull(vm.BeforeImage);
        Assert.Equal(moveWhileRunning ? (byte)93 : (byte)100, FirstPixel(vm.BeforeImage));
        Assert.Equal(2, backend.BeforeRequests.Count);
        Assert.Equal(moveWhileRunning ? 9.3 : 10, backend.BeforeRequests.Last().TargetSeconds, 6);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task 回復画像の変換を保留すると静止待ちも画像反映まで完了しない()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var backend = new ControlledBackend(false);
        var conversionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConversion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<byte[]> ConvertAsync(Y4mFrame frame, CancellationToken token)
        {
            if (frame.Data[0] == 16 && frame.Xts is double timestamp && Math.Abs(timestamp - 1010) < 1e-6)
            {
                conversionStarted.TrySetResult();
                await releaseConversion.Task.WaitAsync(token);
            }
            return await EncodeTime(frame, token);
        }
        await using var vm = new MainWindowViewModel(services, TimeProvider.System, backend, backend, null, null, ConvertAsync,
            debounceOverride: TimeSpan.Zero)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-recovery.ts", CurrentSeconds = 10, InputArgsText = "--avhw", IsShowingBefore = true };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
            await backend.FirstBefore.Task.WaitWithDiagnosticsAsync(Safety);
            backend.Release.TrySetResult();
            await backend.RecoveryStarted.Task.WaitWithDiagnosticsAsync(Safety);
            await conversionStarted.Task.WaitWithDiagnosticsAsync(Safety);
            Task idle = vm.WaitForIdleAsync();
            // 変換完了を試験が保留しているため、静止待ちは論理的に完了できない。
            Assert.False(idle.IsCompleted);
            releaseConversion.TrySetResult();
            await idle.WaitWithDiagnosticsAsync(Safety);
            Assert.NotNull(vm.BeforeImage);
            Assert.Equal((byte)100, FirstPixel(vm.BeforeImage));
            Assert.Equal(2, backend.BeforeRequests.Count);
            Assert.Null(vm.ErrorText);
        }
        finally
        {
            backend.Release.TrySetResult();
            releaseConversion.TrySetResult();
        }
    }

    [AvaloniaFact]
    public async Task AfterWindowRecoveryNeverCarriesItsFilterIntoTheBeforeRequest()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = TimeProvider.System;
        var backend = new ControlledBackend(false, lateAfter: true);
        backend.Release.TrySetResult();
        await using var vm = new MainWindowViewModel(services, clock, backend, backend, null, null, EncodeTime, debounceOverride: TimeSpan.Zero)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-recovery.ts", CurrentSeconds = 10, InputArgsText = "--avhw", IsShowingBefore = true };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.PreviewImage);
        Assert.NotNull(vm.BeforeImage);
        Assert.NotEqual(FirstPixel(vm.PreviewImage), FirstPixel(vm.BeforeImage));
        Assert.NotEmpty(backend.BeforeRequests);
        Assert.All(backend.BeforeRequests, request => Assert.Empty(request.VppArgs));
    }

    [AvaloniaFact]
    public async Task SameUnselectableTargetStopsAtTheWindowRecoveryLimit()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = TimeProvider.System;
        var backend = new ControlledBackend(false, alwaysLate: true);
        await using var vm = await Start(services, clock, backend);
        backend.Release.TrySetResult();
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.False(vm.IsBeforeBusy);
        Assert.Null(vm.BeforeImage);
        Assert.Equal(1 + PreviewWindowRecovery.MaximumRetries, backend.BeforeRequests.Count);
    }

    [AvaloniaFact]
    public async Task AfterUiFollowingEarlyBeforeFrameStartsTheSameThirdRequest()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = TimeProvider.System;
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
            return await EncodeTime(frame, token);
        }
        await using var vm = await Start(services, clock, backend, ConvertFrame);
        try
        {
            backend.Release.TrySetResult();
            await backend.RecoveryStarted.Task.WaitWithDiagnosticsAsync(Safety);
            for (int index = 0; index < 6; ++index)
            {
                vm.StepFrameCommand.Execute(-1);
            }
            // 最後の入力は9.3へ確定済み。After画像のUI適用だけをconverterで保留する。
            vm.BeginSeekDrag();
            vm.EndSeekDrag(9.3);
            await entered.Task.WaitWithDiagnosticsAsync(Safety);
            backend.ReleaseRecovery.TrySetResult();
            await backend.EarlyOutsideCoreLock.Task.WaitWithDiagnosticsAsync(Safety);
            // OnFrameから戻った後なのでCore lockを持たない。実際のAfter適用→QueueBeforeを通す。
            releaseAfter.TrySetResult();
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            Assert.Equal(9.3, vm.CurrentSeconds, 6);
            Assert.NotNull(vm.BeforeImage);
            Assert.Equal((byte)93, FirstPixel(vm.BeforeImage));
            Assert.False(vm.IsBeforeBusy);
            Assert.Null(vm.ErrorText);
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
        var clock = TimeProvider.System;
        var backend = new ControlledBackend(false);
        await using var vm = await Start(services, clock, backend);
        await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        await backend.Cancelled.Task.WaitWithDiagnosticsAsync(Safety);
        Assert.False(vm.IsBeforeBusy);
    }

    [AvaloniaFact]
    public async Task ExplicitRefreshCancelsOldBeforeAndDisplaysTheNewWindow()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = TimeProvider.System;
        var backend = new ControlledBackend(false);
        await using var vm = await Start(services, clock, backend);
        vm.RefreshCommand.Execute(null);
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await backend.Cancelled.Task.WaitWithDiagnosticsAsync(Safety);
        backend.Release.TrySetResult();
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.NotNull(vm.BeforeImage);
        Assert.Equal((byte)100, FirstPixel(vm.BeforeImage));
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBeforeBusy);
    }

    private static async Task<MainWindowViewModel> Start(Services services, TimeProvider clock, ControlledBackend backend,
        Func<Y4mFrame, CancellationToken, Task<byte[]>>? converter = null)
    {
        var vm = new MainWindowViewModel(services, clock, backend, backend, null, null, converter ?? EncodeTime, debounceOverride: TimeSpan.Zero)
        { EncoderPath = FakeEncoderPath(), InputPath = "before-recovery.ts", CurrentSeconds = 10, InputArgsText = "--avhw" };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        Task afterApplied = ObserveAfterApplied(vm);
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        vm.IsShowingBefore = true;
        await backend.FirstBefore.Task.WaitWithDiagnosticsAsync(Safety);
        await afterApplied.WaitWithDiagnosticsAsync(Safety);
        return vm;
    }

    private static Task<byte[]> EncodeTime(Y4mFrame frame, CancellationToken token)
    {
        int role = frame.Data[0] == 80 ? 100 : 0;
        byte value = (byte)(Math.Round(((frame.Xts ?? 1000) - 1000) * 10) + role);
        return Task.FromResult(Enumerable.Repeat(value, frame.Header.Width * frame.Header.Height * 4).ToArray());
    }

    private static byte FirstPixel(Avalonia.Media.Imaging.Bitmap image)
    {
        using var pixels = Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(image).Lock();
        return System.Runtime.InteropServices.Marshal.ReadByte(pixels.Address);
    }

    private static Task ObserveAfterApplied(MainWindowViewModel vm)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler? changed = null;
        changed = (_, args) =>
        {
            if (args.PropertyName != nameof(vm.PreviewImage)) return;
            vm.PropertyChanged -= changed;
            done.TrySetResult();
        };
        vm.PropertyChanged += changed;
        return done.Task;
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
                if (number == 2)
                {
                    RecoveryStarted.TrySetResult();
                    if (blockRecovery) await WaitForReleaseAsync(ReleaseRecovery.Task, cancellationToken);
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
                byte pixel = request.VppArgs.Count == 0 ? (byte)16 : (byte)80;
                var frame = new Y4mFrame(header, 1000 + first + i * 0.1, 0.1,
                    new[] { pixel, pixel, pixel, pixel, (byte)128, (byte)128 });
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

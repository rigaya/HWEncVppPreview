using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.App.Tests;

public sealed class NavigationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public async Task HeldFrameNavigationShowsIntermediateFramesBeforeKeysAreReleased()
    {
        var clock = new FakeTimeProvider();
        await using var backend = new TimedBackend(clock);
        await using var vm = new MainWindowViewModel(new Services(), clock, backend, backend,
            null, new FrameCache(0), ConvertImmediatelyAsync)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "navigation input.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await backend.NextStartAsync();
        clock.Advance(TimeSpan.FromMilliseconds(400));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.NotNull(vm.PreviewImage);
        string previous = ImageHash(vm);
        int imageChanges = 0;
        var displayedLevels = new List<byte>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(vm.PreviewImage) || vm.PreviewImage == null) return;
            string current = ImageHash(vm);
            if (current != previous)
            {
                ++imageChanges;
                using var locked = Assert.IsType<WriteableBitmap>(vm.PreviewImage).Lock();
                displayedLevels.Add(Marshal.ReadByte(locked.Address));
            }
            previous = current;
        };
        int initialStarts = backend.Starts;
        int initialCancels = backend.Cancels;
        var targets = new List<double>();
        for (int i = 0; i < 91; ++i)
        {
            vm.StepFrameCommand.Execute(1);
            targets.Add(vm.CurrentSeconds);
            await vm.WaitForPreparationAsync().WaitAsync(Safety);
            clock.Advance(TimeSpan.FromMilliseconds(33));
            await UiBarrierAsync();
        }
        output.WriteLine($"押下中3003ms: 画像変化={imageChanges}, backend開始={backend.Starts - initialStarts}, cancel={backend.Cancels - initialCancels}, 目標={vm.CurrentSeconds:F6}, 単調={targets.Zip(targets.Skip(1)).All(pair => pair.First <= pair.Second)}");
        Assert.True(imageChanges >= 2, $"押下中の画像変化は{imageChanges}回、backend開始は{backend.Starts - initialStarts}回。期待は2回以上。");
        Assert.True(targets.Zip(targets.Skip(1)).All(pair => pair.First <= pair.Second));
        Assert.True(targets[^1] > targets[0]);
        Assert.True(displayedLevels.Zip(displayedLevels.Skip(1)).All(pair => pair.First <= pair.Second));
        // 区間外の隣を取り直す間の反復は加算せず、最後に確定した最新目標へ追い付く。
        await DrainNavigationAsync(vm, clock);
        Assert.Equal(ExpectedHash((int)Math.Round(vm.CurrentSeconds * 30)), ImageHash(vm));
    }

    [AvaloniaFact]
    public async Task FrameStepTargetDoesNotRegressWhileANeighborRefetchIsPending()
    {
        var clock = new FakeTimeProvider();
        await using var backend = new TimedBackend(clock);
        await using var vm = new MainWindowViewModel(new Services(), clock, preferred: backend, fallback: backend)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "navigation input.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };
        await InitializeAsync(vm, clock, backend);
        var targets = new List<double>();
        for (int i = 0; i < 10; ++i)
        {
            vm.StepFrameCommand.Execute(1);
            targets.Add(vm.CurrentSeconds);
            await vm.WaitForPreparationAsync().WaitAsync(Safety);
            await UiBarrierAsync();
        }
        Assert.True(targets.Zip(targets.Skip(1)).All(pair => pair.First <= pair.Second));
        Assert.Equal(1.0 / 30, vm.CurrentSeconds, 7);
        await DrainNavigationAsync(vm, clock);
        Assert.Equal(2.0 / 30, vm.CurrentSeconds, 7);
    }

    [AvaloniaFact]
    public async Task SeekDragPublishesAFrameBeforeTheDragEnds()
    {
        var clock = new FakeTimeProvider();
        await using var backend = new TimedBackend(clock);
        await using var vm = new MainWindowViewModel(new Services(), clock, backend, backend,
            null, new FrameCache(0), ConvertImmediatelyAsync)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "navigation input.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };
        await InitializeAsync(vm, clock, backend);
        string initial = ImageHash(vm);
        vm.BeginSeekDrag();
        vm.CurrentSeconds = 1;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await backend.NextStartAsync();
        clock.Advance(TimeSpan.FromMilliseconds(400));
        await WaitUntilAsync(() => ImageHash(vm) != initial);
        Assert.Equal(1, vm.CurrentSeconds);
        Assert.NotEqual(initial, ImageHash(vm));
        vm.EndSeekDrag(1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        async Task WaitAsync()
        {
            while (!condition()) await UiBarrierAsync();
        }
        await WaitAsync().WaitAsync(Safety);
    }

    [AvaloniaFact]
    public async Task CachedFrameNavigationKeepsRenderingWhileConversionsAreSlowerThanKeyRepeat()
    {
        var clock = new FakeTimeProvider();
        await using var backend = new TimedBackend(clock) { FrameCount = 100 };
        var converter = new TimedConverter(clock);
        await using var vm = new MainWindowViewModel(new Services(), clock, backend, backend,
            null, new FrameCache(), converter.ConvertAsync)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "navigation input.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };
        await InitializeAsync(vm, clock, backend);
        string initial = ImageHash(vm);
        string previous = initial;
        var displayedLevels = new List<byte>();
        int imageChanges = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(vm.PreviewImage) || vm.PreviewImage == null) return;
            string current = ImageHash(vm);
            if (current != previous)
            {
                ++imageChanges;
                var bitmap = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
                using var locked = bitmap.Lock();
                displayedLevels.Add(Marshal.ReadByte(locked.Address));
            }
            previous = current;
        };
        converter.DelayEnabled = true;
        int starts = backend.Starts;
        var targets = new List<double>();
        for (int i = 0; i < 91; ++i)
        {
            // 取得済みの先頭10枚だけを往復し、前後画像とも全移動をキャッシュ内に保つ。
            vm.StepFrameCommand.Execute((i / 10) % 2 == 0 ? 1 : -1);
            targets.Add(vm.CurrentSeconds);
            await vm.WaitForPreparationAsync().WaitAsync(Safety);
            clock.Advance(TimeSpan.FromMilliseconds(33));
            await UiBarrierAsync();
        }
        int heldImageChanges = imageChanges;
        output.WriteLine($"キャッシュ内3003ms: 画像変化={heldImageChanges}, backend追加={backend.Starts - starts}, 変換最大並列={converter.PeakConcurrency}, 目標={vm.CurrentSeconds:F6}");
        // 押下後の残りも時計だけで処理し、失敗時のDispose待ちへ未完了変換を残さない。
        converter.DelayEnabled = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(starts, backend.Starts);
        Assert.All(targets, target => Assert.InRange(target, 0, 10.0 / 30));
        Assert.Equal(9.0 / 30, vm.CurrentSeconds, 7);
        Assert.True(heldImageChanges >= 2, $"キャッシュ内の押下中画像変化は{heldImageChanges}回。400ms変換でも2回以上必要。");
        Assert.Equal(1, converter.PeakConcurrency);
        // Before込みでもキー操作数を下回り、全操作分をFIFOで変換していないことを確認する。
        Assert.True(converter.Calls < targets.Count, $"{targets.Count}操作に対して変換が{converter.Calls}回走った。");
        Assert.Equal(ExpectedHash(9), ImageHash(vm));
        Assert.NotEmpty(displayedLevels);
    }

    [AvaloniaFact]
    public async Task EditingRejectsTheOldFilteredImageEvenWhenItsConversionFinishesLater()
    {
        var clock = new FakeTimeProvider();
        await using var backend = new TimedBackend(clock) { FrameCount = 100 };
        var converter = new TimedConverter(clock);
        await using var vm = new MainWindowViewModel(new Services(), clock, backend, backend,
            null, new FrameCache(), converter.ConvertAsync)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "navigation input.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };
        await InitializeAsync(vm, clock, backend);
        string initial = ImageHash(vm);
        var published = new List<string>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.PreviewImage) && vm.PreviewImage != null) published.Add(ImageHash(vm));
        };
        converter.DelayEnabled = true;
        vm.StepFrameCommand.Execute(1);
        await UiBarrierAsync();
        vm.FilterLines.Add(new() { Text = "--vpp-unsharp" });
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await backend.NextStartAsync();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await UiBarrierAsync();
        await UiBarrierAsync();
        Assert.Equal(initial, ImageHash(vm));
        Assert.DoesNotContain(ExpectedHash(1), published);
        converter.DelayEnabled = false;
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await WaitUntilAsync(() => ImageHash(vm) == ExpectedHash(81));
        Assert.DoesNotContain(ExpectedHash(1), published);
        Assert.Equal(1.0 / 30, vm.CurrentSeconds, 7);
        Assert.Equal(1, converter.PeakConcurrency);
    }

    [AvaloniaFact]
    public async Task NavigationWithinAnUnfilteredIntervalReusesBeforeWithoutCancellation()
    {
        var clock = new FakeTimeProvider();
        await using var backend = new TimedBackend(clock);
        await using var vm = new MainWindowViewModel(new Services(), clock, backend, backend,
            null, new FrameCache(0), ConvertImmediatelyAsync)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "navigation input.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };
        await InitializeAsync(vm, clock, backend);
        string original = ImageHash(vm);
        int cancels = backend.Cancels;
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await backend.NextStartAsync();
        Assert.Equal(cancels, backend.Cancels);
        // LRU容量0でも現在の区間から前後の画像を選び、元画像を再起動しない。
        await WaitUntilAsync(() => ImageHash(vm) == ExpectedHash(1));
        Assert.NotEqual(original, ImageHash(vm));
        Assert.Equal(ExpectedHash(1), ImageHash(vm));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(1.0 / 30, vm.CurrentSeconds, 7);
        Assert.Equal(cancels, backend.Cancels);
        Assert.Equal(2, backend.Starts);
        Assert.Equal(ImageHash(vm), ImageHash(vm.BeforeImage));
    }

    [AvaloniaFact]
    public async Task AConversionFinishingAfterFailureDoesNotReplaceTheErrorStatus()
    {
        var clock = new FakeTimeProvider();
        await using var backend = new TimedBackend(clock);
        var converter = new TimedConverter(clock);
        await using var vm = new MainWindowViewModel(new Services(), clock, backend, backend,
            null, new FrameCache(0), converter.ConvertAsync)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "navigation input.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };
        await InitializeAsync(vm, clock, backend);
        converter.DelayEnabled = true;
        backend.FailAfterFrame = true;
        vm.CurrentSeconds = 1;
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await backend.NextStartAsync();
        clock.Advance(TimeSpan.FromMilliseconds(400));
        await WaitUntilAsync(() => vm.ErrorText != null);
        string status = vm.StatusText;
        string? error = vm.ErrorText;
        string log = vm.LogText;
        clock.Advance(TimeSpan.FromMilliseconds(400));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal("エラー", status);
        Assert.Equal(status, vm.StatusText);
        Assert.Equal(error, vm.ErrorText);
        Assert.Equal(log, vm.LogText);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task NavigationDuringInitialProbeReceivesTheProbeFailureAndClearsBusy()
    {
        string directory = Path.Combine(Path.GetTempPath(), "viewer-navigation-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string ready = Path.Combine(directory, "ready"), release = Path.Combine(directory, "release");
        try
        {
            var clock = new FakeTimeProvider();
            await using var backend = new TimedBackend(clock);
            await using var probe = new InputProbe(clock, configureStartInfo: start =>
            {
                // 先頭フレームを出さず、ゲート開放後のrc=23で入力検査だけを失敗させる。
                int frames = start.ArgumentList.IndexOf("--frames");
                start.ArgumentList[frames + 1] = "0";
                start.ArgumentList.Add("--fake-rc"); start.ArgumentList.Add("23");
                start.ArgumentList.Add("--fake-duration-at-exit");
                start.ArgumentList.Add("--fake-duration-exit-gate"); start.ArgumentList.Add(release);
                start.Environment["FAKE_ENCODER_DURATION_EXIT_READY_FILE"] = ready;
            });
            await using var vm = new MainWindowViewModel(new Services(), clock, backend, backend,
                probe, new FrameCache(0), ConvertImmediatelyAsync)
            { EncoderPath = FakeEncoderPath(), InputPath = "navigation probe input.ts" };
            Task initialize = vm.InitializeAsync(false);
            await WaitUntilAsync(() => File.Exists(ready));
            vm.StepFrameCommand.Execute(1);
            await File.WriteAllTextAsync(release, "");
            await initialize.WaitAsync(Safety);
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            Assert.False(vm.IsBusy);
            Assert.Equal("エラー", vm.StatusText);
            Assert.NotNull(vm.ErrorText);
            Assert.Contains("23", vm.LogText);
            // 未取得の隣の時刻は推測せず、入力検査の失敗を最新意図へ返す。
            Assert.Equal(0, vm.CurrentSeconds, 7);
            Assert.Equal(0, backend.Starts);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static Task<byte[]> ConvertImmediatelyAsync(Y4mFrame frame, CancellationToken token) =>
        Task.FromResult(YuvToBgra.Convert(frame));

    private static string ExpectedHash(int index)
    {
        var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
        byte value = (byte)(16 + index % 200);
        var frame = new Y4mFrame(header, 0, 1.0 / 30, new byte[] { value, value, value, value, 128, 128 });
        return Convert.ToHexString(SHA256.HashData(YuvToBgra.Convert(frame)));
    }

    private static async Task DrainNavigationAsync(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        Task idle = vm.WaitForIdleAsync();
        async Task DrainAsync()
        {
            while (!idle.IsCompleted)
            {
                clock.Advance(TimeSpan.FromMilliseconds(400));
                await UiBarrierAsync();
                await UiBarrierAsync();
            }
            await idle;
        }
        await DrainAsync().WaitAsync(Safety);
    }

    private static async Task DrainToImageAsync(MainWindowViewModel vm, FakeTimeProvider clock, TimedBackend backend, string expected)
    {
        for (int i = 0; i < 8 && ImageHash(vm) != expected; ++i)
        {
            int starts = backend.Starts;
            clock.Advance(TimeSpan.FromMilliseconds(400));
            await UiBarrierAsync();
            await UiBarrierAsync();
            if (ImageHash(vm) != expected) await WaitUntilAsync(() => backend.Starts > starts || ImageHash(vm) == expected);
        }
        Assert.Equal(expected, ImageHash(vm));
    }

    private static async Task InitializeAsync(MainWindowViewModel vm, FakeTimeProvider clock, TimedBackend backend)
    {
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await backend.NextStartAsync();
        clock.Advance(TimeSpan.FromMilliseconds(400));
        // フィルタなしの初期区間は、LRUの容量によらず前後表示で共有する。
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.NotNull(vm.PreviewImage);
    }

    private static async Task UiBarrierAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => done.TrySetResult());
        await done.Task.WaitAsync(Safety);
    }

    private static string ImageHash(MainWindowViewModel vm) => ImageHash(vm.PreviewImage);
    private static string ImageHash(Bitmap? image)
    {
        var bitmap = Assert.IsType<WriteableBitmap>(image);
        using var locked = bitmap.Lock();
        byte[] data = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        for (int y = 0; y < bitmap.PixelSize.Height; ++y)
            Marshal.Copy(locked.Address + y * locked.RowBytes, data, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
        return Convert.ToHexString(SHA256.HashData(data));
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

    private sealed class TimedConverter(FakeTimeProvider clock)
    {
        private int _active, _peak, _calls;
        public bool DelayEnabled { get; set; }
        public int PeakConcurrency => Volatile.Read(ref _peak);
        public int Calls => Volatile.Read(ref _calls);
        public Task<byte[]> ConvertAsync(Y4mFrame frame, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            byte[] pixels = YuvToBgra.Convert(frame);
            if (!DelayEnabled) return Task.FromResult(pixels);
            int active = Interlocked.Increment(ref _active);
            _peak = Math.Max(_peak, active);
            // 非同期継続を強制せず、時計のtimer発火からVMのPostまで同期して進める。
            var result = new TaskCompletionSource<byte[]>();
            ITimer? timer = null;
            var registration = token.Register(() => result.TrySetCanceled(token));
            timer = clock.CreateTimer(_ => result.TrySetResult(pixels), null,
                TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
            return FinishAsync();
            async Task<byte[]> FinishAsync()
            {
                try { return await result.Task.ConfigureAwait(false); }
                finally
                {
                    Interlocked.Decrement(ref _active);
                    timer?.Dispose();
                    registration.Dispose();
                }
            }
        }
    }

    private sealed class TimedBackend(TimeProvider clock) : IPreviewBackend
    {
        private readonly Channel<bool> _starts = Channel.CreateUnbounded<bool>();
        private readonly CancellationTokenSource _lifetime = new();
        private int _started, _cancelled;
        public int FrameCount { get; init; } = 2;
        public bool FailAfterFrame { get; set; }
        public int Starts => Volatile.Read(ref _started);
        public int Cancels => Volatile.Read(ref _cancelled);
        public Task NextStartAsync() => _starts.Reader.ReadAsync().AsTask().WaitAsync(Safety);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var result = new TaskCompletionSource<PreviewRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            ITimer? timer = null;
            var registration = linked.Token.Register(() =>
            {
                Interlocked.Increment(ref _cancelled);
                timer?.Dispose();
                result.TrySetResult(new(-12, "navigation cancelled", true));
            });
            // 偽時計を進めた呼出元でフレームを通知し、ThreadPoolの実行順に依存しない。
            timer = clock.CreateTimer(_ =>
            {
                if (result.Task.IsCompleted) return;
                var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1, SarNum = 1, SarDen = 1 };
                double relative = CommandBuilder.Plan(request).ActualPreMargin;
                int index = (int)Math.Round(request.TargetSeconds * 30) + (request.VppArgs.Count > 0 ? 80 : 0);
                sink.OnOutput();
                sink.OnHeader(header);
                for (int i = 0; i < FrameCount; ++i) sink.OnFrame(Frame(header, relative + i / 30.0, index + i));
                result.TrySetResult(FailAfterFrame ? new(-29, "early frame then failure") : new(0, "navigation completed"));
            }, null, TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
            Interlocked.Increment(ref _started);
            _starts.Writer.TryWrite(true);
            return FinishAsync();
            async Task<PreviewRunResult> FinishAsync()
            {
                try { return await result.Task; }
                finally { timer?.Dispose(); registration.Dispose(); linked.Dispose(); }
            }
        }
        private static Y4mFrame Frame(Y4mHeader header, double time, int index) =>
            new(header, time, 1.0 / 30, new byte[] { (byte)(16 + index % 200), (byte)(16 + index % 200), (byte)(16 + index % 200), (byte)(16 + index % 200), 128, 128 });
        public ValueTask AbortAsync() { _lifetime.Cancel(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { _lifetime.Cancel(); return ValueTask.CompletedTask; }
    }
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class NeighborNavigationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [AvaloniaTheory]
    [InlineData(1, 0.0, 0.46, 0.55, 1048576)]
    [InlineData(-1, 2.6, 0.62, 0.55, 1048576)]
    [InlineData(1, 0.0, 0.46, 0.55, 0)]
    [InlineData(-1, 2.6, 0.62, 0.55, 0)]
    public async Task VfrCacheEdgeRefetchMovesExactlyOneNeighbor(int direction, double initial, double edge, double expected, long capacity)
    {
        var clock = new FakeTimeProvider();
        var backend = new NeighborBackend();
        await using var vm = Create(clock, backend, initial, capacity);
        await InitializeAsync(vm, clock);
        await MoveToAsync(vm, edge, direction);
        int starts = backend.AfterStarts;
        string before = ImageHash(vm);
        vm.StepFrameCommand.Execute(direction);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        output.WriteLine($"VFR方向={direction}: 起点={edge:F6}, 目標={vm.CurrentSeconds:F6}, 追加起動={backend.AfterStarts - starts}, 画像変化={before != ImageHash(vm)}");
        Assert.Equal(starts + 1, backend.AfterStarts);
        Assert.Equal(expected, vm.CurrentSeconds, 7);
        Assert.NotEqual(before, ImageHash(vm));
        Assert.Equal(ExpectedHash(expected), ImageHash(vm));
    }

    [AvaloniaTheory]
    [InlineData(-1, 0.0, "先頭")]
    [InlineData(1, 4.5, "終端")]
    public async Task MissingNeighborKeepsTheImageAndReportsTheBoundary(int direction, double origin, string status)
    {
        var clock = new FakeTimeProvider();
        var backend = new NeighborBackend();
        await using var vm = Create(clock, backend, origin);
        await InitializeAsync(vm, clock);
        string before = ImageHash(vm);
        int starts = backend.Starts;
        vm.StepFrameCommand.Execute(direction);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(origin, vm.CurrentSeconds, 7);
        Assert.Equal(before, ImageHash(vm));
        Assert.Contains(status, vm.StatusText);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);
        Assert.Equal(starts, backend.Starts);
    }

    [AvaloniaFact]
    public async Task RepeatedStepsDuringRefetchDoNotAccumulateAndTheNextStepUsesTheNewInterval()
    {
        var clock = new FakeTimeProvider();
        var backend = new NeighborBackend { HoldTarget = 0.46 };
        await using var vm = Create(clock, backend, 0);
        await InitializeAsync(vm, clock);
        await MoveToAsync(vm, 0.46, 1);
        int starts = backend.Starts;
        vm.StepFrameCommand.Execute(1);
        await backend.RefetchStarted.Task.WaitAsync(Safety);
        for (int i = 0; i < 20; ++i) vm.StepFrameCommand.Execute(1);
        Assert.Equal(0.46, vm.CurrentSeconds, 7);
        Assert.Equal(starts + 1, backend.Starts);
        backend.ReleaseRefetch();
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(0.55, vm.CurrentSeconds, 7);
        Assert.Equal(ExpectedHash(0.55), ImageHash(vm));
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(0.62, vm.CurrentSeconds, 7);
        Assert.Equal(ExpectedHash(0.62), ImageHash(vm));
        Assert.Equal(starts + 1, backend.Starts);
    }

    [AvaloniaFact]
    public async Task FineVfrIntervalsMoveToTheActualNeighborInsteadOfSkippingWithinTheTolerance()
    {
        var clock = new FakeTimeProvider();
        var backend = new NeighborBackend { FineIntervals = true };
        await using var vm = Create(clock, backend, 0);
        await InitializeAsync(vm, clock);
        await MoveToAsync(vm, 0.5, 1);
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(0.505, vm.CurrentSeconds, 7);
        Assert.Equal(ExpectedHash(0.505), ImageHash(vm));
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(0.51, vm.CurrentSeconds, 7);
        Assert.Equal(ExpectedHash(0.51), ImageHash(vm));
        Assert.Equal(2, backend.Starts);
    }

    [AvaloniaFact]
    public async Task ReturningToAnEarlierIntervalReusesItsPreviouslyRetrievedNeighborWindow()
    {
        var clock = new FakeTimeProvider();
        var backend = new NeighborBackend();
        await using var vm = Create(clock, backend, 0);
        await InitializeAsync(vm, clock);
        await MoveToAsync(vm, 0.46, 1);
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(0.55, vm.CurrentSeconds, 7);
        Assert.Equal(2, backend.Starts);
        int starts = backend.Starts;
        // 元の中心区間へ戻ってから同じ端を再度越える。どちらの区間も取得済み。
        vm.CurrentSeconds = 0;
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(ExpectedHash(0), ImageHash(vm));
        await MoveToAsync(vm, 0.46, 1);
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        output.WriteLine($"取得済み区間の往復: 追加起動={backend.Starts - starts}, 目標={vm.CurrentSeconds:F6}");
        Assert.Equal(starts, backend.Starts);
        Assert.Equal(0.55, vm.CurrentSeconds, 7);
        Assert.Equal(ExpectedHash(0.55), ImageHash(vm));
    }

    private static MainWindowViewModel Create(FakeTimeProvider clock, NeighborBackend backend, double target, long capacity = 1048576) =>
        new(new Services(), clock, backend, backend, cache: new FrameCache(capacity))
        {
            EncoderPath = FakeEncoderPath(), InputPath = "vfr.ts", CurrentSeconds = target,
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };
    private static async Task InitializeAsync(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.NotNull(vm.PreviewImage);
    }
    private static async Task MoveToAsync(MainWindowViewModel vm, double target, int direction)
    {
        for (int i = 0; i < 64 && Math.Abs(vm.CurrentSeconds - target) > 1e-8; ++i)
        {
            double previous = vm.CurrentSeconds;
            vm.StepFrameCommand.Execute(direction);
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            Assert.True((vm.CurrentSeconds - previous) * direction > 0);
        }
        Assert.Equal(target, vm.CurrentSeconds, 7);
    }

    private static readonly double[] Times = [0, 0.05, 0.10, 0.16, 0.24, 0.30, 0.41, 0.46, 0.55, 0.62, 0.71, 0.84, 0.90, 1.0, 1.08, 1.2, 1.4, 1.6, 1.8, 2.0, 2.1, 2.3, 2.6, 2.8, 3.0, 3.4, 4.0, 4.5];
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 60, FpsDen = 1, SarNum = 1, SarDen = 1 };
    private static Y4mFrame Frame(double absolute, double seek)
    {
        byte level = (byte)(32 + (int)Math.Round(absolute * 400) % 192);
        return new(Header, absolute - seek, null, [level, level, level, level, 128, 128]);
    }
    private static string ExpectedHash(double absolute) => Convert.ToHexString(SHA256.HashData(YuvToBgra.Convert(Frame(absolute, 0))));
    private static string ImageHash(MainWindowViewModel vm)
    {
        var bitmap = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
        using var pixels = bitmap.Lock();
        byte[] data = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        for (int y = 0; y < bitmap.PixelSize.Height; ++y)
            Marshal.Copy(pixels.Address + y * pixels.RowBytes, data, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
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
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "viewer-neighbor-" + Guid.NewGuid().ToString("N"), "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
    private sealed class NeighborBackend : IPreviewBackend
    {
        private int _starts, _afterStarts;
        public int AfterStarts => Volatile.Read(ref _afterStarts);
        public bool FineIntervals { get; init; }
        public double? HoldTarget { get; init; }
        public TaskCompletionSource RefetchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseRefetch() => _release.TrySetResult();
        public int Starts => Volatile.Read(ref _starts);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _starts);
            if (request.CorrelationId > 0) Interlocked.Increment(ref _afterStarts);
            if (HoldTarget is double target && Math.Abs(request.TargetSeconds - target) <= 1e-8)
            {
                RefetchStarted.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            var plan = CommandBuilder.Plan(request);
            double seek = plan.SeekSeconds ?? 0;
            double end = seek + (plan.FrameCount - 1) / request.Fps;
            // 同じseekと入力枚数には、呼出し順序によらず同じグローバル時系列を返す。
            IEnumerable<double> series = FineIntervals ? Times.Concat([0.5, 0.505, 0.51]).Order() : Times;
            sink.OnOutput();
            sink.OnHeader(Header);
            foreach (double time in series.Where(time => time >= seek - 1e-8 && time <= end + 1e-8)) sink.OnFrame(Frame(time, seek));
            return new PreviewRunResult(0, "vfr completed");
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

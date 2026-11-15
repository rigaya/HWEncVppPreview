using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class NoTimestampRollingViewModelTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static readonly Y4mHeader Header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420");

    [AvaloniaTheory]
    [InlineData(20)]
    [InlineData(0)]
    public async Task FiniteUntimedFramesKeepGlobalNumbersAndBeforeImage(int cacheFrames)
    {
        using var capabilities = new LegacyCapabilities();
        using var services = new Services();
        var backend = new Backend();
        var cache = new FrameCache(Header.FrameBytes * (long)cacheFrames);
        await using var vm = Create(services, backend, cache);
        await Start(vm);
        foreach (int direction in new[] { 1, 1, -1, -1 })
        {
            vm.StepFrameCommand.Execute(direction);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        }
        Assert.Equal(10, vm.CurrentSeconds, 8);
        AssertImages(vm, 300);
    }

    [AvaloniaFact]
    public async Task MissingNeighborRefetchUsesCompletedUntimedWindowOffset()
    {
        using var capabilities = new LegacyCapabilities();
        using var services = new Services();
        var backend = new Backend { HoldSecond = true };
        await using var vm = Create(services, backend, new FrameCache(Header.FrameBytes * 20L));
        try
        {
            await Start(vm);
            vm.StepFrameCommand.Execute(1);
            await backend.SecondStarted.Task.WaitWithDiagnosticsAsync(Safety);
            // 最初の3枚窓の末尾から、次の隣だけを有限の完了列で選ばせる。
            vm.StepFrameCommand.Execute(1);
            await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
            backend.ReleaseSecond.TrySetResult();
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            Assert.InRange(vm.CurrentSeconds, 10 + 2.0 / 30 - 0.001, 10 + 2.0 / 30 + 0.001);
            AssertImages(vm, 302);
        }
        finally { backend.ReleaseSecond.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionSecondsInvertsUntimedGlobalTime(bool trusted)
    {
        var frames = Enumerable.Range(0, 9).Select(i => new Y4mFrame(new Y4mHeader { Width = Header.Width, Height = Header.Height, FpsNum = 24, FpsDen = Header.FpsDen, Chroma = Header.Chroma, BitDepth = Header.BitDepth }, null, null, new byte[Header.FrameBytes])).ToArray();
        var original = new PreviewSegmentSnapshot(0, new() { Fps = 30 }, new(1000, 8, trusted), frames, 8, 10, false, false);
        var retained = new PreviewFrameReference(original, 6).RetainFrameOnly().Segment with { Frames = frames.Skip(6).ToArray() };
        double input = original.Clock.ToInputSeconds(frames[7], 7, 30);
        double local = retained.Clock.ToSelectionSeconds(input, retained.Frames, 30);
        Assert.Equal(1.0 / 24, local, 10);
        Assert.Equal(1, FrameSelector.Select(retained.Frames, local, 30));
        Assert.Equal(input, retained.Clock.ToInputSeconds(retained.Frames[1], 1, 30), 10);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SelectionSecondsKeepsRelativeAbsoluteAndMixedTimestamps(bool absolute, bool mixed)
    {
        var frames = Enumerable.Range(0, 9).Select(i => new Y4mFrame(Header,
            mixed && i != 7 ? null : (absolute ? 1008 : 0) + i / 30.0, null, new byte[Header.FrameBytes])).ToArray();
        var original = new PreviewSegmentSnapshot(0, new() { Fps = 30 }, new(1000, 8, absolute), frames, 8, 10, false, false);
        var retained = new PreviewFrameReference(original, 6).RetainFrameOnly().Segment with { Frames = frames.Skip(6).ToArray() };
        double input = original.Clock.ToInputSeconds(frames[7], 7, 30);
        double local = retained.Clock.ToSelectionSeconds(input, retained.Frames, 30);
        Assert.Equal(retained.Clock.ToFrameSeconds(input), local, 10);
        Assert.Equal(1, FrameSelector.Select(retained.Frames, local, 30));
    }

    private static MainWindowViewModel Create(Services services, Backend backend, FrameCache cache)
        => new(services, TimeProvider.System, backend, backend, null, cache,
            (frame, token) => Task.FromResult(Enumerable.Repeat(frame.Data[0], frame.Header.Width * frame.Header.Height * 4).ToArray()))
        { EncoderPath = FakeEncoderPath(), InputPath = "no-xts-rolling.ts", InputArgsText = "--avhw", CurrentSeconds = 10,
            CompareMode = PreviewCompareMode.SideBySide };
    private static async Task Start(MainWindowViewModel vm)
    {
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Null(vm.ErrorText);
    }
    private static void AssertImages(MainWindowViewModel vm, int tick)
    {
        Assert.Null(vm.ErrorText);
        var after = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
        var before = Assert.IsType<WriteableBitmap>(vm.BeforeImage);
        using var afterPixels = after.Lock();
        using var beforePixels = before.Lock();
        Assert.Equal((byte)(tick & 255), Marshal.ReadByte(afterPixels.Address));
        Assert.Equal(Marshal.ReadByte(afterPixels.Address), Marshal.ReadByte(beforePixels.Address));
    }

    private sealed class Backend : IPreviewBackend
    {
        public bool HoldSecond { get; init; }
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        private int _calls;
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Enqueue(request);
            int call = Interlocked.Increment(ref _calls);
            if (call == 2)
            {
                SecondStarted.TrySetResult();
                if (HoldSecond) await ReleaseSecond.Task.WaitAsync(token);
            }
            var plan = CommandBuilder.Plan(request);
            int first = (int)Math.Round((plan.SeekSeconds ?? 0) * 30);
            sink.OnHeader(Header);
            for (int i = 0; i < plan.FrameCount; i++)
            {
                token.ThrowIfCancellationRequested();
                var data = Enumerable.Repeat((byte)128, Header.FrameBytes).ToArray();
                data[0] = (byte)((first + i) & 255);
                sink.OnOutput(); sink.OnFrame(new(Header, null, null, data));
            }
            return new(0, "");
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class LegacyCapabilities : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        public LegacyCapabilities() => Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", null);
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
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-no-xts-{Guid.NewGuid():N}");
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

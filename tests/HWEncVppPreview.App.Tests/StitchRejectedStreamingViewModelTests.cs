using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class StitchRejectedStreamingViewModelTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private const byte OldPixels = 40, RejectedPixels = 90, StreamPixels = 140, ReversePixels = 190;
    private static readonly Y4mHeader Header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420");

    [AvaloniaFact]
    public async Task PixelRejectKeepsForwardContinuousAndReverseFiniteWithoutMixingRuns()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var cache = new FrameCache();
        var request = new PreviewRequest
        {
            EncoderPath = FakeEncoderPath(), InputFile = "reject-stream.ts", Fps = 30, TargetSeconds = 10,
            Capabilities = new(true, true), TimestampOriginSeconds = 1000,
        };
        var old = Assert.IsType<PreviewSegmentSnapshot>(cache.StoreSegment(request, Frames(OldPixels), new(1000, 0, true), 9, 12));
        var backend = new ControlledBackend();
        var applied = new ConcurrentQueue<byte[]>();
        await using var vm = new MainWindowViewModel(services, clock, backend, backend, null, cache)
        { EncoderPath = FakeEncoderPath(), InputPath = request.InputFile, CurrentSeconds = 10, InputArgsText = "--avhw" };
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.PreviewImage) && vm.PreviewImage is WriteableBitmap bitmap)
                applied.Enqueue(Pixels(bitmap));
        };
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Empty(backend.Requests);
        Assert.NotNull(vm.PreviewImage);
        Assert.True(cache.CanStitch(request));

        // 初回source設定は診断をresetするため、VM初期化後に実画素不一致を発生させる。
        var rejected = Assert.IsType<PreviewSegmentSnapshot>(cache.StoreSegment(request,
            Frames(RejectedPixels), new(1000, 0, true), 9, 12));
        Assert.False(cache.CanStitch(request));
        Assert.False(rejected.CanStitch);
        Assert.True(rejected.Clock.TrustedAbsolute);
        Assert.Null(cache.GetSegment(old.SegmentId));
        while (applied.TryDequeue(out _)) { }

        vm.CurrentSeconds = 13;
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await backend.ForwardPaused.Task.WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        var forward = Assert.Single(backend.Requests, value => value.CorrelationId >= 0);
        Assert.True(forward.Continuous);
        Assert.Equal(13, forward.TargetSeconds);
        Assert.NotEmpty(applied);
        Assert.All(applied, image => Assert.Equal(YuvToBgra.Convert(Frame(13, StreamPixels)), image));
        Assert.False(cache.CanStitch(request));
        Assert.Null(cache.GetSegment(rejected.SegmentId));
        var navigation = cache.GetNavigationFrames(request with { TargetSeconds = 13 });
        Assert.NotEmpty(navigation);
        long streamId = navigation[0].SegmentId;
        Assert.All(navigation, reference =>
        {
            Assert.Equal(streamId, reference.SegmentId);
            Assert.False(reference.Segment.CanStitch);
            Assert.True(reference.Clock.TrustedAbsolute);
            Assert.Contains(reference.Frame, backend.StreamFrames);
            Assert.Equal(StreamPixels, reference.Frame.Data[0]);
        });
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);

        while (applied.TryDequeue(out _)) { }
        // 保持域の左端までは実フレームを戻り、最後の1操作で逆方向の有限再取得へ進む。
        for (int step = 0; step < 40; ++step)
        {
            while (applied.TryDequeue(out _)) { }
            vm.StepFrameCommand.Execute(-1);
            await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            if (backend.Requests.Any(value => value.CorrelationId >= 0 && !value.Continuous)) break;
        }
        var reverse = Assert.Single(backend.Requests, value => value.CorrelationId >= 0 && !value.Continuous);
        Assert.InRange(reverse.TargetSeconds, 12 - 1.0 / 30, 12);
        await backend.ForwardCancelled.Task.WaitWithDiagnosticsAsync(Safety);
        Assert.NotEmpty(applied);
        Assert.All(applied, image => Assert.Equal(YuvToBgra.Convert(Frame(reverse.TargetSeconds, ReversePixels)), image));
        Assert.False(cache.CanStitch(request));
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);
    }

    private static Y4mFrame Frame(double time, byte value)
        => new(Header, 1000 + time, 1.0 / 30, Enumerable.Repeat(value, Header.FrameBytes).ToArray());
    private static Y4mFrame[] Frames(byte value)
        => Enumerable.Range(0, 91).Select(index => Frame(9 + index / 30.0, value)).ToArray();
    private static byte[] Pixels(WriteableBitmap bitmap)
    {
        byte[] data = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        using var locked = bitmap.Lock();
        for (int row = 0; row < bitmap.PixelSize.Height; ++row)
            Marshal.Copy(IntPtr.Add(locked.Address, row * locked.RowBytes), data,
                row * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
        return data;
    }

    private sealed class ControlledBackend : IPreviewBackend
    {
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public ConcurrentQueue<Y4mFrame> StreamFrames { get; } = new();
        public TaskCompletionSource ForwardPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ForwardCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default)
            => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Enqueue(request);
            sink.OnHeader(Header);
            var plan = CommandBuilder.Plan(request);
            double seek = plan.SeekSeconds ?? 0;
            byte pixel = request.CorrelationId < 0 ? (byte)240 : request.Continuous ? StreamPixels : ReversePixels;
            try
            {
                for (int index = 0; request.Continuous || index < plan.FrameCount; ++index)
                {
                    var permission = sink.BeforeReadAsync(token);
                    if (!permission.IsCompleted)
                    {
                        sink.OnReadStateChanged(false);
                        if (request.Continuous) ForwardPaused.TrySetResult();
                    }
                    await permission;
                    sink.OnReadStateChanged(true);
                    token.ThrowIfCancellationRequested();
                    var frame = Frame(seek + index / 30.0, pixel);
                    if (request.Continuous) StreamFrames.Enqueue(frame);
                    sink.OnOutput();
                    sink.OnFrame(frame);
                }
                return new(0, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (request.Continuous) ForwardCancelled.TrySetResult();
                return new(-12, "", true);
            }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-stitch-reject-{Guid.NewGuid():N}");
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

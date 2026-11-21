using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class StitchRejectedStreamingViewModelTests
{
    private const byte OldPixels = 40, RejectedPixels = 90, StreamPixels = 140, ReversePixels = 190;
    private static readonly Y4mHeader Header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420");

    [AvaloniaFact]
    public async Task PixelRejectKeepsForwardContinuousAndReverseFiniteWithoutMixingRuns()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var scope = new RawOwnershipScope(new RawBufferPool());
        var cache = new FrameCache(scope);
        var request = new PreviewRequest
        {
            EncoderPath = FakeEncoderPath(), InputFile = "reject-stream.ts", InputArgs = ["--avhw"], Fps = 30, TargetSeconds = 10,
            Capabilities = new(true, true), TimestampOriginSeconds = 1000,
        };
        cache.StoreSegment(request, Frames(OldPixels), new(1000, 0, true), 9, 12);
        var backend = new ControlledBackend();
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        await using var vm = TestViewModels.Create(services, backend, null, cache,
            (frame, token) =>
            {
                byte[] result = new byte[frame.Header.Width * frame.Header.Height * 4];
                byte position = (byte)((int)Math.Round(((frame.Xts ?? 1000) - 1000) * 30) & 255);
                for (int index = 0; index < result.Length; index += 4)
                {
                    result[index] = frame.RawPixels[0];
                    result[index + 1] = position;
                    result[index + 3] = 255;
                }
                return Task.FromResult(result);
            },
            debounceOverride: TimeSpan.Zero, owned: dependencies);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = request.InputFile;
        vm.CurrentSeconds = 10;
        vm.InputArgsText = "--avhw";
        await vm.InitializeAsync(false);
        await vm.WaitForIdleAsync();
        // 二つの区間に異なる画素を与え、接続できない状態で操作を続ける。
        cache.StoreSegment(request, Frames(RejectedPixels), new(1000, 0, true), 9, 12);

        vm.CurrentSeconds = 13;
        await vm.WaitForPreparationAsync();
        await backend.ForwardPaused.Task;
        await vm.WaitForIdleAsync();
        for (int step = 0; step < 40; ++step)
        {
            vm.StepFrameCommand.Execute(-1);
            await vm.WaitForPreparationAsync();
            await vm.WaitForIdleAsync();
        }
        // 取得経路で時刻表現がミリ秒丸めの分だけ異なるため、フレームの同一性で比べる。
        Assert.True(Math.Abs(vm.CurrentSeconds - 11.6663333333) < 1.0 / 60.0);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorText);
        using (var pixels = Assert.IsType<WriteableBitmap>(vm.PreviewImage).Lock())
        {
            Assert.Equal(StreamPixels, Marshal.ReadByte(pixels.Address));
            Assert.Equal((byte)94, Marshal.ReadByte(pixels.Address, 1));
        }
        await vm.DisposeAsync().AsTask();
        Assert.True(scope.Capture().IsEmpty);
    }

    private static Y4mFrame Frame(double time, byte value)
        => new(Header, 1000 + time, 1.0 / 30, Enumerable.Repeat(value, Header.FrameBytes).ToArray());
    private static Y4mFrame[] Frames(byte value)
        => Enumerable.Range(0, 91).Select(index => Frame(9 + index / 30.0, value)).ToArray();
    private sealed class ControlledBackend : IPreviewBackend, IRawOwnedBackend
    {
        public TaskCompletionSource ForwardPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default)
            => Task.FromResult<bool?>(false);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => throw new InvalidOperationException("所有権のない経路は使えません。");
        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink,
            RawOwnershipScope scope, CancellationToken token = default)
        {
            sink.OnHeader(Header);
            var plan = CommandBuilder.Plan(request);
            double seek = plan.SeekSeconds ?? 0;
            long run = scope.NewRunId();
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
                    using var frame = scope.Create(Header, 1000 + seek + index / 30.0, 1.0 / 30,
                        Enumerable.Repeat(pixel, Header.FrameBytes).ToArray(), run, "試験の読取");
                    sink.OnOutput();
                    sink.OnFrame(frame.Frame);
                }
                return new(0, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
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

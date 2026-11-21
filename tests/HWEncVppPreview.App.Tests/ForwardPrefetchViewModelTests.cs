using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class ForwardPrefetchViewModelTests
{
    [AvaloniaFact]
    public async Task cache順移動の先読み開始で現在画像を境界へ飛ばさず微小逆移動も保持する()
    {
        using var capability = new CapabilityScope(); using var services = new Services();
        var backend = new Backend();
        await using var vm = TestViewModels.Create(services, backend, null, null,
            (frame, token) => Task.FromResult(Enumerable.Repeat(frame.Data[0], frame.Header.Width * frame.Header.Height * 4).ToArray()),
            debounceOverride: TimeSpan.Zero);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "c2-prefetch.ts";
        vm.CurrentSeconds = 10;
        vm.InputArgsText = "--avhw";
        await vm.InitializeAsync(false);
        await vm.WaitForIdleAsync();
        for (int i = 0; i < 6; i++)
        {
            vm.StepFrameCommand.Execute(1);
        }
        await backend.SecondStarted.Task;
        vm.StepFrameCommand.Execute(-1); await vm.WaitForIdleAsync();
        Assert.Equal(10.5, vm.CurrentSeconds, 7);
        Assert.Null(vm.ErrorText);
        using (var pixels = Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(vm.PreviewImage).Lock())
            Assert.Equal((byte)105, System.Runtime.InteropServices.Marshal.ReadByte(pixels.Address));
        // 先読みの取消と回収を待ってから物理要求数を確認する。
        await vm.DisposeAsync().AsTask();
        Assert.Equal(2, backend.Requests.Count);
    }

    private sealed class Backend : IPreviewBackend
    {
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancelled;
        public int Cancelled => Volatile.Read(ref _cancelled);
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Enqueue(request); int call = Requests.Count; if (call == 2) SecondStarted.TrySetResult();
            var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F10:1 Ip A1:1 C420"); sink.OnHeader(header);
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            try
            {
                for (int i = 0; ; i++)
                {
                    var permission = sink.BeforeReadAsync(token); if (!permission.IsCompleted) sink.OnReadStateChanged(false);
                    await permission; sink.OnReadStateChanged(true); token.ThrowIfCancellationRequested();
                    double time = seek + i / 10d; sink.OnOutput();
                    sink.OnFrame(new(header, 1000 + time, 0.1, Enumerable.Repeat((byte)((int)Math.Round(time * 10) % 251), header.FrameBytes).ToArray()));
                    // 初回の区間は入力EOFにせず終了し、activeなしの接続済みcacheを用意する。
                    if (call == 1 && time >= 12 - 1e-8) return new(-12, "", true);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { Interlocked.Increment(ref _cancelled); return new(-12, "", true); }
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

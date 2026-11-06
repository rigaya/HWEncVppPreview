using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.App.Tests;

public sealed class HdrDisplayViewModelTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public async Task WhiteChangesReconvertBothDisplayedFramesAndCachedFramesWithoutRequests()
    {
        using var services = new Services();
        var backend = new FramesBackend(afterHdr: true, beforeHdr: true);
        var clock = new FakeTimeProvider();
        await using var vm = Create(services, backend, clock);
        await vm.InitializeAsync(false).WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        int runs = backend.Runs;
        string after = Hash(vm.PreviewImage!), before = Hash(vm.BeforeImage!);
        string status = vm.StatusText;
        double seconds = vm.CurrentSeconds;
        vm.HdrReferenceWhiteNits = 400;
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.NotEqual(after, Hash(vm.PreviewImage!));
        Assert.NotEqual(before, Hash(vm.BeforeImage!));
        Assert.Equal(runs, backend.Runs);
        Assert.Equal(seconds, vm.CurrentSeconds);
        Assert.Equal(status, vm.StatusText);
        Assert.Contains("PQ→SDR 400nit", vm.ColorInfoText);
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(runs, backend.Runs);
        var raw = Assert.IsType<Y4mFrame>(typeof(MainWindowViewModel)
            .GetField("_displayedFrame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(YuvToBgra.Convert(raw, new() { ReferenceWhiteNits = 400 }))), Hash(vm.PreviewImage!));
        await vm.SaveSettingsAsync();
        Assert.Equal(400, AppSettings.Load(services.SettingsPath).HdrReferenceWhiteNits);
    }

    [AvaloniaFact]
    public async Task SdrAfterRemainsByteIdenticalWhenOnlyBeforeIsHdr()
    {
        using var services = new Services();
        var backend = new FramesBackend(afterHdr: false, beforeHdr: true);
        await using var vm = Create(services, backend, new FakeTimeProvider());
        await vm.InitializeAsync(false).WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        int runs = backend.Runs;
        Bitmap after = vm.PreviewImage!;
        string afterHash = Hash(after), beforeHash = Hash(vm.BeforeImage!);
        vm.HdrReferenceWhiteNits = 500;
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Same(after, vm.PreviewImage);
        Assert.Equal(afterHash, Hash(vm.PreviewImage!));
        Assert.NotEqual(beforeHash, Hash(vm.BeforeImage!));
        Assert.Equal(runs, backend.Runs);
        Assert.DoesNotContain("→SDR", vm.ColorInfoText);
        vm.IsShowingBefore = true;
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Contains("PQ→SDR 500nit", vm.ColorInfoText);
    }

    [AvaloniaFact]
    public async Task OldConversionCannotOverwriteAWhiteChangeWhileItIsRunning()
    {
        using var services = new Services();
        var backend = new FramesBackend(afterHdr: true, beforeHdr: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int hdrConversions = 0;
        async Task<byte[]> ConvertAsync(Y4mFrame frame, CancellationToken token)
        {
            if (!YuvToBgra.GetColorInfo(frame.Header).IsHdr) return YuvToBgra.Convert(frame);
            int version = Interlocked.Increment(ref hdrConversions);
            if (version == 1) { started.TrySetResult(); await release.Task.WaitAsync(token); }
            byte[] data = Enumerable.Repeat((byte)(version * 10), frame.Header.Width * frame.Header.Height * 4).ToArray();
            for (int i = 3; i < data.Length; i += 4) data[i] = 255;
            return data;
        }
        await using var vm = Create(services, backend, new FakeTimeProvider(), ConvertAsync);
        var shown = new List<byte>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.PreviewImage) && vm.PreviewImage is { } image)
            {
                using var locked = Assert.IsType<WriteableBitmap>(image).Lock();
                shown.Add(Marshal.ReadByte(locked.Address));
            }
        };
        try
        {
            await vm.InitializeAsync(false).WaitAsync(Safety);
            await started.Task.WaitAsync(Safety);
            vm.HdrReferenceWhiteNits = 400;
            vm.HdrReferenceWhiteNits = 500;
        }
        finally { release.TrySetResult(); }
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(2, hdrConversions);
        Assert.Equal(new byte[] { 20 }, shown);
        Assert.Equal(500, vm.HdrReferenceWhiteNits);
        Assert.Equal(2, backend.Runs);
    }

    private static MainWindowViewModel Create(Services services, FramesBackend backend, FakeTimeProvider clock,
        Func<Y4mFrame, CancellationToken, Task<byte[]>>? converter = null)
    {
        var vm = new MainWindowViewModel(services, clock, backend, backend, null, null, converter)
        { EncoderPath = FakeEncoderPath(), InputPath = "hdr.ts", InputArgsText = "--avhw --fake-duration 00:00:10.00" };
        vm.FilterLines.Add(new() { Text = "--vpp-tweak brightness=0.01" });
        return vm;
    }
    private sealed class FramesBackend(bool afterHdr, bool beforeHdr) : IPreviewBackend
    {
        public int Runs;
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            Runs++;
            bool hdr = request.VppArgs.Count > 0 ? afterHdr : beforeHdr;
            var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420 XCOLORMATRIX=bt2020nc XCOLORPRIM=bt2020 XTRANSFER=" + (hdr ? "smpte2084" : "bt709"));
            sink.OnHeader(header);
            for (int index = 0; index < 31; index++)
            {
                byte[] data = Enumerable.Repeat((byte)128, header.FrameBytes).ToArray();
                Array.Fill(data, (byte)(128 + index % 10), 0, header.Width * header.Height);
                sink.OnFrame(new(header, index / 30.0, 1.0 / 30, data));
            }
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static string Hash(Bitmap bitmap)
    {
        byte[] data = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new Avalonia.PixelRect(bitmap.PixelSize), pinned.AddrOfPinnedObject(), data.Length, bitmap.PixelSize.Width * 4); }
        finally { pinned.Free(); }
        return Convert.ToHexString(SHA256.HashData(data));
    }
    private static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), "tests", "FakeEncoder", "bin",
            configuration, "net10.0", OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }
    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "viewer-hdr-" + Guid.NewGuid().ToString("N"));
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}

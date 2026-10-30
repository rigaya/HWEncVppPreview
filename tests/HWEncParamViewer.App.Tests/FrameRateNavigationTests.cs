using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.App.Tests;

public sealed class FrameRateNavigationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public async Task DoubleRateRightStepsShowEveryOutputFrameWithoutSkipping()
    {
        using var services = new Services();
        var clock = new FakeTimeProvider();
        int starts = 0;
        await using var vm = Create(services, clock, () => ++starts);
        vm.CurrentSeconds = 6.0 / 60;
        await InitializeAsync(vm, clock);
        int initialStarts = starts;
        for (int expected = 7; expected <= 13; ++expected)
        {
            vm.StepFrameCommand.Execute(1);
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            output.WriteLine($"右: 期待frame={expected}, 目標={vm.CurrentSeconds:R}, 追加起動={starts - initialStarts}");
            Assert.Equal(expected / 60.0, vm.CurrentSeconds, 9);
            Assert.Equal(ExpectedHash(expected), ImageHash(vm));
        }
        Assert.Equal(initialStarts, starts);
    }

    [AvaloniaFact]
    public async Task DoubleRateLeftStepStaysInsideTheCachedInterval()
    {
        using var services = new Services();
        var clock = new FakeTimeProvider();
        int starts = 0;
        await using var vm = Create(services, clock, () => ++starts);
        vm.CurrentSeconds = 13.0 / 60;
        await InitializeAsync(vm, clock);
        int initialStarts = starts;
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        output.WriteLine($"左: 期待frame=12, 目標={vm.CurrentSeconds:R}, 追加起動={starts - initialStarts}");
        Assert.Equal(initialStarts, starts);
        Assert.Equal(12.0 / 60, vm.CurrentSeconds, 9);
        Assert.Equal(ExpectedHash(12), ImageHash(vm));
    }

    private static MainWindowViewModel Create(Services services, FakeTimeProvider clock, Action started)
    {
        var backend = new OneShotBackend(_ => started());
        var vm = new MainWindowViewModel(services, clock, backend, backend)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "frame rate input.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };
        // 入力検査は30fpsのまま、フィルタ後のヘッダだけを60fpsにする。
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--fake-output-rate double --fake-no-xdur" });
        return vm;
    }

    private static async Task InitializeAsync(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        await vm.InitializeAsync(false).WaitAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.PreviewImage);
    }

    private static string ExpectedHash(int index)
    {
        var header = new Y4mHeader { Width = 64, Height = 36, FpsNum = 60, FpsDen = 1 };
        byte[] seed = SHA256.HashData(Array.Empty<byte>());
        byte[] data = new byte[header.FrameBytes];
        for (int i = 0; i < data.Length; ++i) data[i] = (byte)(seed[i % seed.Length] + index);
        return Convert.ToHexStringLower(SHA256.HashData(YuvToBgra.Convert(new(header, index / 60.0, null, data))));
    }

    private static string ImageHash(MainWindowViewModel vm)
    {
        var bitmap = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
        using var frame = bitmap.Lock();
        byte[] row = new byte[bitmap.PixelSize.Width * 4];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int y = 0; y < bitmap.PixelSize.Height; ++y)
        {
            Marshal.Copy(IntPtr.Add(frame.Address, y * frame.RowBytes), row, 0, row.Length);
            hash.AppendData(row);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
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
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-frame-rate-{Guid.NewGuid():N}");
        public Services() => Directory.CreateDirectory(_directory);
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}

using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class RealEncoderSeekTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeeksWithTheSelectedDeviceAndClampsToInputDuration(bool filtered)
    {
        string? encoder = Environment.GetEnvironmentVariable("HWENC_VIEWER_SEEK_ENCODER");
        string? input = Environment.GetEnvironmentVariable("HWENC_VIEWER_SEEK_INPUT");
        if (string.IsNullOrWhiteSpace(encoder) || string.IsNullOrWhiteSpace(input))
            Assert.Skip("実シークの検証はエンコーダと入力を明示したときだけ実行する");
        await using var vm = new MainWindowViewModel(new SeekServices())
        {
            EncoderPath = encoder!, InputPath = input!,
            InputArgsText = Environment.GetEnvironmentVariable("HWENC_VIEWER_SEEK_ARGS") ?? "-d 1",
        };
        if (filtered) vm.FilterLines.Add(new() { Text = "--vpp-tweak brightness=0.01" });
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(vm.ErrorText);
        Assert.True(vm.HasDuration);
        Assert.NotNull(vm.PreviewImage);
        if (filtered) Assert.NotNull(vm.BeforeImage);
        Assert.True(double.IsFinite(vm.DurationSeconds) && vm.DurationSeconds > 0);
        double duration = vm.DurationSeconds;
        foreach (double seconds in new[] { duration * 0.04, duration * 0.56, Math.Max(0, duration - 10), duration + 120, duration * 0.07, 0 })
        {
            vm.CurrentSeconds = seconds;
            await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.True(vm.ErrorText == null, $"seek={seconds}, duration={vm.DurationSeconds}, current={vm.CurrentSeconds}: {vm.ErrorText}");
            Assert.NotNull(vm.PreviewImage);
            if (filtered) Assert.NotNull(vm.BeforeImage);
            Assert.InRange(vm.CurrentSeconds, 0, vm.DurationSeconds);
        }
    }

    private sealed class SeekServices : IViewModelServices
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "hwenc-seek-unused.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
}

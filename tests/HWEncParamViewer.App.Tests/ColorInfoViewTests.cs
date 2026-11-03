using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.App.Views;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;
using static HWEncParamViewer.App.Tests.CompletionViewTests;

namespace HWEncParamViewer.App.Tests;

public sealed class ColorInfoViewTests
{
    [AvaloniaFact]
    public async Task 表示画像の色とHDRの印を表示し言語と前後の切り替えに追従する()
    {
        string previous = UiLanguage.Preference;
        var clock = new FakeTimeProvider();
        await using var vm = new MainWindowViewModel(timeProvider: clock, preferred: new OneShotBackend())
        {
            EncoderPath = FakeEncoderPath(), InputPath = "fake.ts"
        };
        vm.FilterLines.Add(new FilterLineViewModel
        {
            Text = "--fake-colormatrix bt2020nc --fake-colorrange full --fake-transfer smpte2084"
        });
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        try
        {
            UiLanguage.Set("ja");
            Assert.Equal("", vm.ColorInfoText);
            window.Show();
            await vm.InitializeAsync(false).WaitAsync(TimeSpan.FromSeconds(20));
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await vm.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Flush();
            Assert.NotNull(vm.PreviewImage);
            Assert.Contains("BT.2020", vm.ColorInfoText);
            Assert.Contains("full", vm.ColorInfoText);
            Assert.Contains("HDR (SDRとして表示)", vm.ColorInfoText);
            Assert.Equal(vm.ColorInfoText, window.FindControl<TextBlock>("ColorInfoLabel")!.Text);
            UiLanguage.Set("en");
            await Flush();
            Assert.Contains("HDR (shown as SDR)", vm.ColorInfoText);
            string after = vm.ColorInfoText;

            vm.IsShowingBefore = true;
            await vm.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Flush();
            Assert.NotNull(vm.BeforeImage);
            Assert.Equal("BT.601 limited", vm.ColorInfoText);
            vm.IsShowingBefore = false;
            Assert.Equal(after, vm.ColorInfoText);
            vm.CompareMode = PreviewCompareMode.Split;
            Assert.Equal(after, vm.ColorInfoText);
        }
        finally
        {
            window.Close();
            UiLanguage.Set(previous);
        }
    }

    private static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), "tests", "FakeEncoder", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }
}

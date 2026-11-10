using Avalonia.Headless.XUnit;
using HWEncVppPreview.App;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.App.Views;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class EncoderDiscoveryViewModelTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;

    [AvaloniaFact]
    public async Task 自動取得は画面に示し設定へ保存せず次回の起動で探し直す()
    {
        using var services = new Services { Found = Missing("automatic-a") };
        MainWindow? window = null;
        await using (var vm = new MainWindowViewModel(services))
        {
            try
            {
                window = new MainWindow { DataContext = vm };
                window.Show();
                await vm.InitializeAsync().WaitWithDiagnosticsAsync(Safety);
                await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
                Assert.True(vm.IsEncoderPathAutomatic);
                Assert.Equal(services.Found, vm.EncoderPath);
                Assert.Contains(AppMessages.AutomaticallySelected, vm.EncoderDescription);
                Assert.EndsWith(ApplicationVersion.Value, window.Title);
                await vm.SaveSettingsAsync().WaitWithDiagnosticsAsync(Safety);
                Assert.Equal("", AppSettings.Load(services.SettingsPath).EncoderPath);
            }
            finally { window?.Close(); }
        }
        services.Found = Missing("automatic-b");
        await using var next = new MainWindowViewModel(services);
        await next.InitializeAsync().WaitWithDiagnosticsAsync(Safety);
        await next.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(services.Found, next.EncoderPath);
        Assert.True(next.IsEncoderPathAutomatic);
        Assert.Equal(2, services.Searches);
    }

    [AvaloniaFact]
    public async Task 自動と同じ経路を利用者が選び直すと明示指定として保存する()
    {
        using var services = new Services { Found = Missing("automatic") };
        await using (var vm = new MainWindowViewModel(services))
        {
            await vm.InitializeAsync().WaitWithDiagnosticsAsync(Safety);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            vm.OpenEncoder(vm.EncoderPath);
            Assert.False(vm.IsEncoderPathAutomatic);
            Assert.DoesNotContain(AppMessages.AutomaticallySelected, vm.EncoderDescription);
            await vm.SaveSettingsAsync().WaitWithDiagnosticsAsync(Safety);
            Assert.Equal(services.Found, AppSettings.Load(services.SettingsPath).EncoderPath);
        }
        await using var next = new MainWindowViewModel(services);
        await next.InitializeAsync().WaitWithDiagnosticsAsync(Safety);
        await next.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.False(next.IsEncoderPathAutomatic);
        Assert.Equal(1, services.Searches);
    }

    [AvaloniaFact]
    public async Task 存在しない明示指定でも自動探索へ切り替えず入力検査のエラーを示す()
    {
        using var services = new Services { Found = Missing("fallback-must-not-be-used") };
        string explicitPath = Missing("explicit");
        new AppSettings { EncoderPath = explicitPath, InputPath = "missing-input.ts" }.Save(services.SettingsPath);
        await using var vm = new MainWindowViewModel(services);
        await vm.InitializeAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(explicitPath, vm.EncoderPath);
        Assert.False(vm.IsEncoderPathAutomatic);
        Assert.Equal(0, services.Searches);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorText));
    }

    [AvaloniaFact]
    public async Task 候補がない場合は従来の未選択案内に戻る()
    {
        using var services = new Services();
        await using var vm = new MainWindowViewModel(services);
        await vm.InitializeAsync().WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal("", vm.EncoderPath);
        Assert.False(vm.IsEncoderPathAutomatic);
        Assert.Equal(AppMessages.SelectEncoderAndInput, vm.StatusText);
        Assert.Null(vm.ErrorText);
    }

    [Theory]
    [InlineData("rkmppenc (aarch64) 0.2.17", "rkmppenc 0.2.17")]
    [InlineData("RKMPPENC 1.23", "rkmppenc 1.23")]
    [InlineData("QSVEncC (x64) 8.36", "QSVEnc 8.36")]
    public void エンコーダの説明は対応する名前と版を示す(string output, string expected)
    {
        var method = typeof(MainWindowViewModel).GetMethod("DescribeEncoder",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, [output, "encoder"]));
    }

    private static string Missing(string label) => Path.Combine(Path.GetTempPath(),
        "viewer-encoder-not-present-" + label + "-" + Guid.NewGuid().ToString("N"));

    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "viewer-auto-" + Guid.NewGuid().ToString("N"));
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        internal string? Found { get; set; }
        internal int Searches { get; private set; }
        public string? FindEncoder()
        {
            ++Searches;
            return Found;
        }
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose()
        {
            if (!Directory.Exists(_directory)) return;
            foreach (string file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}

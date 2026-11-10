using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.Localization;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.App.Views;
using HWEncVppPreview.Core;
using static HWEncVppPreview.App.Tests.CompletionViewTests;

namespace HWEncVppPreview.App.Tests;

public sealed class UiLanguageViewTests
{
    private const string Help = """
VPP Options:
  [denoise] Denoise
   --vpp-alpha [<param1>=<value>][,<param2>=<value>][...]
     Test filter.
    params
      sigma=<float> Strength. (default: 0.50, range: 0..1)
      dependent=<float> Automatic strength. (default: derived)
""";

    [AvaloniaFact]
    public async Task 切り替えると画面と分類とフォームが更新され行は変わらない()
    {
        string previous = UiLanguage.Preference;
        MainWindow? window = null;
        await using var vm = new MainWindowViewModel();
        try
        {
            vm.Language = "ja";
            typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.HelpSchema))!.SetValue(vm, new HelpSchema(Help));
            var line = new FilterLineViewModel { Text = "--vpp-alpha sigma=0.25" };
            vm.FilterLines.Add(line);
            vm.SelectedFilterLine = line;
            var oldParameter = line.Parameters[0];
            var oldCatalog = vm.FilterCatalog[0];
            var oldText = Localizer.Instance.Text;
            Assert.Equal("ノイズ除去", line.CategoryName);
            Assert.Equal("自動", line.Parameters[1].DefaultText);
            window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            await Flush();
            Assert.Equal("HWEnc VPPプレビュー " + ApplicationVersion.Value, window.Title);
            Assert.Equal("＋ フィルタを追加", window.FindControl<Button>("AddFilterButton")!.Content);
            var selector = window.FindControl<ComboBox>("LanguageSelector")!;
            Assert.Equal(1, selector.SelectedIndex);

            // 実際の選択欄から切り替え、静的文言とViewModelの表示を両方確認する。
            selector.SelectedIndex = 2;
            await Flush();
            Assert.Equal("en", vm.Language);
            Assert.NotSame(oldText, Localizer.Instance.Text);
            Assert.Equal("HWEnc VPP Preview " + ApplicationVersion.Value, window.Title);
            Assert.Equal("+ Add filter", window.FindControl<Button>("AddFilterButton")!.Content);
            Assert.Equal("Denoise", line.CategoryName);
            Assert.Equal("Denoise", vm.FilterCatalog[0].CategoryName);
            Assert.NotSame(oldParameter, line.Parameters[0]);
            Assert.NotSame(oldCatalog, vm.FilterCatalog[0]);
            Assert.NotEqual("自動", line.Parameters[1].DefaultText);
            Assert.Equal("--vpp-alpha sigma=0.25", line.Text);
            Assert.Equal("0.25", line.Parameters[0].ValueText);

            vm.Language = "ja";
            await Flush();
            Assert.Equal(1, selector.SelectedIndex);
            Assert.Equal("HWEnc VPPプレビュー " + ApplicationVersion.Value, window.Title);
            Assert.Equal("ノイズ除去", line.CategoryName);
            Assert.Equal("--vpp-alpha sigma=0.25", line.Text);
        }
        finally
        {
            window?.Close();
            UiLanguage.Set(previous);
        }
    }

    [AvaloniaFact]
    public async Task 自動設定は日本語のOS設定へ戻り文言オブジェクトも差し替わる()
    {
        string previous = UiLanguage.Preference;
        try
        {
            Localizer.Instance.Language = "en";
            var english = Localizer.Instance.Text;
            Assert.Equal("+ Add filter", english.AddFilter);
            Localizer.Instance.Language = "auto";
            await Flush();
            Assert.Equal("ja", UiLanguage.Current);
            Assert.NotSame(english, Localizer.Instance.Text);
            Assert.Equal("＋ フィルタを追加", Localizer.Instance.Text.AddFilter);
        }
        finally { UiLanguage.Set(previous); }
    }

    [AvaloniaFact]
    public async Task 言語をViewModelから保存して次回初期化で復元する()
    {
        string previous = UiLanguage.Preference;
        using var services = new LanguageServices();
        try
        {
            await using (var vm = new MainWindowViewModel(services))
            {
                vm.Language = "en";
                await vm.SaveSettingsAsync();
            }
            Assert.Equal("en", AppSettings.Load(services.SettingsPath).Language);
            UiLanguage.Set("ja");
            await using var restored = new MainWindowViewModel(services);
            await restored.InitializeAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.Equal("en", restored.Language);
            Assert.Equal("en", UiLanguage.Current);
            Assert.Equal("+ Add filter", Localizer.Instance.Text.AddFilter);
        }
        finally { UiLanguage.Set(previous); }
    }

    [AvaloniaFact]
    public async Task 別スレッドの言語変更もUIへ非同期に通知する()
    {
        string previous = UiLanguage.Preference;
        bool notified = false, onUi = false;
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(Localizer.Text)) return;
            notified = true;
            onUi = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
        }
        try
        {
            UiLanguage.Set("ja");
            Localizer.Instance.PropertyChanged += Changed;
            await Task.Run(() => UiLanguage.Set("en"));
            await Flush();
            Assert.True(notified);
            Assert.True(onUi);
            Assert.Equal("+ Add filter", Localizer.Instance.Text.AddFilter);
        }
        finally
        {
            Localizer.Instance.PropertyChanged -= Changed;
            UiLanguage.Set(previous);
        }
    }

    private sealed class LanguageServices : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "viewer-language-ui-" + Guid.NewGuid().ToString("N"));
        public LanguageServices() => Directory.CreateDirectory(_directory);
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

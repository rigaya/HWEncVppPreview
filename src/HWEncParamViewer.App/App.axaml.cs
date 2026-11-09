using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.App.Views;

namespace HWEncParamViewer.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm };
            bool closing = false;
            bool mayClose = false;
            window.Opened += (_, _) =>
            {
                _ = InitializeWindowAsync();
                // 描画より低い優先度へ送り、画面表示を掃除の待ちにしない。
                Dispatcher.UIThread.Post(NativeExtractionCleanupService.Start, DispatcherPriority.Background);
            };
            window.Closing += (_, e) =>
            {
                if (mayClose) return;
                // 子プロセスの終了と設定保存が済むまで、最終ウィンドウを閉じない。
                e.Cancel = true;
                if (closing) return;
                closing = true;
                _ = CloseWindowAsync();
            };
            desktop.MainWindow = window;

            async Task InitializeWindowAsync()
            {
                try
                {
                    await vm.InitializeAsync().ConfigureAwait(false);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (closing) return;
                        window.Width = vm.WindowWidth;
                        window.Height = vm.WindowHeight;
                    });
                }
                catch (Exception exception)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!closing) vm.ErrorText = exception.Message;
                    });
                }
            }

            async Task CloseWindowAsync()
            {
                double width = window.Width;
                double height = window.Height;
                try { await vm.SaveSettingsAsync(width, height).ConfigureAwait(false); }
                catch (Exception) { /* 保存に失敗しても、終了と子プロセスの回収は続ける。 */ }
                finally
                {
                    try { await vm.DisposeAsync().ConfigureAwait(false); }
                    finally
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            mayClose = true;
                            window.Close();
                        });
                    }
                }
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

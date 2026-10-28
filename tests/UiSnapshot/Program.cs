using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.App.Views;

// 使い方: UiSnapshot <出力ディレクトリ>
// MainWindow をデザイン用のダミー値で描画し、ライト/ダーク・状態違いの PNG を保存する。
string outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

AppBuilder.Configure<HWEncParamViewer.App.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

void Capture(string name, ThemeVariant theme, Action<MainWindowViewModel> setup)
{
    Application.Current!.RequestedThemeVariant = theme;
    var vm = MainWindowViewModel.CreateDesignSample();
    vm.PreviewImage = CreateTestImage(1440, 1080);
    vm.DisplayWidth = 1920;   // SAR 4:3 の 1440x1080 を 16:9 で表示
    vm.DisplayHeight = 1080;
    setup(vm);
    var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    using var frame = window.CaptureRenderedFrame();
#pragma warning disable CS0618 // 開発用ツールなので、PNG 保存は簡単な旧 API で十分
    frame!.Save(Path.Combine(outDir, name + ".png"));
#pragma warning restore CS0618
    window.Close();
    Console.WriteLine(name);
}

Capture("dark", ThemeVariant.Dark, _ => { });
Capture("light", ThemeVariant.Light, _ => { });
Capture("dark_busy_error", ThemeVariant.Dark, vm =>
{
    vm.IsBusy = true;
    vm.ErrorText = "エンコーダがエラー終了しました: -24\nInvalid value for --vpp-nlmeans sigma=: abc";
    vm.IsLogOpen = true;
    vm.LogText = "RGYPREVIEW BEGIN id=12\nInvalid value for --vpp-nlmeans sigma=: abc\nRGYPREVIEW END id=12 rc=-24";
});
Capture("dark_empty", ThemeVariant.Dark, vm =>
{
    vm.PreviewImage = null;
    vm.HasDuration = false;
    vm.FilterLines.Clear();
    vm.HelpText = "";
});

// テスト用の画像: グラデーションとカラーバー (色の評価に使う画面なので、無彩色の背景との見え方を確認する)
static WriteableBitmap CreateTestImage(int w, int h)
{
    var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    using var fb = bmp.Lock();
    unsafe
    {
        uint[] bars = [0xFFC0C0C0, 0xFFC0C000, 0xFF00C0C0, 0xFF00C000, 0xFFC000C0, 0xFFC00000, 0xFF0000C0];
        for (int y = 0; y < h; y++)
        {
            var row = (uint*)((byte*)fb.Address + y * fb.RowBytes);
            for (int x = 0; x < w; x++)
            {
                if (y < h * 2 / 3) row[x] = bars[x * bars.Length / w];
                else
                {
                    uint g = (uint)(x * 255 / (w - 1));
                    row[x] = 0xFF000000 | (g << 16) | (g << 8) | g;
                }
            }
        }
    }
    return bmp;
}

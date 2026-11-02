using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using HWEncParamViewer.App.Services;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.App.Views;

// 使い方: UiSnapshot <出力ディレクトリ> [--encoder <exe> --input <入力> --time <秒>]
// MainWindow をデザイン用のダミー値で描画し、ライト/ダーク・状態違いの PNG を保存する。
string outDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : ".";
Directory.CreateDirectory(outDir);

AppBuilder.Configure<HWEncParamViewer.App.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

void Capture(string name, ThemeVariant theme, Action<MainWindowViewModel> setup, Action<MainWindow>? afterShow = null)
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
    if (afterShow != null)
    {
        afterShow(window);
        Dispatcher.UIThread.RunJobs();
    }
    using var frame = window.CaptureRenderedFrame();
#pragma warning disable CS0618 // 開発用ツールなので、PNG 保存は簡単な旧 API で十分
    frame!.Save(Path.Combine(outDir, name + ".png"));
#pragma warning restore CS0618
    window.Close();
    Pump(vm.DisposeAsync().AsTask());
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
// V5: 比較表示と拡大 (フィルタ前はグレースケールにした画像で代用する)
Capture("dark_split", ThemeVariant.Dark, vm =>
{
    vm.BeforeImage = CreateTestImage(1440, 1080, grayscale: true);
    vm.CompareMode = PreviewCompareMode.Split;
    vm.SplitPosition = 0.4;
});
Capture("dark_side_by_side", ThemeVariant.Dark, vm =>
{
    vm.BeforeImage = CreateTestImage(1440, 1080, grayscale: true);
    vm.CompareMode = PreviewCompareMode.SideBySide;
});
Capture("dark_zoom_toggle_before", ThemeVariant.Dark, vm =>
{
    vm.BeforeImage = CreateTestImage(1440, 1080, grayscale: true);
    vm.IsShowingBefore = true;
    vm.IsBeforeBusy = true;
}, window =>
{
    // カラーバーとグラデーションの境界付近を 800% で表示する (nearest 補間の確認)
    var viewer = window.FindControl<HWEncParamViewer.App.Controls.ZoomPanViewer>("MainViewer")!;
    viewer.Zoom = new HWEncParamViewer.App.Controls.ZoomState(false, 8.0, new Point(275, 720));
});

// V6: フィルタ行の補完
CaptureCompletion("dark_completion_option_category", ThemeVariant.Dark, "", category: true);
CaptureCompletion("dark_completion_option_typing", ThemeVariant.Dark, "--vpp-n");
CaptureCompletion("dark_completion_parameter", ThemeVariant.Dark, "--vpp-kfm ");
CaptureCompletion("light_completion_value", ThemeVariant.Light, "--vpp-kfm mode=");

// V8: パラメータのフォーム (設計書 23.2)
CaptureForm("dark_form", ThemeVariant.Dark, ["--vpp-nlmeans sigma=0.01,h=0.08", "--vpp-kfm mode=24", "--vpp-edgelevel"], 0);
CaptureForm("light_form_choice", ThemeVariant.Light, ["--vpp-nlmeans sigma=0.01", "--vpp-kfm mode=24,thswitch=0.6"], 1);
CaptureForm("dark_form_empty", ThemeVariant.Dark, [], -1);
CaptureForm("dark_form_add", ThemeVariant.Dark, ["--vpp-nlmeans"], 0, openAdd: true);
CaptureForm("dark_form_textmode", ThemeVariant.Dark, ["--vpp-nlmeans sigma=0.01,h=0.08", "--vpp-unknown a=1"], 1, textMode: true);

void CaptureForm(string name, ThemeVariant theme, string[] lines, int selected, bool openAdd = false, bool textMode = false)
{
    Application.Current!.RequestedThemeVariant = theme;
    var vm = MainWindowViewModel.CreateDesignSample();
    vm.PreviewImage = CreateTestImage(1440, 1080);
    vm.FilterLines.Clear();
    typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.HelpSchema))!
        .SetValue(vm, new HWEncParamViewer.Core.HelpSchema(File.ReadAllText(HelpDataPath("qsvenc-h1.txt"))));
    foreach (var text in lines) vm.FilterLines.Add(new FilterLineViewModel { Text = text });
    vm.SelectedFilterLine = selected >= 0 ? vm.FilterLines[selected] : null;
    vm.IsTextListMode = textMode;
    var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    if (openAdd)
    {
        window.FindControl<Button>("AddFilterButton")!.Flyout!.ShowAt(window.FindControl<Button>("AddFilterButton")!);
        Dispatcher.UIThread.RunJobs();
    }
    string before = string.Join(" | ", vm.FilterLines.Select(line => line.Text));
#pragma warning disable CS0618 // 開発用ツールなので、PNG 保存は簡単な旧 API で十分
    using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(outDir, name + ".png"));
#pragma warning restore CS0618
    // 表示しただけで行のテキストが変わっていないこと (欄の初期化で書き戻さないこと)
    string after = string.Join(" | ", vm.FilterLines.Select(line => line.Text));
    window.Close();
    Pump(vm.DisposeAsync().AsTask());
    Console.WriteLine(name + (before == string.Join(" | ", lines) && after == before ? "" : $" (行が変わった: {after})"));
}

void CaptureCompletion(string name, ThemeVariant theme, string text, bool category = false)
{
    Application.Current!.RequestedThemeVariant = theme;
    var vm = MainWindowViewModel.CreateDesignSample();
    vm.PreviewImage = CreateTestImage(1440, 1080);
    // デザイン用の ViewModel はエンコーダを実行しないので、H1 の書式の実 help を直接与える (開発用ツールなのでリフレクションで足りる)
    typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.HelpSchema))!
        .SetValue(vm, new HWEncParamViewer.Core.HelpSchema(File.ReadAllText(HelpDataPath("qsvenc-h1.txt"))));
    var line = new FilterLineViewModel { Text = text };
    vm.FilterLines.Add(line);
    // V8 以降、行のテキスト欄は選んだ行のフォームの上にある
    vm.SelectedFilterLine = line;
    var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("filterLine") && ReferenceEquals(t.DataContext, line));
    window.Activate();
    box.Focus();
    box.CaretIndex = text.Length;
    Dispatcher.UIThread.RunJobs();
    if (category) window.FindControl<Button>("OrderCategoryButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    window.KeyPress(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Space, null);
    Dispatcher.UIThread.RunJobs();
    // Headless ではポップアップがウィンドウ内に重ねて描かれるので、ウィンドウの撮影だけで一覧も写る
    bool popupOpen = window.FindControl<Avalonia.Controls.Primitives.Popup>("CompletionPopup")!.IsOpen;
#pragma warning disable CS0618 // 開発用ツールなので、PNG 保存は簡単な旧 API で十分
    using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(outDir, name + ".png"));
#pragma warning restore CS0618
    window.Close();
    Pump(vm.DisposeAsync().AsTask());
    Console.WriteLine(name + (popupOpen ? "" : " (補完の一覧が開いていない)"));
}

static string HelpDataPath(string file)
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "Data", "help"))) directory = directory.Parent;
    return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("tests/Data/help"), "tests", "Data", "help", file);
}

// 実行入口と同じViewModel経路で取得した画像を、デザイン用の生成画像とは別名で保存する。
string? encoder = Option(args, "--encoder");
string? input = Option(args, "--input");
if (encoder != null || input != null)
{
    if (encoder == null || input == null) throw new ArgumentException("--encoderと--inputは両方指定してください");
    double seconds = double.Parse(Option(args, "--time") ?? "0.5", CultureInfo.InvariantCulture);
    if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentException("--timeは有限の0以上の秒数が必要です");
    var vm = new MainWindowViewModel(new SnapshotServices(outDir))
    {
        EncoderPath = encoder,
        InputPath = input,
        InputArgsText = Option(args, "--input-args") ?? "--avhw",
        CurrentSeconds = seconds,
    };
    string? vpp = Option(args, "--vpp");
    if (vpp != null)
    {
        var line = new FilterLineViewModel { Text = vpp };
        vm.FilterLines.Add(line);
        vm.SelectedFilterLine = line;
    }
    var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
    window.Show();
    try
    {
        Pump(vm.InitializeAsync(false));
        Pump(vm.WaitForIdleAsync());
        if (vm.PreviewImage == null) throw new InvalidOperationException(vm.ErrorText ?? "実プレビュー画像が生成されませんでした");
        foreach (var variant in new[] { ("real_dark", ThemeVariant.Dark), ("real_light", ThemeVariant.Light) })
        {
            Application.Current!.RequestedThemeVariant = variant.Item2;
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
#pragma warning disable CS0618 // 開発用ツールのPNG保存に既存APIを使う。
            frame!.Save(Path.Combine(outDir, variant.Item1 + ".png"));
#pragma warning restore CS0618
            Console.WriteLine(variant.Item1);
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        foreach (var variant in new[]
        {
            ("real_split", PreviewCompareMode.Split, false),
            ("real_side_by_side", PreviewCompareMode.SideBySide, false),
            ("real_toggle_before", PreviewCompareMode.Single, true),
        })
        {
            vm.SetCompareModeCommand.Execute(variant.Item2);
            vm.IsShowingBefore = variant.Item3;
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
#pragma warning disable CS0618 // 実プレビュー比較の目視確認用PNGを保存する。
            frame!.Save(Path.Combine(outDir, variant.Item1 + ".png"));
#pragma warning restore CS0618
            Console.WriteLine(variant.Item1);
        }
    }
    finally
    {
        Pump(vm.DisposeAsync().AsTask());
        window.Close();
    }
}

static string? Option(string[] arguments, string name)
{
    int index = Array.IndexOf(arguments, name);
    return index < 0 ? null : index + 1 < arguments.Length ? arguments[index + 1]
        : throw new ArgumentException($"{name}の値がありません");
}

static void Pump(Task operation)
{
    // HeadlessのコンソールにはUIループがないため、Postの反映と非同期処理を同じスレッドで待つ。
    while (!operation.IsCompleted)
    {
        Dispatcher.UIThread.RunJobs();
        ((IAsyncResult)operation).AsyncWaitHandle.WaitOne(5);
    }
    operation.GetAwaiter().GetResult();
    Dispatcher.UIThread.RunJobs();
}

// テスト用の画像: グラデーションとカラーバー (色の評価に使う画面なので、無彩色の背景との見え方を確認する)
static WriteableBitmap CreateTestImage(int w, int h, bool grayscale = false)
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
                if (y < h * 2 / 3)
                {
                    uint c = bars[x * bars.Length / w];
                    if (grayscale)
                    {
                        // フィルタ前の代用: 同じ図柄をグレースケールにする
                        uint g = (((c >> 16) & 0xFF) * 77 + ((c >> 8) & 0xFF) * 150 + (c & 0xFF) * 29) >> 8;
                        c = 0xFF000000 | (g << 16) | (g << 8) | g;
                    }
                    row[x] = c;
                }
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

sealed class SnapshotServices(string outputDirectory) : IViewModelServices
{
    public string SettingsPath => Path.Combine(outputDirectory, "snapshot.settings.json");
    public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
    public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
    public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
    public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
    public Task CopyTextAsync(string text) => Task.CompletedTask;
}

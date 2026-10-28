using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;

namespace HWEncParamViewer.App.ViewModels;

/// <summary>
/// メインウィンドウの ViewModel。
/// プロパティ名とコマンド名は View (yua) との契約 (設計書 17.5)。変える場合は相談すること。
/// TODO(airi): 各プロパティの更新処理と、コマンドの実装、Core (PreviewController など) との連携。
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private string _encoderPath = "";
    private string _inputPath = "";
    private string _encoderDescription = "";
    private string _inputArgsText = "--avhw";
    private FilterLineViewModel? _selectedFilterLine;
    private string _helpText = "";
    private Bitmap? _previewImage;
    private double _displayWidth = 1920;
    private double _displayHeight = 1080;
    private string _currentTimeText = "0:00:00.000";
    private double _currentSeconds;
    private double _durationSeconds;
    private bool _hasDuration;
    private string _durationText = "";
    private bool _isBusy;
    private string _statusText = "";
    private string? _errorText;
    private string _logText = "";
    private bool _isLogOpen;
    private string _backendModeText = "";
    private string _elapsedText = "";
    private string _inputInfoText = "";

    public MainWindowViewModel()
    {
        BrowseEncoderCommand = new RelayCommand(() => { });
        BrowseInputCommand = new RelayCommand(() => { });
        AddFilterLineCommand = new RelayCommand(() => FilterLines.Add(new FilterLineViewModel()));
        RemoveFilterLineCommand = new RelayCommand(p => { if (p is FilterLineViewModel line) FilterLines.Remove(line); });
        StepFrameCommand = new RelayCommand(_ => { });
        StepSecondsCommand = new RelayCommand(_ => { });
        GoToStartCommand = new RelayCommand(() => { });
        GoToEndCommand = new RelayCommand(() => { });
        CommitTimeTextCommand = new RelayCommand(() => { });
        RefreshCommand = new RelayCommand(() => { });
        SaveFiltersCommand = new RelayCommand(() => { });
        LoadFiltersCommand = new RelayCommand(() => { });
        CopyCommandLineCommand = new RelayCommand(() => { });
        ToggleLogCommand = new RelayCommand(() => IsLogOpen = !IsLogOpen);
    }

    public string EncoderPath { get => _encoderPath; set => SetProperty(ref _encoderPath, value); }
    public string InputPath { get => _inputPath; set => SetProperty(ref _inputPath, value); }
    /// <summary>例: "QSVEnc 8.35"。</summary>
    public string EncoderDescription { get => _encoderDescription; set => SetProperty(ref _encoderDescription, value); }
    public string InputArgsText { get => _inputArgsText; set => SetProperty(ref _inputArgsText, value); }

    public ObservableCollection<FilterLineViewModel> FilterLines { get; } = new();
    /// <summary>フォーカスのある行。View の code-behind が設定する。ヘルプ表示の対象。</summary>
    public FilterLineViewModel? SelectedFilterLine { get => _selectedFilterLine; set => SetProperty(ref _selectedFilterLine, value); }
    public string HelpText { get => _helpText; set => SetProperty(ref _helpText, value); }

    /// <summary>表示中の画像 (BGRA)。処理中も直前の画像を保持する。</summary>
    public Bitmap? PreviewImage { get => _previewImage; set => SetProperty(ref _previewImage, value); }
    /// <summary>SAR を考慮した表示上の幅・高さ (画素)。View はこの比率で拡大縮小して表示する。</summary>
    public double DisplayWidth { get => _displayWidth; set => SetProperty(ref _displayWidth, value); }
    public double DisplayHeight { get => _displayHeight; set => SetProperty(ref _displayHeight, value); }

    /// <summary>時刻欄 (双方向)。Enter で CommitTimeTextCommand。</summary>
    public string CurrentTimeText { get => _currentTimeText; set => SetProperty(ref _currentTimeText, value); }
    /// <summary>シークバーの位置 (秒)。ドラッグ中は View が更新しても要求を出さないこと。</summary>
    public double CurrentSeconds { get => _currentSeconds; set => SetProperty(ref _currentSeconds, value); }
    /// <summary>入力の長さ (秒)。HasDuration が false のときは意味を持たない。</summary>
    public double DurationSeconds { get => _durationSeconds; set => SetProperty(ref _durationSeconds, value); }
    /// <summary>入力の長さが分かっているか。false ならシークバーを隠す (設計書 17.1)。</summary>
    public bool HasDuration { get => _hasDuration; set => SetProperty(ref _hasDuration, value); }
    /// <summary>例: "1:52:30"。</summary>
    public string DurationText { get => _durationText; set => SetProperty(ref _durationText, value); }

    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    /// <summary>エラーのとき stderr の末尾を含む文言。null ならエラーなし。</summary>
    public string? ErrorText { get => _errorText; set => SetProperty(ref _errorText, value); }
    public string LogText { get => _logText; set => SetProperty(ref _logText, value); }
    public bool IsLogOpen { get => _isLogOpen; set => SetProperty(ref _isLogOpen, value); }
    /// <summary>例: "常駐" / "都度起動"。</summary>
    public string BackendModeText { get => _backendModeText; set => SetProperty(ref _backendModeText, value); }
    /// <summary>例: "0.48秒"。</summary>
    public string ElapsedText { get => _elapsedText; set => SetProperty(ref _elapsedText, value); }
    /// <summary>例: "1440x1080 (SAR 4:3) 29.97fps"。</summary>
    public string InputInfoText { get => _inputInfoText; set => SetProperty(ref _inputInfoText, value); }

    public ICommand BrowseEncoderCommand { get; }
    public ICommand BrowseInputCommand { get; }
    public ICommand AddFilterLineCommand { get; }
    /// <summary>引数: 削除する FilterLineViewModel。</summary>
    public ICommand RemoveFilterLineCommand { get; }
    /// <summary>引数: -1 / +1 (前後のフレーム)。</summary>
    public ICommand StepFrameCommand { get; }
    /// <summary>引数: 移動する秒数 (double)。</summary>
    public ICommand StepSecondsCommand { get; }
    public ICommand GoToStartCommand { get; }
    public ICommand GoToEndCommand { get; }
    public ICommand CommitTimeTextCommand { get; }
    /// <summary>キャッシュを使わずに再実行 (F5)。</summary>
    public ICommand RefreshCommand { get; }
    public ICommand SaveFiltersCommand { get; }
    public ICommand LoadFiltersCommand { get; }
    public ICommand CopyCommandLineCommand { get; }
    public ICommand ToggleLogCommand { get; }

    /// <summary>シークバーのドラッグ開始。ドラッグ中は要求を出さない。</summary>
    public void BeginSeekDrag() { }

    /// <summary>シークバーのドラッグ終了。その位置で要求を出す。</summary>
    public void EndSeekDrag(double seconds) => CurrentSeconds = seconds;

    /// <summary>デザイン確認用のダミー値を入れた ViewModel (スクリーンショット・デザイナー用)。</summary>
    public static MainWindowViewModel CreateDesignSample()
    {
        var vm = new MainWindowViewModel
        {
            EncoderPath = @"C:\Encoders\QSVEncC64.exe",
            EncoderDescription = "QSVEnc 8.35",
            InputPath = @"X:\Encoders\narnia.ts",
            CurrentTimeText = "1:29:58.000",
            CurrentSeconds = 5398,
            DurationSeconds = 6750,
            HasDuration = true,
            DurationText = "1:52:30",
            BackendModeText = "常駐",
            ElapsedText = "0.48秒",
            InputInfoText = "1440x1080 (SAR 4:3)  29.97fps",
            StatusText = "完了",
            DisplayWidth = 1920,
            DisplayHeight = 1080,
            HelpText =
                "   --vpp-nlmeans [<param1>=<value>][,<param2>=<value>][...]\n" +
                "     enable denoise filter by non-local means.\n" +
                "    params\n" +
                "      sigma=<float>  sigma (default=0.005, 0.0 -)\n" +
                "      h=<float>      h (default=0.050, 0.0 <)\n" +
                "      patch=<int>    patch size (default=5, 3-)\n" +
                "      search=<int>   search size (default=11, 3-)\n" +
                "      fp16=<string>  select fp16 usage.\n" +
                "                       none, blockdiff (default), all\n" +
                "      d=<int>        temporal radius (default=0, 0-2)\n",
        };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.005,h=0.05" });
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-edgelevel strength=10", IsEnabled = false });
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-deband" });
        vm.SelectedFilterLine = vm.FilterLines[0];
        return vm;
    }
}

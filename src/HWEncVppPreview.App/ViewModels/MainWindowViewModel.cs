using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.Core;
using static HWEncVppPreview.Core.EncoderPreviewSession;

namespace HWEncVppPreview.App.ViewModels;

/// <summary>画面の操作をCoreへ接続する。Viewと合意したプロパティ名・コマンド名を維持する。</summary>
/// <remarks>Coreの通知ではUIの完了を待たず、PostでUIスレッドへ戻す。利用終了時はDisposeAsyncを呼ぶ。</remarks>
public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable, IEncoderPreviewHost
{
    private string _encoderPath = "", _inputPath = "", _encoderDescription = "", _inputArgsText = "";
    private bool _isEncoderPathAutomatic;
    private FilterLineViewModel? _selectedFilterLine;
    private string _helpText = "";
    private Bitmap? _previewImage;
    private double _displayWidth = 1920, _displayHeight = 1080;
    private string _currentTimeText = "0:00:00.000";
    private bool _isBusy, _isLogOpen;
    private string _durationText = "", _statusText = "", _logText = "", _elapsedText = "", _inputInfoText = "";
    private string? _errorText;
    private readonly Action<Action> _postToUi;
    private readonly IViewModelServices _services;
    private readonly TimeProvider _clock;
    private readonly PreviewTimeouts _timeouts;
    private readonly EncoderPreviewSession _session;
    private readonly TimeSpan _inputEditDelay;
    private readonly Func<Y4mFrame, HdrDisplaySettings, CancellationToken, Task<byte[]>>? _imageConverter;
    private HelpIndex _helpIndex = new("");
    private HelpSchema? _helpSchema;
    private readonly HashSet<FilterLineViewModel> _observedLines = new();

    public MainWindowViewModel(IViewModelServices? services = null, TimeProvider? timeProvider = null,
        IPreviewBackend? backend = null, InputProbe? probe = null, FrameCache? cache = null)
        : this(services, timeProvider, backend, probe, cache, null) { }

    /// <summary>試験用に画像変換、時計と期限を差し替える内部入口。</summary>
    internal MainWindowViewModel(IViewModelServices? services, TimeProvider? timeProvider,
        IPreviewBackend? backend, InputProbe? probe, FrameCache? cache,
        Func<Y4mFrame, CancellationToken, Task<byte[]>>? converter,
        IPreviewBackend? backgroundBackend = null, bool beforeEnabled = true,
        OwnedPreviewDependencies? owned = null, TimeSpan? debounceOverride = null,
        Func<string, string, CancellationToken, Task<string>>? readTextOverride = null,
        PreviewTimeouts? timeouts = null)
    {
        if (owned != null)
        {
            ArgumentNullException.ThrowIfNull(backend);
            if (backend is not IRawOwnedBackend
                || (backgroundBackend != null && backgroundBackend is not IRawOwnedBackend))
                throw new ArgumentException("所有権付きの画像を扱えるバックエンドが必要です。", nameof(owned));
            if (cache != null && !ReferenceEquals(cache.OwnershipScope, owned.Scope))
                throw new ArgumentException("キャッシュと画像の所有範囲が一致しません。", nameof(cache));
        }
        _postToUi = owned?.PostToUi ?? (action => Dispatcher.UIThread.Post(action));
        _bgraPool = owned?.BgraPool ?? new BgraBufferPool();
        // rawを外へ渡し得る注入が1つでもあれば、VM全体を通常配列の経路へ固定する。
        bool usesOwnedRaw = owned != null || (backend == null && backgroundBackend == null
            && cache == null && converter == null && probe == null);
        _imageConverter = converter != null ? (frame, settings, token) => converter(frame, token) : null;
        _services = services ?? new AvaloniaViewModelServices();
        _clock = timeProvider ?? TimeProvider.System;
        _timeouts = timeouts ?? PreviewTimeouts.Default;
        _inputEditDelay = debounceOverride ?? TimeSpan.FromMilliseconds(150);
        // 同じ長さなら読取が全体を上書きし、所有権が貸出中の画素を守るため、入力世代や形式では分けない。
        var rawScope = owned?.Scope ?? (usesOwnedRaw ? new RawOwnershipScope(new RawBufferPool()) : null);
        var frameCache = cache ?? (rawScope != null ? new FrameCache(rawScope) : new FrameCache());
        _session = new EncoderPreviewSession(_clock, _timeouts, backend, backgroundBackend, frameCache, rawScope,
            probe, debounceOverride, readTextOverride, this, _postToUi, beforeEnabled);
        FilterLines.CollectionChanged += LinesChanged;
        BrowseEncoderCommand = new RelayCommand(() => RunCommand(async () => { string? path = await _services.BrowseEncoderAsync(); if (path != null) OpenEncoder(path); }));
        BrowseInputCommand = new RelayCommand(() => RunCommand(async () => { string? path = await _services.BrowseInputAsync(); if (path != null) OpenInput(path); }));
        AddFilterLineCommand = new RelayCommand(() => { var line = new FilterLineViewModel(); FilterLines.Add(line); SelectedFilterLine = line; });
        AddFilterCommand = new RelayCommand(parameter =>
        {
            if (parameter is not string optionName || HelpSchema?.Lookup(optionName) is null) return;
            var line = new FilterLineViewModel { Text = optionName };
            FilterLines.Add(line);
            SelectedFilterLine = line;
        });
        RemoveFilterLineCommand = new RelayCommand(p => { if (p is FilterLineViewModel line) { FilterLines.Remove(line); if (ReferenceEquals(SelectedFilterLine, line)) SelectedFilterLine = FilterLines.FirstOrDefault(); } });
        StepFrameCommand = new RelayCommand(p => _session.StepFrame((int)Number(p)));
        StepSecondsCommand = new RelayCommand(p => _session.SeekTo(CurrentSeconds + Number(p), operation: NavigationOperation.Page));
        GoToStartCommand = new RelayCommand(() => _session.SeekTo(0));
        GoToEndCommand = new RelayCommand(() => { if (HasDuration) _session.SeekTo(DurationSeconds); });
        CommitTimeTextCommand = new RelayCommand(CommitTime);
        RefreshCommand = new RelayCommand(() => _ = _session.Reload());
        SaveFiltersCommand = new RelayCommand(() => RunCommand(SaveFiltersAsync));
        LoadFiltersCommand = new RelayCommand(() => RunCommand(LoadFiltersAsync));
        CopyCommandLineCommand = new RelayCommand(() => RunCommand(() => _services.CopyTextAsync(EnabledOptionsText())));
        ToggleLogCommand = new RelayCommand(() => IsLogOpen = !IsLogOpen);
        UiLanguage.Changed += OnLanguageChanged;
        SetCompareModeCommand = new RelayCommand(parameter =>
        {
            if (parameter is PreviewCompareMode mode) CompareMode = mode;
        });
    }

    // 画素の所有経路を保ったまま、試験で実行と画面配送の境界を制御する。
    internal sealed record OwnedPreviewDependencies(RawOwnershipScope Scope, Action<Action> PostToUi,
        BgraBufferPool? BgraPool = null);

    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;

    /// <summary>設定を復元して入力検査とSubmitまで行う。debounce完了や描画の完了はWaitForIdleAsyncで待つ。</summary>
    public async Task InitializeAsync(bool restoreSettings = true)
    {
        ObjectDisposedException.ThrowIf(_session.IsDisposed, this);
        if (_session.IsInitialized) return;
        if (restoreSettings)
        {
            var settings = AppSettings.Load(_services.SettingsPath);
            _session.IsSuspended = true;
            Language = settings.Language;
            HdrReferenceWhiteNits = settings.HdrReferenceWhiteNits;
            EncoderPath = settings.EncoderPath;
            InputPath = settings.InputPath;
            InputArgsText = settings.InputArgsText;
            _session.RestoreTime(settings.CurrentSeconds);
            CurrentTimeText = FormatTime(CurrentSeconds);
            WindowWidth = settings.WindowWidth;
            IsTextListMode = settings.IsTextListMode;
            WindowHeight = settings.WindowHeight;
            FilterLines.Clear();
            foreach (var line in settings.FilterLines) FilterLines.Add(new FilterLineViewModel { Text = line.Text, IsEnabled = line.IsEnabled });
            _session.IsSuspended = false;
            OnPropertyChanged(nameof(CurrentSeconds));
        }
        if (EncoderPath.Length == 0 && _services.FindEncoder() is { Length: > 0 } automaticPath)
            SetEncoderPath(automaticPath, automatic: true);
        _session.IsInitialized = true;
        await _session.QueuePreview(bypassDebounce: true, validateRestoredTime: restoreSettings);
    }

    /// <summary>通常要求の入力検査とSubmit登録を待つ。パス文字編集中は待機タイマー登録までを同期点とし、時計を進めた後の完了はWaitForIdleAsyncで待つ。</summary>
    public Task WaitForPreparationAsync() => _inputEditCancellation != null ? Task.CompletedTask : _session.Preparation;

    /// <summary>入力検査、エンコード、変換、UIへPostした反映をすべて待つ。</summary>
    public Task WaitForIdleAsync() => _session.WaitForIdleAsync();

    public async Task SaveSettingsAsync(double width = 1440, double height = 900)
    {
        WindowWidth = width;
        WindowHeight = height;
        var settings = new AppSettings
        {
            // 自動取得した経路は持ち運べる設定ではないため、次回起動時に探し直す。
            EncoderPath = _isEncoderPathAutomatic ? "" : EncoderPath, InputPath = InputPath, InputArgsText = InputArgsText,
            FilterLines = FilterLines.Select(line => new FilterSetting(line.Text, line.IsEnabled)).ToArray(),
            WindowWidth = width, WindowHeight = height, CurrentSeconds = CurrentSeconds, IsTextListMode = IsTextListMode, Language = Language, HdrReferenceWhiteNits = HdrReferenceWhiteNits,
        };
        try { await Task.Run(() => settings.Save(_services.SettingsPath)).ConfigureAwait(false); }
        catch (Exception exception) { await _session.PostAsync(() => ShowExceptionError(AppMessages.SaveSettingsFailed, "", exception)).ConfigureAwait(false); }
    }

    public string EncoderPath { get => _encoderPath; set => SetEncoderPath(value ?? "", automatic: false); }
    /// <summary>自動で選んだ経路か。表示は使える経路を保ち、保存は利用者の明示指定だけにする。</summary>
    public bool IsEncoderPathAutomatic => _isEncoderPathAutomatic;

    private void SetEncoderPath(string value, bool automatic)
    {
        // 同じ経路を利用者が選び直した場合も、明示指定として保存できるようにする。
        if (_isEncoderPathAutomatic != automatic)
        {
            _isEncoderPathAutomatic = automatic;
            OnPropertyChanged(nameof(IsEncoderPathAutomatic));
            OnPropertyChanged(nameof(EncoderDescription));
        }
        if (SetProperty(ref _encoderPath, value, nameof(EncoderPath)))
        {
            _session.CancelEncoderInitializationTracked();
            EncoderDescription = "";
            _session.ClearMetadata();
            _helpIndex = new HelpIndex("");
            HelpSchema = null;
            HelpText = "";
            InputChanged();
        }
    }
    public string InputPath { get => _inputPath; set { if (SetProperty(ref _inputPath, value ?? "")) InputChanged(); } }
    /// <summary>例: "QSVEnc 8.35"。</summary>
    public string EncoderDescription
    {
        get => _isEncoderPathAutomatic
            ? (_encoderDescription.Length == 0 ? AppMessages.AutomaticallySelected : _encoderDescription + " (" + AppMessages.AutomaticallySelected + ")")
            : _encoderDescription;
        set => SetProperty(ref _encoderDescription, value);
    }
    public string InputArgsText { get => _inputArgsText; set { if (SetProperty(ref _inputArgsText, value ?? "")) InputChanged(); } }

    public ObservableCollection<FilterLineViewModel> FilterLines { get; } = new();
    /// <summary>フォーカスのある行。View の code-behind が設定する。ヘルプ表示の対象。</summary>
    public FilterLineViewModel? SelectedFilterLine { get => _selectedFilterLine; set { if (SetProperty(ref _selectedFilterLine, value)) UpdateHelp(); } }
    private bool _isTextListMode;
    public bool IsTextListMode { get => _isTextListMode; set => SetProperty(ref _isTextListMode, value); }
    public ObservableCollection<FilterCatalogItem> FilterCatalog { get; } = new();
    public HelpSchema? HelpSchema
    {
        get => _helpSchema;
        private set
        {
            if (!SetProperty(ref _helpSchema, value)) return;
            foreach (var line in FilterLines) line.HelpSchema = value;
            RefreshFilterCatalog();
        }
    }
    private void RefreshFilterCatalog()
    {
        FilterCatalog.Clear();
        string[] categoryOrder = ["deinterlace", "denoise", "edge", "deband", "color", "resize", "framerate", "overlay", "ai", "other"];
        // 「その他・設定」(フィルタでない設定や、単体では使わない部品) は追加の一覧に出さない (2026-11-03 部長判断)。
        // テキストでの入力と補完では今までどおり使える
        foreach (var option in (HelpSchema?.Options ?? []).Where(option => option.Category != "other").OrderBy(option =>
            {
                int order = Array.IndexOf(categoryOrder, option.Category);
                return order < 0 ? categoryOrder.Length : order;
            }).ThenBy(option => option.Name, StringComparer.Ordinal))
            FilterCatalog.Add(new(option.Name, option.Name.StartsWith("--vpp-", StringComparison.Ordinal) ? option.Name[6..] : option.Name,
                HelpCategories.DisplayName(option.Category), option.Description.Split('\n')[0]));
        UpdateCatalogUsage();
    }
    private void UpdateCatalogUsage()
    {
        foreach (var item in FilterCatalog) item.IsInUse = FilterLines.Any(line => line.OptionName.Equals(item.OptionName, StringComparison.OrdinalIgnoreCase));
    }
    public string HelpText { get => _helpText; set => SetProperty(ref _helpText, value); }

    /// <summary>表示中の画像 (BGRA)。処理中も直前の画像を保持する。</summary>
    public Bitmap? PreviewImage
    {
        get => _previewImage;
        set
        {
            var previous = _previewImage;
            if (!SetProperty(ref _previewImage, value)) return;
            previous?.Dispose();
            if (value == null) _session.ClearAfterPresentation();
        }
    }
    /// <summary>SAR を考慮した表示上の幅・高さ (画素)。View はこの比率で拡大縮小して表示する。</summary>
    public double DisplayWidth { get => _displayWidth; set => SetProperty(ref _displayWidth, value); }
    public double DisplayHeight { get => _displayHeight; set => SetProperty(ref _displayHeight, value); }

    /// <summary>時刻欄 (双方向)。Enter で CommitTimeTextCommand。</summary>
    public string CurrentTimeText { get => _currentTimeText; set => SetProperty(ref _currentTimeText, value ?? ""); }
    /// <summary>シークバーの目標位置(秒)。ドラッグ中も移動要求を送り、待ちは最新位置へ置き換える。</summary>
    public double CurrentSeconds { get => _session.CurrentSeconds; set => _session.SliderChanged(value); }
    /// <summary>入力の長さ (秒)。HasDuration が false のときは意味を持たない。</summary>
    public double DurationSeconds { get => _session.DurationSeconds; set => _session.DurationSeconds = value; }
    /// <summary>入力の長さが分かっているか。false ならシークバーを隠す (設計書 17.1)。</summary>
    public bool HasDuration { get => _session.HasDuration; set => _session.HasDuration = value; }
    /// <summary>例: "1:52:30"。</summary>
    public string DurationText { get => _durationText; set => SetProperty(ref _durationText, value); }

    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    /// <summary>エラーのとき stderr の末尾を含む文言。null ならエラーなし。</summary>
    public string? ErrorText { get => _errorText; set => SetProperty(ref _errorText, value); }
    public string LogText { get => _logText; set => SetProperty(ref _logText, value); }
    public bool IsLogOpen { get => _isLogOpen; set => SetProperty(ref _isLogOpen, value); }
    /// <summary>例: "0.48秒"。</summary>
    public string ElapsedText { get => _elapsedText; set => SetProperty(ref _elapsedText, value); }
    /// <summary>例: "1440x1080 (SAR 4:3) 29.97fps"。</summary>
    public string InputInfoText { get => _inputInfoText; set => SetProperty(ref _inputInfoText, value); }

    public ICommand BrowseEncoderCommand { get; }
    public ICommand BrowseInputCommand { get; }
    public ICommand AddFilterLineCommand { get; }
    public ICommand AddFilterCommand { get; }
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


    public void BeginSeekDrag() => _session.BeginSeekDrag();

    public void EndSeekDrag(double seconds) => _session.EndSeekDrag(seconds);

    private void InputChanged(bool bypassDebounce = false)
    {
        _bgraPool.Retire();
        bool suspended = _session.IsSuspended;
        _session.IsSuspended = true;
        try
        {
            _session.ClearInputInfo();
            _session.ClearInterval();
        }
        finally { _session.IsSuspended = suspended; }
        if (!_session.IsInitialized || _session.IsSuspended || _session.IsDisposed) return;
        // 入力切替時の未使用配列は片付ける。貸出中の配列は最後の所有解放まで変更しない。
        _session.DiscardInputRequests();
        if (bypassDebounce) { CancelInputEdit(); _ = _session.QueuePreview(bypassDebounce: true); }
        else ScheduleInputEdit();
    }
    private void LinesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var line in _observedLines.Where(line => !FilterLines.Contains(line)).ToArray())
        { line.PropertyChanged -= LineChanged; _observedLines.Remove(line); }
        foreach (var line in FilterLines.Where(line => !_observedLines.Contains(line)))
        { line.HelpSchema = HelpSchema; line.PropertyChanged += LineChanged; _observedLines.Add(line); }
        UpdateCatalogUsage();
        FiltersChanged(bypassDebounce: true);
    }
    private void LineChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(FilterLineViewModel.Text) or nameof(FilterLineViewModel.IsEnabled))
        { UpdateCatalogUsage(); FiltersChanged(bypassDebounce: args.PropertyName == nameof(FilterLineViewModel.IsEnabled)); }
        if (ReferenceEquals(sender, SelectedFilterLine)) UpdateHelp();
    }
    private void FiltersChanged(bool bypassDebounce = false) => _ = _session.FiltersChanged(bypassDebounce);
    private void UpdateHelp()
    {
        if (_session.IsInitialized || _helpIndex.OptionNames.Count > 0) HelpText = _helpIndex.Lookup(SelectedFilterLine?.OptionName ?? "");
    }
    private void ShowError(string message, string diagnostics) => ShowErrorCore(message, diagnostics, null);

    private void ShowExceptionError(string message, string diagnostics, Exception? exception) => ShowErrorCore(message, diagnostics, exception);

    private void ShowErrorCore(string message, string diagnostics, Exception? exception)
    {
        if (PerfTrace.Enabled) PerfTrace.Write("vm.error", new { message, diagnostics, exception = exception?.ToString() });
        IsBusy = false;
        StatusText = AppMessages.Error;
        LogText = _session.AppendBeforeFallbackDiagnostic(AppendExceptionDetails(diagnostics, exception));
        string[] lines = diagnostics.Split('\n');
        string tail = string.Join(Environment.NewLine, lines.TakeLast(6));
        // readerの後片付けで末尾が埋まっても、長いkey間隔による窓の保持上限や探索上限の理由を隠さない。
        // 表示する診断の選択だけで、窓の不足判定や回復の可否には使わない。
        string? windowError = lines.FirstOrDefault(line => line.Contains("preview window keyframe buffer exceeds ", StringComparison.Ordinal)
            || line.Contains("preview window keyframe not found within ", StringComparison.Ordinal));
        if (windowError != null && !tail.Contains(windowError, StringComparison.Ordinal))
            tail = windowError.TrimEnd('\r') + Environment.NewLine + tail;
        // stderrの末尾選択とは分けて、深い原因チェーンも全段を表示する。
        tail = AppendExceptionDetails(tail, exception);
        ErrorText = string.IsNullOrWhiteSpace(tail) ? message : message + Environment.NewLine + tail;
    }

    private void CommitTime()
    {
        if (!TryParseTime(CurrentTimeText, out double seconds)) { ShowError(AppMessages.InvalidTime, ""); return; }
        _session.SeekTo(seconds, force: true, allowConcurrentBefore: true);
    }
    private static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < 0 || (i > 0 && value >= 60)) return false;
            if (i < parts.Length - 1 && value != Math.Floor(value)) return false;
            seconds = seconds * 60 + value;
        }
        return seconds <= TimeSpan.MaxValue.TotalSeconds;
    }
    private static string FormatTime(double seconds, bool milliseconds = true)
    {
        seconds = Math.Clamp(seconds, 0, TimeSpan.MaxValue.TotalSeconds - 1);
        long ms = (long)Math.Round(seconds * 1000);
        return milliseconds ? $"{ms / 3600000}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}" : $"{ms / 3600000}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}";
    }
    private static double Number(object? parameter) => parameter switch { null => 0, double number => number, int number => number,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed, _ => 0 };
    private string EnabledOptionsText() => string.Join(" ", FilterLines.Where(line => line.IsEnabled && OptionTextParser.TokenizeLine(line.Text).Count > 0).Select(line => line.Text.Trim()));
    private async Task SaveFiltersAsync()
    {
        string? path = await _services.ChooseSaveFiltersAsync();
        if (path == null) return;
        string text = string.Join(Environment.NewLine, FilterLines.Select(line => line.IsEnabled ? line.Text : "# " + line.Text));
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
    }
    private async Task LoadFiltersAsync()
    {
        string? path = await _services.ChooseLoadFiltersAsync();
        if (path == null) return;
        string text = await File.ReadAllTextAsync(path);
        _session.IsSuspended = true;
        try
        {
            FilterLines.Clear();
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    string candidate = trimmed[1..];
                    if (candidate.StartsWith(' ')) candidate = candidate[1..];
                    string? option = OptionTextParser.TokenizeLine(candidate).FirstOrDefault();
                    if (option is { Length: > 2 } && option.StartsWith("--", StringComparison.Ordinal) && char.IsAsciiLetterOrDigit(option[2]))
                    { FilterLines.Add(new FilterLineViewModel { Text = candidate, IsEnabled = false }); continue; }
                }
                FilterLines.Add(new FilterLineViewModel { Text = line });
            }
            SelectedFilterLine = FilterLines.FirstOrDefault();
        }
        finally { _session.IsSuspended = false; }
        _ = _session.QueuePreview(bypassDebounce: true);
    }
    private void RunCommand(Func<Task> command)
    {
        _session.Track(ExecuteAsync());
        async Task ExecuteAsync()
        {
            try { await command(); }
            catch (Exception exception) { await _session.PostAsync(() => { if (!_session.IsDisposed) ShowExceptionError(AppMessages.OperationFailed, "", exception); }); }
        }
    }

    private static string DescribeEncoder(string version, string encoder)
    {
        string first = version.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? Path.GetFileName(encoder);
        var match = Regex.Match(first, @"\b(NVEncC?|QSVEncC?|VCEEncC?|rkmppenc)\b.*?\b(\d+\.\d+(?:\.\d+)?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (match.Success)
        {
            string name = match.Groups[1].Value;
            if (name.Equals("rkmppenc", StringComparison.OrdinalIgnoreCase)) name = "rkmppenc";
            else if (name.EndsWith("C", StringComparison.OrdinalIgnoreCase)) name = name[..^1];
            return name + " " + match.Groups[2].Value;
        }
        return first.Length > 48 ? first[..48] + "…" : first;
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

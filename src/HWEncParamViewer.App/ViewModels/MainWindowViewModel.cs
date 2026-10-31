using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

/// <summary>画面の操作をCoreへ接続する。Viewと合意したプロパティ名・コマンド名を維持する。</summary>
/// <remarks>Coreの通知ではUIの完了を待たず、PostでUIスレッドへ戻す。利用終了時はDisposeAsyncを呼ぶ。</remarks>
public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private string _encoderPath = "", _inputPath = "", _encoderDescription = "", _inputArgsText = "--avhw";
    private FilterLineViewModel? _selectedFilterLine;
    private string _helpText = "";
    private Bitmap? _previewImage;
    private double _displayWidth = 1920, _displayHeight = 1080;
    private string _currentTimeText = "0:00:00.000";
    private double _currentSeconds, _durationSeconds;
    private bool _hasDuration, _isBusy, _isLogOpen;
    private string _durationText = "", _statusText = "", _logText = "", _backendModeText = "", _elapsedText = "", _inputInfoText = "";
    private string? _errorText;
    private readonly IViewModelServices _services;
    private readonly TimeProvider _clock;
    private readonly InputProbe _probe;
    private readonly FrameCache _cache;
    private readonly PreviewController _controller;
    private readonly Func<Y4mFrame, CancellationToken, Task<byte[]>> _imageConverter;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _taskLock = new();
    private readonly HashSet<Task> _tasks = new();
    private CancellationTokenSource? _preparationCancellation;
    private Task _preparation = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _initialized, _disposed, _suspendChanges, _seekDragging;
    private long _generation;
    private RequestContext? _context;
    private InputInfo? _inputInfo;
    private HelpIndex _helpIndex = new("");
    private HelpSchema? _helpSchema;
    private string _metadataEncoder = "";
    private PreviewRequest? _intervalRequest;
    private IReadOnlyList<Y4mFrame> _intervalFrames = Array.Empty<Y4mFrame>();
    private Y4mFrame? _displayedFrame;
    private readonly HashSet<FilterLineViewModel> _observedLines = new();

    public MainWindowViewModel(IViewModelServices? services = null, TimeProvider? timeProvider = null,
        IPreviewBackend? preferred = null, IPreviewBackend? fallback = null, InputProbe? probe = null, FrameCache? cache = null)
        : this(services, timeProvider, preferred, fallback, probe, cache, null) { }

    /// <summary>偽時計による描画待ちの再現用に、画像変換だけを差し替える内部入口。</summary>
    internal MainWindowViewModel(IViewModelServices? services, TimeProvider? timeProvider,
        IPreviewBackend? preferred, IPreviewBackend? fallback, InputProbe? probe, FrameCache? cache,
        Func<Y4mFrame, CancellationToken, Task<byte[]>>? converter)
    {
        _imageConverter = converter ?? ((frame, token) => Task.Run(() =>
        {
            // 外側のTask.Run待ちと、変換内の並列処理を含む待ちを計測時だけ区別する。
            if (PerfTrace.Enabled) PerfTrace.Write("vm.convert.worker.start", new { frame.Xts });
            try { return YuvToBgra.Convert(frame); }
            finally { if (PerfTrace.Enabled) PerfTrace.Write("vm.convert.worker.end", new { frame.Xts }); }
        }, token));
        _services = services ?? new AvaloniaViewModelServices();
        _clock = timeProvider ?? TimeProvider.System;
        _probe = probe ?? new InputProbe(_clock);
        _cache = cache ?? new FrameCache();
        var first = preferred ?? new ServerBackend(_clock);
        var second = fallback ?? new OneShotBackend();
        var observedFirst = new ObservedBackend(first, first is OneShotBackend ? "都度起動" : "常駐", this);
        _controller = new PreviewController(observedFirst,
            ReferenceEquals(first, second) ? observedFirst : new ObservedBackend(second, "都度起動", this), _cache, _clock);
        _controller.FrameReady += OnFrameReady;
        _controller.Completed += OnCompleted;
        _controller.Failed += OnFailed;
        FilterLines.CollectionChanged += LinesChanged;
        BrowseEncoderCommand = new RelayCommand(() => RunCommand(async () => { string? path = await _services.BrowseEncoderAsync(); if (path != null) EncoderPath = path; }));
        BrowseInputCommand = new RelayCommand(() => RunCommand(async () => { string? path = await _services.BrowseInputAsync(); if (path != null) InputPath = path; }));
        AddFilterLineCommand = new RelayCommand(() => { var line = new FilterLineViewModel(); FilterLines.Add(line); SelectedFilterLine = line; });
        RemoveFilterLineCommand = new RelayCommand(p => { if (p is FilterLineViewModel line) { FilterLines.Remove(line); if (ReferenceEquals(SelectedFilterLine, line)) SelectedFilterLine = FilterLines.FirstOrDefault(); } });
        StepFrameCommand = new RelayCommand(p => StepFrame((int)Number(p)));
        StepSecondsCommand = new RelayCommand(p => SeekTo(CurrentSeconds + Number(p)));
        GoToStartCommand = new RelayCommand(() => SeekTo(0));
        GoToEndCommand = new RelayCommand(() => { if (HasDuration) SeekTo(DurationSeconds); });
        CommitTimeTextCommand = new RelayCommand(CommitTime);
        RefreshCommand = new RelayCommand(() => { _cache.Clear(); ClearInterval(); ClearBeforeInterval(); _ = QueuePreview(bypassCache: true); });
        SaveFiltersCommand = new RelayCommand(() => RunCommand(SaveFiltersAsync));
        LoadFiltersCommand = new RelayCommand(() => RunCommand(LoadFiltersAsync));
        CopyCommandLineCommand = new RelayCommand(() => RunCommand(() => _services.CopyTextAsync(EnabledOptionsText())));
        ToggleLogCommand = new RelayCommand(() => IsLogOpen = !IsLogOpen);
        SetCompareModeCommand = new RelayCommand(parameter =>
        {
            if (parameter is PreviewCompareMode mode) CompareMode = mode;
        });
    }

    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;

    /// <summary>設定を復元して入力検査とSubmitまで行う。debounce完了や描画の完了はWaitForIdleAsyncで待つ。</summary>
    public async Task InitializeAsync(bool restoreSettings = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;
        if (restoreSettings)
        {
            var settings = AppSettings.Load(_services.SettingsPath);
            _suspendChanges = true;
            EncoderPath = settings.EncoderPath;
            InputPath = settings.InputPath;
            InputArgsText = settings.InputArgsText;
            _currentSeconds = settings.CurrentSeconds;
            CurrentTimeText = FormatTime(_currentSeconds);
            WindowWidth = settings.WindowWidth;
            WindowHeight = settings.WindowHeight;
            FilterLines.Clear();
            foreach (var line in settings.FilterLines) FilterLines.Add(new FilterLineViewModel { Text = line.Text, IsEnabled = line.IsEnabled });
            _suspendChanges = false;
            OnPropertyChanged(nameof(CurrentSeconds));
        }
        _initialized = true;
        await QueuePreview();
    }

    /// <summary>現在の要求について入力検査とSubmit登録が完了するまで待つ。テストで偽時計を進める前の同期点にもなる。</summary>
    public Task WaitForPreparationAsync() => _preparation;

    /// <summary>入力検査、エンコード、変換、UIへPostした反映をすべて待つ。</summary>
    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_taskLock) tasks = _tasks.ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            await _controller.WaitForIdleAsync().ConfigureAwait(false);
            await PostAsync(() => { }).ConfigureAwait(false);
            lock (_taskLock) { if (_tasks.Count == 0) return; }
        }
    }

    public async Task SaveSettingsAsync(double width = 1440, double height = 900)
    {
        WindowWidth = width;
        WindowHeight = height;
        var settings = new AppSettings
        {
            EncoderPath = EncoderPath, InputPath = InputPath, InputArgsText = InputArgsText,
            FilterLines = FilterLines.Select(line => new FilterSetting(line.Text, line.IsEnabled)).ToArray(),
            WindowWidth = width, WindowHeight = height, CurrentSeconds = CurrentSeconds,
        };
        try { await Task.Run(() => settings.Save(_services.SettingsPath)).ConfigureAwait(false); }
        catch (Exception exception) { await PostAsync(() => ShowError("設定を保存できませんでした", exception.Message)).ConfigureAwait(false); }
    }

    public string EncoderPath { get => _encoderPath; set { if (SetProperty(ref _encoderPath, value ?? "")) { EncoderDescription = ""; _metadataEncoder = ""; _helpIndex = new HelpIndex(""); HelpSchema = null; HelpText = ""; InputChanged(); } } }
    public string InputPath { get => _inputPath; set { if (SetProperty(ref _inputPath, value ?? "")) InputChanged(); } }
    /// <summary>例: "QSVEnc 8.35"。</summary>
    public string EncoderDescription { get => _encoderDescription; set => SetProperty(ref _encoderDescription, value); }
    public string InputArgsText { get => _inputArgsText; set { if (SetProperty(ref _inputArgsText, value ?? "")) InputChanged(); } }

    public ObservableCollection<FilterLineViewModel> FilterLines { get; } = new();
    /// <summary>フォーカスのある行。View の code-behind が設定する。ヘルプ表示の対象。</summary>
    public FilterLineViewModel? SelectedFilterLine { get => _selectedFilterLine; set { if (SetProperty(ref _selectedFilterLine, value)) UpdateHelp(); } }
    public HelpSchema? HelpSchema { get => _helpSchema; private set => SetProperty(ref _helpSchema, value); }
    public string HelpText { get => _helpText; set => SetProperty(ref _helpText, value); }

    /// <summary>表示中の画像 (BGRA)。処理中も直前の画像を保持する。</summary>
    public Bitmap? PreviewImage { get => _previewImage; set { var previous = _previewImage; if (SetProperty(ref _previewImage, value)) previous?.Dispose(); } }
    /// <summary>SAR を考慮した表示上の幅・高さ (画素)。View はこの比率で拡大縮小して表示する。</summary>
    public double DisplayWidth { get => _displayWidth; set => SetProperty(ref _displayWidth, value); }
    public double DisplayHeight { get => _displayHeight; set => SetProperty(ref _displayHeight, value); }

    /// <summary>時刻欄 (双方向)。Enter で CommitTimeTextCommand。</summary>
    public string CurrentTimeText { get => _currentTimeText; set => SetProperty(ref _currentTimeText, value ?? ""); }
    /// <summary>シークバーの目標位置(秒)。ドラッグ中も移動要求を送り、待ちは最新位置へ置き換える。</summary>
    public double CurrentSeconds { get => _currentSeconds; set { if (SetProperty(ref _currentSeconds, ClampTime(value))) { CurrentTimeText = FormatTime(_currentSeconds); _ = QueuePreview(navigation: true); } } }
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


    public void BeginSeekDrag() => _seekDragging = true;
    public void EndSeekDrag(double seconds)
    {
        if (!_seekDragging) return;
        _seekDragging = false;
        SeekTo(seconds);
    }

    private void InputChanged()
    {
        CancelDuration();
        _inputInfo = null;
        HasDuration = false;
        DurationSeconds = 0;
        DurationText = "";
        ClearInterval();
        _ = QueuePreview();
    }
    private void LinesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var line in _observedLines.Where(line => !FilterLines.Contains(line)).ToArray())
        { line.PropertyChanged -= LineChanged; _observedLines.Remove(line); }
        foreach (var line in FilterLines.Where(line => !_observedLines.Contains(line)))
        { line.PropertyChanged += LineChanged; _observedLines.Add(line); }
        FiltersChanged();
    }
    private void LineChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(FilterLineViewModel.Text) or nameof(FilterLineViewModel.IsEnabled)) FiltersChanged();
        if (ReferenceEquals(sender, SelectedFilterLine)) UpdateHelp();
    }
    private void FiltersChanged() { ClearInterval(); _ = QueuePreview(); }
    private void UpdateHelp()
    {
        if (_initialized || _helpIndex.OptionNames.Count > 0) HelpText = _helpIndex.Lookup(SelectedFilterLine?.OptionName ?? "");
    }
    private void ClearInterval() { _intervalFrames = Array.Empty<Y4mFrame>(); _intervalRequest = null; }

    private bool IsCurrent(long generation) => !_disposed && generation == Volatile.Read(ref _generation);
    private bool IsCurrent(RequestContext context) => context.Kind == RequestKind.Before
        ? IsCurrent(context.Generation) && ReferenceEquals(Volatile.Read(ref _context), context)
        : !_disposed && context.EditEpoch == Volatile.Read(ref _editEpoch) && context.Generation >= _lastDisplayedAfter;
    private RequestContext? ContextFor(PreviewRequest request)
    {
        if (_afterRequests.TryGetValue(request.CorrelationId, out var after) && SameRequest(after.Request, request)) return after;
        var context = Volatile.Read(ref _context);
        return context != null && SameRequest(context.Request, request) ? context : null;
    }
    private static bool SameRequest(PreviewRequest a, PreviewRequest b) => a.CorrelationId == b.CorrelationId && a.EncoderPath == b.EncoderPath && a.InputFile == b.InputFile
        && a.TargetSeconds == b.TargetSeconds && a.Fps == b.Fps && a.InputArgs.SequenceEqual(b.InputArgs) && a.VppArgs.SequenceEqual(b.VppArgs);

    private void OnFrameReady(PreviewFrameResult value)
    {
        var context = ContextFor(value.Request);
        if (context == null || context.NeighborDirection != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrent(context)) return;
            if (context.Kind == RequestKind.Before) DisplayBeforeFrame(value.Frame, context.Generation);
            else DisplayFrame(value.Frame, context);
        });
    }
    private void OnCompleted(PreviewCompletion value)
    {
        var context = ContextFor(value.Request);
        if (context == null) return;
        IReadOnlyList<Y4mFrame> frames = value.Frames;
        if (frames.Count == 0) _cache.TryGet(value.Request, out frames);
        if (context.Kind == RequestKind.After) _afterRequests.TryRemove(context.Generation, out _);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrent(context)) return;
            if (context.Kind == RequestKind.Before)
            {
                _beforeIntervalRequest = value.Request;
                _beforeIntervalFrames = frames;
                IsBeforeBusy = false;
                return;
            }
            if (_lastFailedAfter == Volatile.Read(ref _generation)) return;
            _intervalRequest = value.Request;
            _intervalFrames = frames;
            _lastCompletedAfter = Math.Max(_lastCompletedAfter, context.Generation);
            var afterRequest = context.NeighborDirection != 0 ? CompleteNeighbor(context, frames) : value.Request;
            if (frames.Count == 0 && context.Generation == Volatile.Read(ref _generation) && _lastQueuedAfter < context.Generation)
            { _lastFailedAfter = context.Generation; ShowError("表示できるフレームがありません", value.Result.StandardError); return; }
            ErrorText = null;
            LogText = value.Result.StandardError;
            if (!string.IsNullOrEmpty(context.Mode)) BackendModeText = context.Mode;
            ElapsedText = $"{_clock.GetElapsedTime(context.Started).TotalSeconds:0.00}秒";
            if (context.Generation == Volatile.Read(ref _generation))
            {
                if (afterRequest != null)
                    _completedAfterStatus = value.TargetNotReached ? "完了（目標時刻に届かなかったため最終フレームを表示）" : value.FromCache ? "完了（キャッシュ）" : "完了";
                QueueBefore(afterRequest ?? value.Request, context.Generation);
            }
            UpdateNavigationStatus();
        });
    }
    private void OnFailed(PreviewFailure value)
    {
        var context = ContextFor(value.Request);
        if (context == null) return;
        string diagnostics = value.Result?.StandardError ?? (value.Exception as PreviewBackendException)?.StandardError ?? context.Diagnostics;
        if (context.Kind == RequestKind.After) _afterRequests.TryRemove(context.Generation, out _);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrent(context)) return;
            if (context.Kind == RequestKind.Before)
            {
                IsBeforeBusy = false;
                Interlocked.Increment(ref _beforeImageGeneration);
                _beforeDisplayedFrame = null;
                BeforeImage = null;
                return;
            }
            if (context.Generation != Volatile.Read(ref _generation)) return;
            if (_pendingNeighbor?.Generation == context.Generation) _pendingNeighbor = null;
            _lastFailedAfter = context.Generation;
            if (!string.IsNullOrEmpty(context.Mode)) BackendModeText = context.Mode;
            ElapsedText = $"{_clock.GetElapsedTime(context.Started).TotalSeconds:0.00}秒";
            ShowError(value.Reason, diagnostics);
        });
    }
    private void DisplayFrame(Y4mFrame frame, RequestContext context)
    {
        if (context.Generation <= _lastQueuedAfter) return;
        _lastQueuedAfter = context.Generation;
        EnqueueImage(new ImageWork(frame, context.Generation, context.EditEpoch));
    }
    private void ShowError(string message, string diagnostics)
    {
        if (PerfTrace.Enabled) PerfTrace.Write("vm.error", new { message, diagnostics });
        IsBusy = false;
        StatusText = "エラー";
        LogText = diagnostics;
        string tail = string.Join(Environment.NewLine, diagnostics.Split('\n').TakeLast(6));
        ErrorText = string.IsNullOrWhiteSpace(tail) ? message : message + Environment.NewLine + tail;
    }

    private void StepFrame(int direction)
    {
        if (PerfTrace.Enabled) PerfTrace.Write("vm.step", new { direction, pending = _pendingNeighbor != null, target = CurrentSeconds });
        if (direction == 0 || _pendingNeighbor != null)
        {
            if (PerfTrace.Enabled) PerfTrace.Write("vm.step.drop", new { direction, pending = _pendingNeighbor != null });
            return;
        }
        direction = Math.Sign(direction);
        if (_intervalRequest is { } interval && _intervalFrames.Count > 0)
        {
            var plan = CommandBuilder.Plan(interval);
            double seek = plan.SeekSeconds ?? 0;
            double target = CurrentSeconds - seek;
            var first = _intervalFrames[0];
            var last = _intervalFrames[^1];
            double firstTime = first.Xts ?? 0;
            double lastTime = last.Xts ?? (_intervalFrames.Count - 1) / FrameSelector.FrameRate(last, interval.Fps);
            double tolerance = FrameSelector.Duration(last, interval.Fps) / 2;
            if (target >= firstTime - tolerance && target <= lastTime + tolerance)
            {
                int current = NeighborAnchorIndex(_intervalFrames, target, interval.Fps);
                int next = current + direction;
                if (current >= 0 && next >= 0 && next < _intervalFrames.Count)
                {
                    var frame = _intervalFrames[next];
                    double seconds = FrameSeconds(interval, frame, next);
                    if ((seconds - CurrentSeconds) * direction > 1e-9)
                    {
                        if (PerfTrace.Enabled) PerfTrace.Write("vm.step.cache", new { seconds, direction });
                        DisplayCached(frame, seconds); return;
                    }
                }
            }
        }
        // 取り直し中のキー反復は加算せず、実際の隣が届くまで目標を保つ。
        if (!_initialized)
        {
            // 入力を開く前の操作は、表示位置だけを仮に動かす。
            SeekTo(CurrentSeconds + direction / (_inputInfo?.Fps ?? 30));
            return;
        }
        if (PerfTrace.Enabled) PerfTrace.Write("vm.step.refetch", new { direction, target = CurrentSeconds });
        _ = QueuePreview(navigation: true, neighborDirection: direction);
    }
    private void DisplayCached(Y4mFrame frame, double seconds)
    {
        if (_intervalRequest == null) return;
        long generation = BeginIntent(navigation: true);
        SetTimeOnly(seconds);
        var request = RetargetInterval(_intervalRequest, seconds) with { CorrelationId = generation };
        var context = new RequestContext(generation, request, _clock.GetTimestamp(), editEpoch: _editEpoch);
        RegisterContext(context);
        IsBusy = true;
        ErrorText = null;
        _completedAfterStatus = "完了（キャッシュ）";
        DisplayFrame(frame, context);
        // 表示は即時に進め、Coreにも最新の移動意図を伝えて古い待ち要求を置き換える。
        _controller.Submit(request, navigation: true);
        Track(ReleaseContextAsync(context));
    }
    private void SeekTo(double seconds, bool force = false)
    {
        seconds = ClampTime(seconds);
        if (!force && seconds == CurrentSeconds) return;
        SetTimeOnly(seconds);
        _ = QueuePreview(navigation: true);
    }
    private void SetTimeOnly(double seconds)
    {
        _currentSeconds = seconds;
        OnPropertyChanged(nameof(CurrentSeconds));
        CurrentTimeText = FormatTime(seconds);
    }
    private double ClampTime(double seconds) => double.IsFinite(seconds) ? Math.Clamp(seconds, 0, HasDuration ? DurationSeconds : double.MaxValue) : 0;
    private void CommitTime()
    {
        if (!TryParseTime(CurrentTimeText, out double seconds)) { ShowError("時刻は秒数、mm:ss、h:mm:ss.fffで指定してください", ""); return; }
        SeekTo(seconds, force: true);
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
        _suspendChanges = true;
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
        finally { _suspendChanges = false; }
        _ = QueuePreview();
    }
    private void RunCommand(Func<Task> command)
    {
        Track(ExecuteAsync());
        async Task ExecuteAsync()
        {
            try { await command(); }
            catch (Exception exception) { await PostAsync(() => { if (!_disposed) ShowError("操作に失敗しました", exception.Message); }); }
        }
    }

    private async Task LoadMetadataAsync(string encoder, CancellationToken token)
    {
        try
        {
            string version = await ReadTextAsync(encoder, "--version", token).ConfigureAwait(false);
            string help = await ReadTextAsync(encoder, "--help", token).ConfigureAwait(false);
            await PostAsync(() =>
            {
                if (_disposed || EncoderPath != encoder) return;
                EncoderDescription = DescribeEncoder(version, encoder);
                _helpIndex = new HelpIndex(help);
                HelpSchema = new HelpSchema(help);
                UpdateHelp();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await PostAsync(() =>
            {
                if (_disposed || EncoderPath != encoder) return;
                _metadataEncoder = "";
                EncoderDescription = Path.GetFileName(encoder);
                HelpText = "ヘルプを取得できませんでした: " + exception.Message;
            }).ConfigureAwait(false);
        }
    }
    private static string DescribeEncoder(string version, string encoder)
    {
        string first = version.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? Path.GetFileName(encoder);
        var match = Regex.Match(first, @"\b(NVEncC?|QSVEncC?|VCEEncC?)\b.*?\b(\d+\.\d+(?:\.\d+)?)\b", RegexOptions.CultureInvariant);
        if (match.Success)
        {
            string name = match.Groups[1].Value;
            if (name.EndsWith('C')) name = name[..^1];
            return name + " " + match.Groups[2].Value;
        }
        return first.Length > 48 ? first[..48] + "…" : first;
    }

    private async Task<string> ReadTextAsync(string encoder, string argument, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(encoder)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5), _clock, token).ConfigureAwait(false); }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        string stdout = await output.ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stdout) ? await error.ConfigureAwait(false) : stdout;
    }
    private void Track(Task task)
    {
        lock (_taskLock) _tasks.Add(task);
        _ = task.ContinueWith(completed => { lock (_taskLock) _tasks.Remove(completed); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static Task PostAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => { try { action(); completion.TrySetResult(); } catch (Exception exception) { completion.TrySetException(exception); } });
        return completion.Task;
    }
    public ValueTask DisposeAsync()
    {
        _disposeTask ??= DisposeCoreAsync();
        return new(_disposeTask);
    }
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _initialized = false;
        Interlocked.Increment(ref _generation);
        CancelDuration();
        _lifetime.Cancel();
        CancelBefore(updatePresentation: false);
        lock (_taskLock) _preparationCancellation?.Cancel();
        _controller.FrameReady -= OnFrameReady;
        _controller.Completed -= OnCompleted;
        _controller.Failed -= OnFailed;
        await _controller.DisposeAsync().ConfigureAwait(false);
        await _probe.DisposeAsync().ConfigureAwait(false);
        await WaitForIdleAsync().ConfigureAwait(false);
        await PostAsync(() =>
        {
            HasDuration = false;
            DurationSeconds = 0;
            DurationText = "";
            PreviewImage = null;
            BeforeImage = null;
            IsBeforeBusy = false;
            _displayedFrame = null;
            _beforeDisplayedFrame = null;
            ClearInterval();
            ClearBeforeInterval();
            _cache.Clear();
        }).ConfigureAwait(false);
        _lifetime.Dispose();
    }
    private enum RequestKind { After, Before }
    private sealed class RequestContext(long generation, PreviewRequest request, long started, RequestKind kind = RequestKind.After,
        CancellationTokenSource? beforeCancellation = null, long editEpoch = 0, int neighborDirection = 0)
    {
        public long Generation { get; } = generation;
        public PreviewRequest Request { get; } = request;
        public long Started { get; } = started;
        public RequestKind Kind { get; } = kind;
        public long EditEpoch { get; } = editEpoch;
        public int NeighborDirection { get; } = neighborDirection;
        public CancellationTokenSource? BeforeCancellation { get; } = beforeCancellation;
        public string Mode { get; set; } = "";
        public string Diagnostics { get; set; } = "";
    }
    private sealed class ObservedBackend(IPreviewBackend inner, string mode, MainWindowViewModel owner) : IPreviewBackend
    {
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            var context = owner.ExecutionContextFor(request);
            if (context != null) context.Mode = mode;
            using var cancellation = context?.BeforeCancellation == null ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.BeforeCancellation.Token);
            try
            {
                var result = await inner.RunAsync(request, sink, cancellation?.Token ?? cancellationToken).ConfigureAwait(false);
                if (context != null) context.Diagnostics = result.StandardError;
                return result;
            }
            catch (PreviewBackendException exception) { if (context != null) context.Diagnostics = exception.StandardError; throw; }
        }
        public ValueTask AbortAsync() => inner.AbortAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

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

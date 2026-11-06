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
    private readonly bool _borrowInputProbe;
    private readonly FrameCache _cache;
    private readonly PreviewController _controller;
    private readonly Func<Y4mFrame, HdrDisplaySettings, CancellationToken, Task<byte[]>> _imageConverter;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _taskLock = new();
    private readonly HashSet<Task> _tasks = new();
    private CancellationTokenSource? _preparationCancellation;
    private Task _preparation = Task.CompletedTask;
    private Task? _disposeTask;
    private int _shutdownIncomplete;
    private bool _initialized, _disposed, _suspendChanges, _seekDragging;
    private long _generation;
    private RequestContext? _context;
    private InputInfo? _inputInfo;
    private HelpIndex _helpIndex = new("");
    private HelpSchema? _helpSchema;
    private string _metadataEncoder = "";
    private PreviewRequest? _intervalRequest, _intervalLogicalRequest;
    private IReadOnlyList<Y4mFrame> _intervalFrames = Array.Empty<Y4mFrame>();
    private Y4mFrame? _displayedFrame;
    private readonly HashSet<FilterLineViewModel> _observedLines = new();

    public MainWindowViewModel(IViewModelServices? services = null, TimeProvider? timeProvider = null,
        IPreviewBackend? preferred = null, IPreviewBackend? fallback = null, InputProbe? probe = null, FrameCache? cache = null)
        : this(services, timeProvider, preferred, fallback, probe, cache, null) { }

    /// <summary>偽時計による描画待ちの再現用に、画像変換だけを差し替える内部入口。</summary>
    internal MainWindowViewModel(IViewModelServices? services, TimeProvider? timeProvider,
        IPreviewBackend? preferred, IPreviewBackend? fallback, InputProbe? probe, FrameCache? cache,
        Func<Y4mFrame, CancellationToken, Task<byte[]>>? converter,
        IPreviewBackend? backgroundPreferred = null, IPreviewBackend? backgroundFallback = null, bool beforeEnabled = true)
    {
        _imageConverter = converter != null ? (frame, settings, token) => converter(frame, token) : (frame, settings, token) => Task.Run(() =>
        {
            // 外側のTask.Run待ちと、変換内の並列処理を含む待ちを計測時だけ区別する。
            if (PerfTrace.Enabled) PerfTrace.Write("vm.convert.worker.start", new { frame.Xts });
            try { return YuvToBgra.Convert(frame, settings); }
            finally { if (PerfTrace.Enabled) PerfTrace.Write("vm.convert.worker.end", new { frame.Xts }); }
        }, token);
        _beforeEnabled = beforeEnabled;
        _services = services ?? new AvaloniaViewModelServices();
        _clock = timeProvider ?? TimeProvider.System;
        _probe = probe ?? new InputProbe(_clock);
        _borrowInputProbe = probe == null && preferred == null;
        _cache = cache ?? new FrameCache();
        var first = preferred ?? new ServerBackend(_clock);
        var second = fallback ?? new OneShotBackend(timeProvider: _clock);
        var observedFirst = new ObservedBackend(first, first is OneShotBackend ? "oneshot" : "server", this);
        _controller = new PreviewController(observedFirst,
            ReferenceEquals(first, second) ? observedFirst : new ObservedBackend(second, "oneshot", this), _cache, _clock,
            backgroundBackend: backgroundPreferred != null ? new ObservedBackend(backgroundPreferred, "server", this) :
                preferred == null ? new ObservedBackend(new ServerBackend(_clock), "server", this) : observedFirst,
            backgroundFallback: backgroundFallback != null ? new ObservedBackend(backgroundFallback, "oneshot", this) :
                preferred == null ? new ObservedBackend(new OneShotBackend(timeProvider: _clock), "oneshot", this) :
                ReferenceEquals(first, second) ? observedFirst : new ObservedBackend(second, "oneshot", this));
        _controller.ReplayStarted += OnReplayStarted;
        _controller.SegmentUpdated += OnSegmentUpdated;
        _controller.FrameReady += OnFrameReady;
        _controller.Completed += OnCompleted;
        _controller.Failed += OnFailed;
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
        StepFrameCommand = new RelayCommand(p => StepFrame((int)Number(p)));
        StepSecondsCommand = new RelayCommand(p => SeekTo(CurrentSeconds + Number(p)));
        GoToStartCommand = new RelayCommand(() => SeekTo(0));
        GoToEndCommand = new RelayCommand(() => { if (HasDuration) SeekTo(DurationSeconds); });
        CommitTimeTextCommand = new RelayCommand(CommitTime);
        RefreshCommand = new RelayCommand(() => { _cache.Clear(); ClearInterval(); ClearBeforeInterval(); _ = QueuePreview(bypassCache: true, bypassDebounce: true); });
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
            Language = settings.Language;
            HdrReferenceWhiteNits = settings.HdrReferenceWhiteNits;
            EncoderPath = settings.EncoderPath;
            InputPath = settings.InputPath;
            InputArgsText = settings.InputArgsText;
            _currentSeconds = settings.CurrentSeconds;
            CurrentTimeText = FormatTime(_currentSeconds);
            WindowWidth = settings.WindowWidth;
            IsTextListMode = settings.IsTextListMode;
            WindowHeight = settings.WindowHeight;
            FilterLines.Clear();
            foreach (var line in settings.FilterLines) FilterLines.Add(new FilterLineViewModel { Text = line.Text, IsEnabled = line.IsEnabled });
            _suspendChanges = false;
            OnPropertyChanged(nameof(CurrentSeconds));
        }
        _initialized = true;
        await QueuePreview(bypassDebounce: true);
    }

    /// <summary>通常要求の入力検査とSubmit登録を待つ。パス文字編集中は待機タイマー登録までを同期点とし、時計を進めた後の完了はWaitForIdleAsyncで待つ。</summary>
    public Task WaitForPreparationAsync() => _inputEditCancellation != null ? Task.CompletedTask : _preparation;

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
            WindowWidth = width, WindowHeight = height, CurrentSeconds = CurrentSeconds, IsTextListMode = IsTextListMode, Language = Language, HdrReferenceWhiteNits = HdrReferenceWhiteNits,
        };
        try { await Task.Run(() => settings.Save(_services.SettingsPath)).ConfigureAwait(false); }
        catch (Exception exception) { await PostAsync(() => ShowError(AppMessages.SaveSettingsFailed, exception.Message)).ConfigureAwait(false); }
    }

    public string EncoderPath { get => _encoderPath; set { if (SetProperty(ref _encoderPath, value ?? "")) { CancelEncoderInitialization(); EncoderDescription = ""; _metadataEncoder = ""; _helpIndex = new HelpIndex(""); _capabilities = new(); HelpSchema = null; HelpText = ""; InputChanged(); } } }
    public string InputPath { get => _inputPath; set { if (SetProperty(ref _inputPath, value ?? "")) InputChanged(); } }
    /// <summary>例: "QSVEnc 8.35"。</summary>
    public string EncoderDescription { get => _encoderDescription; set => SetProperty(ref _encoderDescription, value); }
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
    public Bitmap? PreviewImage { get => _previewImage; set { var previous = _previewImage; if (SetProperty(ref _previewImage, value)) previous?.Dispose(); } }
    /// <summary>SAR を考慮した表示上の幅・高さ (画素)。View はこの比率で拡大縮小して表示する。</summary>
    public double DisplayWidth { get => _displayWidth; set => SetProperty(ref _displayWidth, value); }
    public double DisplayHeight { get => _displayHeight; set => SetProperty(ref _displayHeight, value); }

    /// <summary>時刻欄 (双方向)。Enter で CommitTimeTextCommand。</summary>
    public string CurrentTimeText { get => _currentTimeText; set => SetProperty(ref _currentTimeText, value ?? ""); }
    /// <summary>シークバーの目標位置(秒)。ドラッグ中も移動要求を送り、待ちは最新位置へ置き換える。</summary>
    public double CurrentSeconds { get => _currentSeconds; set { RecordNavigationIntent(); if (SetProperty(ref _currentSeconds, ClampTime(value))) { CurrentTimeText = FormatTime(_currentSeconds); _ = QueuePreview(navigation: true); } } }
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


    public void BeginSeekDrag() => _seekDragging = true;
    public void EndSeekDrag(double seconds)
    {
        if (!_seekDragging) return;
        _seekDragging = false;
        if (seconds == CurrentSeconds)
        {
            // ドラッグ中のAfterを捨てず、確定した同じ目標のBeforeだけを同時取得へ進める。
            long generation = Volatile.Read(ref _generation);
            _concurrentComparisonGeneration = generation;
            if (_context is { Kind: RequestKind.After } context && IsCurrent(context))
                QueueBefore(context.Request with { AllowConcurrentBackground = true }, generation);
        }
        else SeekTo(seconds, allowConcurrentBefore: true);
    }

    private void InputChanged(bool bypassDebounce = false)
    {
        bool suspended = _suspendChanges;
        _suspendChanges = true;
        try
        {
            CancelDuration();
            _inputInfo = null;
            HasDuration = false;
            DurationSeconds = 0;
            DurationText = "";
            ClearInterval();
        }
        finally { _suspendChanges = suspended; }
        if (!_initialized || _suspendChanges || _disposed) return;
        lock (_taskLock) _preparationCancellation?.Cancel();
        BeginIntent(navigation: false);
        if (bypassDebounce) { CancelInputEdit(); _ = QueuePreview(bypassDebounce: true); }
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
    private void FiltersChanged(bool bypassDebounce = false) { ClearInterval(); _ = QueuePreview(bypassDebounce: bypassDebounce); }
    private void UpdateHelp()
    {
        if (_initialized || _helpIndex.OptionNames.Count > 0) HelpText = _helpIndex.Lookup(SelectedFilterLine?.OptionName ?? "");
    }
    private void ClearInterval() { _intervalFrames = Array.Empty<Y4mFrame>(); _intervalRequest = null; _intervalLogicalRequest = null; _intervalSegment = null; _navigationAnchor = null; }

    private bool IsCurrent(long generation) => !_disposed && generation == Volatile.Read(ref _generation);
    private bool IsCurrent(RequestContext context) => context.Kind == RequestKind.Before
        ? !_disposed && context.EditEpoch == Volatile.Read(ref _editEpoch)
            && ReferenceEquals(Volatile.Read(ref _beforeExecutionContext), context)
            && context.BeforeCancellation?.IsCancellationRequested != true
        : !_disposed && context.EditEpoch == Volatile.Read(ref _editEpoch) && context.Generation >= _lastDisplayedAfter;
    private RequestContext? ContextFor(PreviewRequest request)
    {
        if (_afterRequests.TryGetValue(request.CorrelationId, out var after) && SameRequest(after.Request, request)) return after;
        var before = Volatile.Read(ref _beforeExecutionContext);
        if (before != null && SameRequest(before.Request, request)) return before;
        var context = Volatile.Read(ref _context);
        return context != null && SameRequest(context.Request, request) ? context : null;
    }
    private RequestContext? ContextForPhysical(PreviewRequest request)
    {
        // 物理コピーでは時刻flagを除去するため、対応付けは一意な論理CorIdを使う。
        RequestContext? context = _afterRequests.TryGetValue(request.CorrelationId, out var after) ? after
            : Volatile.Read(ref _beforeExecutionContext);
        if (context?.Request.CorrelationId != request.CorrelationId) context = Volatile.Read(ref _context);
        return context != null && context.Request.CorrelationId == request.CorrelationId
            && context.Request.EncoderPath == request.EncoderPath && context.Request.InputFile == request.InputFile
            && context.Request.Fps == request.Fps ? context : null;
    }
    private static bool SameRequest(PreviewRequest a, PreviewRequest b) => a.CorrelationId == b.CorrelationId && a.EncoderPath == b.EncoderPath && a.InputFile == b.InputFile
        && a.TargetSeconds == b.TargetSeconds && a.Fps == b.Fps && a.InputArgs.SequenceEqual(b.InputArgs) && a.VppArgs.SequenceEqual(b.VppArgs);

    private void OnFrameReady(PreviewFrameResult value)
    {
        var context = ContextFor(value.Request);
        if (context != null && value.Segment != null) context.Segment = value.Segment;
        if (context != null && value.IsReplay) BeginReplay(context);
        long revision = context == null ? 0 : Volatile.Read(ref context.DisplayRevision);
        // 元画像は完了列から最新目標を選ぶため、古い目標向けの早期通知では表示しない。
        if (context == null || context.Kind == RequestKind.Before || context.NeighborDirection != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrent(context) || revision != Volatile.Read(ref context.DisplayRevision)) return;
            if (value.IsReplay) PrepareReplayUi(context);
            DisplayFrame(value.Frame, context);
        });
    }
    private void OnReplayStarted(PreviewRequest request)
    {
        var context = ContextFor(request);
        if (_disposed || context == null) return;
        // 再取得が1枚も返さず失敗しても、初回の変換が後から復活しないよう即時に失効する。
        BeginReplay(context);
        long revision = Volatile.Read(ref context.DisplayRevision);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrent(context) || revision != Volatile.Read(ref context.DisplayRevision)) return;
            if (context.Kind == RequestKind.Before) { IsBeforeBusy = true; return; }
            PrepareReplayUi(context);
            if (context.Generation != Volatile.Read(ref _generation)) return;
            _lastCompletedAfter = Math.Min(_lastCompletedAfter, context.Generation - 1);
            IsBusy = true;
            StatusText = AppMessages.Processing;
        });
    }
    private static void BeginReplay(RequestContext context)
    {
        if (Interlocked.CompareExchange(ref context.ReplayStarted, 1, 0) == 0)
            Interlocked.Increment(ref context.DisplayRevision);
    }
    private void PrepareReplayUi(RequestContext context)
    {
        if (context.ReplayUiStarted) return;
        context.ReplayUiStarted = true;
        context.NeighborResolved = false;
        context.ResolvedTargetSeconds = null;
        if (context.Generation != Volatile.Read(ref _generation)) return;
        _navigationAnchor = null;
        if (_lastFailedAfter == context.Generation)
        {
            _lastFailedAfter = 0;
            ErrorText = null;
        }
    }
    private void OnCompleted(PreviewCompletion value)
    {
        var context = ContextFor(value.Request);
        if (context == null) return;
        if (value.IsReplay) BeginReplay(context);
        if (value.Segment != null) context.Segment = value.Segment;
        IReadOnlyList<Y4mFrame> frames = value.Frames;
        if (frames.Count == 0) _cache.TryGet(value.Request, out frames);
        if (context.Kind == RequestKind.After) _afterRequests.TryRemove(context.Generation, out _);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrent(context)) return;
            if (context.Kind == RequestKind.Before)
            {
                CaptureBeforeFallbackDiagnostic(value.Result.StandardError);
                CompleteBefore(context, frames);
                return;
            }
            if (value.IsReplay) PrepareReplayUi(context);
            if (_lastFailedAfter == Volatile.Read(ref _generation)) return;
            _intervalRequest = value.Segment?.PhysicalRequest ?? value.Request;
            _intervalLogicalRequest = value.Request;
            _intervalSegment = value.Segment;
            _intervalFrames = frames;
            _lastCompletedAfter = Math.Max(_lastCompletedAfter, context.Generation);
            var afterRequest = context.NeighborDirection != 0 ? CompleteNeighbor(context, frames) : value.Request;
            if (frames.Count == 0 && context.Generation == Volatile.Read(ref _generation) && _lastQueuedAfter < context.Generation)
            { _lastFailedAfter = context.Generation; ShowError(AppMessages.NoDisplayableFrames, value.Result.StandardError); return; }
            ErrorText = null;
            LogText = AppendBeforeFallbackDiagnostic(value.Result.StandardError);
            if (!string.IsNullOrEmpty(context.Mode)) BackendModeText = AppMessages.BackendMode(context.Mode);
            ElapsedText = AppMessages.Elapsed(_clock.GetElapsedTime(context.Started).TotalSeconds);
            if (context.Generation == Volatile.Read(ref _generation))
            {
                if (afterRequest != null)
                    _completedAfterStatus = value.TargetNotReached ? AppMessages.DoneTargetNotReached : value.FromCache ? AppMessages.DoneCache : AppMessages.Done;
                QueueBefore(afterRequest ?? value.Request, context.Generation);
            }
            UpdateNavigationStatus();
        });
    }
    private void OnFailed(PreviewFailure value)
    {
        var context = ContextFor(value.Request);
        if (context == null) return;
        string diagnostics = value.Diagnostic ?? value.Result?.StandardError ?? (value.Exception as PreviewBackendException)?.StandardError ?? context.Diagnostics;
        if (context.Kind == RequestKind.After) _afterRequests.TryRemove(context.Generation, out _);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrent(context)) return;
            if (context.Kind == RequestKind.Before)
            {
                BeforeUnavailableForDuration();
                CaptureBeforeFallbackDiagnostic(diagnostics);
                _beforeFinished = true;
                IsBeforeBusy = false;
                Interlocked.Increment(ref _beforeImageGeneration);
                _beforeDisplayedFrame = null;
                BeforeImage = null;
                return;
            }
            if (context.Generation != Volatile.Read(ref _generation)) return;
            if (_pendingNeighbor?.Generation == context.Generation) _pendingNeighbor = null;
            _lastFailedAfter = context.Generation;
            if (!string.IsNullOrEmpty(context.Mode)) BackendModeText = AppMessages.BackendMode(context.Mode);
            ElapsedText = AppMessages.Elapsed(_clock.GetElapsedTime(context.Started).TotalSeconds);
            ShowError(value.Reason, diagnostics);
        });
    }
    private void DisplayFrame(Y4mFrame frame, RequestContext context)
    {
        long revision = Volatile.Read(ref context.DisplayRevision);
        if (context.Generation < _lastQueuedAfter || context.Generation == _lastQueuedAfter && revision <= _lastQueuedRevision) return;
        _lastQueuedAfter = context.Generation;
        _lastQueuedRevision = revision;
        RememberNavigationAnchor(frame, context);
        var clock = context.Segment?.Clock ?? ClockFor(context.Request, new[] { frame });
        EnqueueImage(new ImageWork(frame, context.Generation, context.EditEpoch,
            DisplaySeconds: PerfTrace.Enabled && frame.Xts.HasValue ? clock.ToInputSeconds(frame, 0, context.Request.Fps) : null,
            SegmentId: context.Segment?.SegmentId, TrustedAbsolute: clock.TrustedAbsolute,
            RevisionContext: context, DisplayRevision: revision));
    }
    private void ShowError(string message, string diagnostics)
    {
        if (PerfTrace.Enabled) PerfTrace.Write("vm.error", new { message, diagnostics });
        IsBusy = false;
        StatusText = AppMessages.Error;
        LogText = AppendBeforeFallbackDiagnostic(diagnostics);
        string[] lines = diagnostics.Split('\n');
        string tail = string.Join(Environment.NewLine, lines.TakeLast(6));
        // readerの後片付けで末尾が埋まっても、長いkey間隔による窓の保持上限や探索上限の理由を隠さない。
        // 表示する診断の選択だけで、窓の不足判定や回復の可否には使わない。
        string? windowError = lines.FirstOrDefault(line => line.Contains("preview window keyframe buffer exceeds ", StringComparison.Ordinal)
            || line.Contains("preview window keyframe not found within ", StringComparison.Ordinal));
        if (windowError != null && !tail.Contains(windowError, StringComparison.Ordinal))
            tail = windowError.TrimEnd('\r') + Environment.NewLine + tail;
        ErrorText = string.IsNullOrWhiteSpace(tail) ? message : message + Environment.NewLine + tail;
    }

    private void StepFrame(int direction)
    {
        if (direction != 0) RecordNavigationIntent();
        if (PerfTrace.Enabled) PerfTrace.Write("vm.step", new { direction, pending = _pendingNeighbor != null, reverseWait = _reversePrefetchWait != null, target = CurrentSeconds });
        LeaveReversePrefetchWaitForForwardStep(direction);
        if (direction == 0 || _pendingNeighbor != null)
        {
            if (PerfTrace.Enabled) PerfTrace.Write("vm.step.drop", new { direction, pending = _pendingNeighbor != null });
            return;
        }
        direction = Math.Sign(direction);
        if (_initialized && direction < 0 && CurrentSeconds <= 1e-8)
        {
            _completedAfterStatus = AppMessages.Start;
            UpdateNavigationStatus();
            return;
        }
        ValidateNavigationState();
        if (_intervalRequest is { } interval && _intervalFrames.Count > 0)
        {
            var navigation = NavigationFrames(interval, _intervalSegment);
            if (navigation.Count > 0)
            {
                int anchor = NavigationAnchorIndex(navigation, CurrentSeconds, interval.Fps);
                int next = anchor + direction;
                if (anchor >= 0 && next >= 0 && next < navigation.Count
                    && NavigationNeighborsConnected(navigation[anchor], navigation[next]))
                {
                    var reference = navigation[next];
                    if ((reference.InputSeconds - CurrentSeconds) * direction > 1e-9)
                    {
                        AdoptNavigationReference(reference);
                        DisplayCached(reference.Frame, reference.InputSeconds, direction);
                        return;
                    }
                }
            }
            // trustedな移動viewがあれば、warmを含む元配列へ戻って候補を増やさない。
            if (_intervalSegment?.Clock.TrustedAbsolute != true && _intervalFrames.Count > 0)
            {
                double target = IntervalClock(interval, _intervalFrames).ToFrameSeconds(CurrentSeconds);
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
                        if (FrameIsSafe(seconds) && (seconds - CurrentSeconds) * direction > 1e-9)
                        {
                            if (PerfTrace.Enabled) PerfTrace.Write("vm.step.cache", new { seconds, direction });
                            DisplayCached(frame, seconds, direction); return;
                        }
                    }
                }
            }
        }
        if (TryWaitForReversePrefetch(direction)) return;
        // 取り直し中のキー反復は加算せず、実際の隣が届くまで目標を保つ。
        if (!_initialized)
        {
            // 入力を開く前の操作は、表示位置だけを仮に動かす。
            SeekTo(CurrentSeconds + direction / (_inputInfo?.Fps ?? 30));
            return;
        }
        if (PerfTrace.Enabled) PerfTrace.Write("vm.step.refetch", new { direction, target = CurrentSeconds });
        _ = QueuePreview(navigation: true, neighborDirection: direction,
            requirePhysicalRefetch: _intervalSegment?.Clock.TrustedAbsolute == true);
    }
    private void DisplayCached(Y4mFrame frame, double seconds, int direction)
    {
        if (_intervalRequest == null) return;
        long generation = BeginIntent(navigation: true);
        if (PerfTrace.Enabled) PerfTrace.Write("vm.step.accepted", new { generation, seconds, direction, frame.Xts });
        SetTimeOnly(seconds);
        // 物理offの結果を移動に再利用しても、診断用sourceはhelp由来の論理設定を維持する。
        var logical = _intervalLogicalRequest ?? _intervalRequest;
        var request = RetargetInterval(_intervalRequest, seconds) with { CorrelationId = generation, AllowConcurrentBackground = false,
            InputArgs = logical.InputArgs, VppArgs = logical.VppArgs, Capabilities = _capabilities,
            TimestampOriginTrusted = _inputInfo?.TimestampOriginTrusted ?? _intervalRequest.TimestampOriginTrusted };
        CheckBeforeInterval(request);
        var context = new RequestContext(generation, request, _clock.GetTimestamp(), editEpoch: _editEpoch);
        context.Segment = _intervalSegment;
        _lastIntentTarget = seconds;
        RegisterContext(context);
        IsBusy = true;
        ErrorText = null;
        _completedAfterStatus = AppMessages.DoneCache;
        DisplayFrame(frame, context);
        // 表示は即時に進め、Coreにも最新の移動意図を伝えて古い待ち要求を置き換える。
        _controller.Submit(request, navigation: true);
        NotifyAcceptedFrameStep(request, direction, seconds);
        Track(ReleaseContextAsync(context));
    }
    private void SeekTo(double seconds, bool force = false, bool allowConcurrentBefore = false)
    {
        RecordNavigationIntent();
        seconds = ClampTime(seconds);
        if (!force && seconds == CurrentSeconds) return;
        SetTimeOnly(seconds);
        _ = QueuePreview(navigation: true, allowConcurrentBefore: allowConcurrentBefore);
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
        if (!TryParseTime(CurrentTimeText, out double seconds)) { ShowError(AppMessages.InvalidTime, ""); return; }
        SeekTo(seconds, force: true, allowConcurrentBefore: true);
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
        _ = QueuePreview(bypassDebounce: true);
    }
    private void RunCommand(Func<Task> command)
    {
        Track(ExecuteAsync());
        async Task ExecuteAsync()
        {
            try { await command(); }
            catch (Exception exception) { await PostAsync(() => { if (!_disposed) ShowError(AppMessages.OperationFailed, exception.Message); }); }
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
                _capabilities = CommandBuilder.CapabilitiesFromHelp(_helpIndex);
                _ = StartEncoderInitialization(encoder);
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
                _capabilities = new();
                EncoderDescription = Path.GetFileName(encoder);
                HelpText = AppMessages.HelpUnavailable + exception.Message;
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
        if (process.ExitCode != 0) throw new InvalidOperationException(AppMessages.EncoderInfoFailed(process.ExitCode));
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
        TraceDisposeStage("enter");
        _disposed = true;
        UiLanguage.Changed -= OnLanguageChanged;
        _initialized = false;
        Interlocked.Increment(ref _generation);
        long started = _clock.GetTimestamp();
        Task cancellations = Task.Run(() =>
        {
            CancelEncoderInitialization();
            TraceDisposeStage("duration_cancel.begin");
            CancelDuration();
            TraceDisposeStage("duration_cancel.end");
            TraceDisposeStage("lifetime_cancel.begin");
            _lifetime.Cancel();
            TraceDisposeStage("lifetime_cancel.end");
            TraceDisposeStage("before_cancel.begin");
            CancelBefore(updatePresentation: false);
            TraceDisposeStage("before_cancel.end");
            TraceDisposeStage("preparation_cancel.begin");
            lock (_taskLock) _preparationCancellation?.Cancel();
            TraceDisposeStage("preparation_cancel.end");
        });
        _controller.ReplayStarted -= OnReplayStarted;
        _controller.SegmentUpdated -= OnSegmentUpdated;
        _controller.FrameReady -= OnFrameReady;
        _controller.Completed -= OnCompleted;
        _controller.Failed -= OnFailed;
        // Coreが期限で残したlogical taskをIdle経由でもう一度待たない。
        // 同期取消・検査・変換・UI反映にも同じ総期限を適用し、残る処理は遅延観測する。
        TraceDisposeStage("controller_dispose.begin");
        Task controller = Task.Run(async () => await _controller.DisposeAsync().ConfigureAwait(false));
        await WaitForVmShutdownAsync(cancellations, started, "cancellations").ConfigureAwait(false);
        await WaitForVmShutdownAsync(controller, started, "controller").ConfigureAwait(false);
        if (_controller.ShutdownIncomplete) Interlocked.Exchange(ref _shutdownIncomplete, 1);
        TraceDisposeStage("controller_dispose.end");
        TraceDisposeStage("probe_dispose.begin");
        Task probe = Task.Run(async () => await _probe.DisposeAsync().ConfigureAwait(false));
        await WaitForVmShutdownAsync(probe, started, "probe").ConfigureAwait(false);
        TraceDisposeStage("probe_dispose.end");
        TraceDisposeStage("wait_idle.begin");
        Task pending = Task.Run(async () =>
        {
            Task[] tasks;
            lock (_taskLock) tasks = _tasks.ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        });
        await WaitForVmShutdownAsync(pending, started, "vm_tasks").ConfigureAwait(false);
        TraceDisposeStage("wait_idle.end");
        TraceDisposeStage("cleanup.begin");
        Task cleanup = PostAsync(() =>
        {
            HasDuration = false;
            DurationSeconds = 0;
            DurationText = "";
            PreviewImage = null;
            BeforeImage = null;
            IsBeforeBusy = false;
            lock (_taskLock) { _pendingAfterDisplayRefresh = null; _pendingBeforeDisplayRefresh = null; }
            _displayedFrame = null;
            _beforeDisplayedFrame = null;
            RefreshColorInfo();
            ClearInterval();
            ClearBeforeInterval();
            _cache.Clear();
            Interlocked.Exchange(ref _pendingSegmentUpdate, null);
        });
        await WaitForVmShutdownAsync(cleanup, started, "ui_cleanup").ConfigureAwait(false);
        Task resources = Task.WhenAll(cancellations, controller, probe, pending);
        ObserveVmShutdownTask(resources);
        if (resources.IsCompleted) _lifetime.Dispose();
        else
        {
            // 未回収の取消callbackや準備taskとCTSのDisposeを競合させない。
            Task release = resources.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            ObserveVmShutdownTask(release);
        }
        TraceDisposeStage(Volatile.Read(ref _shutdownIncomplete) == 0 ? "complete" : "incomplete");
    }
    private async Task WaitForVmShutdownAsync(Task task, long started, string stage)
    {
        try
        {
            if (task.IsCompleted) await task.ConfigureAwait(false);
            else
            {
                TimeSpan remaining = TimeSpan.FromSeconds(5) - _clock.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                await task.WaitAsync(remaining, _clock).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _shutdownIncomplete, 1);
            System.Diagnostics.Trace.TraceWarning("Viewer shutdown incomplete ({0}): {1}", stage, exception.Message);
            TraceDisposeStage("incomplete." + stage);
            ObserveVmShutdownTask(task);
        }
    }
    private static void ObserveVmShutdownTask(Task task) => _ = task.ContinueWith(completed =>
    {
        System.Diagnostics.Trace.TraceWarning("Viewer shutdown late failure: {0}", completed.Exception);
    }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static void TraceDisposeStage(string stage)
    {
        if (!PerfTrace.Enabled) return;
        // 終了途中の停止でも最後に到達した段階を失わず、UI文脈への継続も識別する。
        PerfTrace.Write("vm.dispose.stage", new { stage, synchronizationContext = SynchronizationContext.Current?.GetType().FullName });
        PerfTrace.Flush();
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
        public long DisplayRevision;
        public int ReplayStarted;
        public bool ReplayUiStarted;
        public bool NeighborResolved { get; set; }
        public double? ResolvedTargetSeconds { get; set; }
        public PreviewSegmentSnapshot? Segment { get; set; }
        public string Mode { get; set; } = "";
        public string Diagnostics { get; set; } = "";
    }
    private sealed class ObservedBackend(IPreviewBackend inner, string mode, MainWindowViewModel owner) : IPreviewBackend
    {
        public Task InitializeEncoderAsync(string encoderPath, CancellationToken cancellationToken = default)
            => inner.InitializeEncoderAsync(encoderPath, cancellationToken);
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken cancellationToken = default)
            => inner.GetHistoryRequirementAsync(request, cancellationToken);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            var context = owner.ContextForPhysical(request);
            if (context != null) context.Mode = mode;
            if (context?.Kind == RequestKind.Before) owner.ObserveBeforePhysical(context, request);
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
            BackendModeText = AppMessages.Server,
            ElapsedText = AppMessages.Elapsed(0.48),
            InputInfoText = "1440x1080 (SAR 4:3)  29.97fps",
            StatusText = AppMessages.Done,
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

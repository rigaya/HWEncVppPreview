using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HWEncParamViewer.App.ViewModels;

namespace HWEncParamViewer.App.Views;

/// <summary>
/// メインウィンドウ。code-behind は「View 固有のイベントを ViewModel の契約 (設計書 17.5) に渡す」ことだけを行う。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // ← → などのコマ送りは、TextBox が処理しなかったキーだけを受ける (bubble)。
        // TextBox 内のカーソル移動を横取りしないため。
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Bubble);

        TimeBox.KeyDown += TimeBox_KeyDown;
        AddHandler(KeyUpEvent, Window_KeyUp, RoutingStrategies.Bubble);

        // 拡大率の表示をクリックでウィンドウに合わせ、1:1 で等倍 (設計書 18.3)
        ZoomFitButton.Click += (_, _) => MainViewer.ZoomToFit();
        ZoomActualButton.Click += (_, _) => ActiveViewer.ZoomToActualSize();
        // 拡大率の表示は「いま見えているビューア」から取る (並べて表示では MainViewer が非表示で、大きさが 0 になるため)
        foreach (var viewer in new[] { MainViewer, AfterViewer })
        {
            viewer.PropertyChanged += (_, e) =>
            {
                if (e.Property == Controls.ZoomPanViewer.ZoomTextProperty) UpdateZoomText();
            };
        }
        DataContextChanged += (_, _) =>
        {
            if (ViewModel != null) ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.CompareMode)) UpdateZoomText();
            };
        };

        // シークバーはドラッグ中に要求を出さない (設計書 17.4)。
        // Slider はポインタイベントを自分で処理済みにするので、handledEventsToo で受ける。
        SeekBar.AddHandler(PointerPressedEvent, (_, _) => ViewModel?.BeginSeekDrag(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => ViewModel?.EndSeekDrag(SeekBar.Value),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerCaptureLostEvent, (_, _) => ViewModel?.EndSeekDrag(SeekBar.Value),
            RoutingStrategies.Direct, handledEventsToo: true);

        // 表示領域のホイールはフレーム移動 (Ctrl+ホイールは拡大縮小、設計書 19.3)
        foreach (var viewer in new[] { MainViewer, BeforeViewer, AfterViewer })
        {
            viewer.FrameStepRequested += (_, frames) => StepFrames(frames);
        }

        // ファイルのドロップ (設計書 19.4)。TextBox は自前でドラッグを処理済みにすることがあるので handledEventsToo で受ける
        foreach (var target in new Control[] { EncoderBox, InputBox, PreviewArea })
        {
            target.AddHandler(DragDrop.DragOverEvent, File_DragOver, handledEventsToo: true);
            target.AddHandler(DragDrop.DropEvent, File_Drop, handledEventsToo: true);
        }
    }

    private void StepFrames(int frames)
    {
        if (ViewModel is not { } vm) return;
        int direction = Math.Sign(frames);
        for (int i = 0; i < Math.Abs(frames); i++) vm.StepFrameCommand.Execute(direction);
    }

    /// <summary>ドロップされたファイルのうち、先頭のローカルファイルのパス。フォルダやローカル以外は null。</summary>
    private static string? DroppedFile(DragEventArgs e)
    {
        var item = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        return item is IStorageFile ? item.TryGetLocalPath() : null;
    }

    private static void File_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedFile(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void File_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { } vm || DroppedFile(e) is not { } path) return;
        bool isExe = string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        // 表示領域では拡張子で振り分ける (exe を落としても入力扱いにしない)
        if (ReferenceEquals(sender, EncoderBox) || (ReferenceEquals(sender, PreviewArea) && isExe)) vm.EncoderPath = path;
        else vm.InputPath = path;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>等倍の基準にするビューア (並べて表示では右側。拡大状態は全ビューアで共有している)。</summary>
    private Controls.ZoomPanViewer ActiveViewer =>
        ViewModel?.CompareMode == PreviewCompareMode.SideBySide ? AfterViewer : MainViewer;

    private void UpdateZoomText() => ZoomFitButton.Content = ActiveViewer.ZoomText;

    private void FilterLine_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (ViewModel != null && sender is Control { DataContext: FilterLineViewModel line })
        {
            ViewModel.SelectedFilterLine = line;
        }
    }

    private void FilterLine_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;
        if (Nudge(sender, e.Delta.Y > 0 ? 1 : -1, e.KeyModifiers.HasFlag(KeyModifiers.Shift))) e.Handled = true;
    }

    private void FilterLine_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        int direction = e.Key switch { Key.Up => 1, Key.Down => -1, _ => 0 };
        if (direction == 0) return;
        if (Nudge(sender, direction, e.KeyModifiers.HasFlag(KeyModifiers.Shift))) e.Handled = true;
    }

    /// <summary>カーソル位置の数値を増減する。数値の上でなければ何もしない (false)。</summary>
    private static bool Nudge(object? sender, int direction, bool coarse)
    {
        if (sender is not TextBox { DataContext: FilterLineViewModel line } box) return false;
        int? caret = line.Nudge(box.CaretIndex, direction, coarse);
        if (caret == null) return false;
        box.CaretIndex = caret.Value;
        return true;
    }

    private void TimeBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel == null) return;
        ViewModel.CommitTimeTextCommand.Execute(null);
        e.Handled = true;
    }

    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        // TextBox の中で離した場合も、表示はフィルタ後に戻す (押したまま入力欄へ移ったときの取り残し防止)
        if (e.Key == Key.Space && ViewModel is { IsShowingBefore: true } vm)
        {
            vm.IsShowingBefore = false;
        }
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null || e.Handled) return;
        // テキスト入力中は、TextBox が処理しなかったキーでもコマ送りにしない (誤操作防止)。
        if (e.Source is TextBox) return;

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Left when shift: vm.StepSecondsCommand.Execute(-1.0); break;
            case Key.Right when shift: vm.StepSecondsCommand.Execute(1.0); break;
            case Key.Left: vm.StepFrameCommand.Execute(-1); break;
            case Key.Right: vm.StepFrameCommand.Execute(1); break;
            case Key.PageUp: vm.StepSecondsCommand.Execute(-10.0); break;
            case Key.PageDown: vm.StepSecondsCommand.Execute(10.0); break;
            case Key.Home: vm.GoToStartCommand.Execute(null); break;
            case Key.End: vm.GoToEndCommand.Execute(null); break;
            // 表示モード (設計書 18.2)
            case Key.D1 or Key.NumPad1: vm.SetCompareModeCommand.Execute(PreviewCompareMode.Single); break;
            case Key.D2 or Key.NumPad2: vm.SetCompareModeCommand.Execute(PreviewCompareMode.Split); break;
            case Key.D3 or Key.NumPad3: vm.SetCompareModeCommand.Execute(PreviewCompareMode.SideBySide); break;
            // 拡大 (設計書 18.3)
            case Key.D0 or Key.NumPad0 when e.KeyModifiers.HasFlag(KeyModifiers.Control): ActiveViewer.ZoomToActualSize(); break;
            case Key.D0 or Key.NumPad0: MainViewer.ZoomToFit(); break;
            // Space を押している間だけフィルタ前を表示する (切替モード)。キーリピートでも true のまま
            case Key.Space when vm.CompareMode is PreviewCompareMode.Single or PreviewCompareMode.Toggle:
                vm.IsShowingBefore = true;
                break;
            default: return;
        }
        e.Handled = true;
    }
}

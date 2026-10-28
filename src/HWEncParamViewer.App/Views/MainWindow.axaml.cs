using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        // シークバーはドラッグ中に要求を出さない (設計書 17.4)。
        // Slider はポインタイベントを自分で処理済みにするので、handledEventsToo で受ける。
        SeekBar.AddHandler(PointerPressedEvent, (_, _) => ViewModel?.BeginSeekDrag(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => ViewModel?.EndSeekDrag(SeekBar.Value),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerCaptureLostEvent, (_, _) => ViewModel?.EndSeekDrag(SeekBar.Value),
            RoutingStrategies.Direct, handledEventsToo: true);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

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
            default: return;
        }
        e.Handled = true;
    }
}

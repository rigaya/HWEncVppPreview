using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HWEncParamViewer.App.ViewModels;

namespace HWEncParamViewer.App.Views;

/// <summary>
/// フィルタの追加の一覧と、パラメータのフォーム (設計書 23.2、23.3)。
/// 値の解釈と行の書き換えは ViewModel (FilterParameterViewModel) が行い、ここは操作を渡すだけにする。
/// </summary>
public partial class MainWindow
{
    // 利用者が操作中のスライダー。これ以外のスライダーの値の変化 (初期化・範囲の補正・ViewModel からの更新) は行に書き戻さない
    private Slider? _activeSlider;

    private void InitializeForm()
    {
        FlyoutCatalog.FilterChosen += (_, optionName) =>
        {
            AddFilterButton.Flyout?.Hide();
            AddFilter(optionName);
        };
        InlineCatalog.FilterChosen += (_, optionName) => AddFilter(optionName);
        // 開くたびに絞り込みを消して、すぐ打てるようにする
        // Flyout は Control ではないので x:Name のフィールドが作られない。ボタンから取る
        AddFilterButton.Flyout!.Opened += (_, _) => Dispatcher.UIThread.Post(FlyoutCatalog.Reset, DispatcherPriority.Background);
        DataContextChanged += (_, _) => FlyoutCatalog.Items = ViewModel?.FilterCatalog;

        AddHandler(RangeBase.ValueChangedEvent, ParameterSlider_ValueChanged, RoutingStrategies.Bubble);
        // Slider はポインタを自分で処理済みにするので、tunnel かつ処理済みも受ける
        AddHandler(PointerPressedEvent, (_, e) => _activeSlider = ParameterSliderOf(e.Source),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // 離したときの値の確定 (トラックのクリックなど) を通してから外す
        AddHandler(PointerReleasedEvent, (_, _) => Dispatcher.UIThread.Post(() => _activeSlider = null),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, ParameterValue_PointerWheelChanged, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, ParameterValue_KeyDown, RoutingStrategies.Tunnel);
    }

    private void AddFilter(string optionName)
    {
        if (ViewModel is not { } vm) return;
        vm.AddFilterCommand.Execute(optionName);
        // 追加した行は末尾に入って選択される。一覧が長いときも見えるようにする
        if (vm.SelectedFilterLine is { } line) Dispatcher.UIThread.Post(() => FilterList.ScrollIntoView(line), DispatcherPriority.Background);
    }

    private static Slider? ParameterSliderOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<Slider>(includeSelf: true) is { } slider && slider.Classes.Contains("param") ? slider : null;

    private static FilterParameterViewModel? NumberParameterOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is { DataContext: FilterParameterViewModel parameter } box
        && box.Classes.Contains("number") ? parameter : null;

    private void ParameterSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (ParameterSliderOf(e.Source) is not { DataContext: FilterParameterViewModel parameter } slider) return;
        // ドラッグ・クリック中か、キーボードで操作しているときだけ書き戻す
        if (!ReferenceEquals(_activeSlider, slider) && !slider.IsKeyboardFocusWithin) return;
        parameter.NumberValue = e.NewValue;
    }

    /// <summary>数値欄の上のホイールで増減する (刻みは最下位の桁、Shift で10倍。範囲の端で止まる)。</summary>
    private void ParameterValue_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0 || NumberParameterOf(e.Source) is not { } parameter) return;
        parameter.Nudge(e.Delta.Y > 0 ? 1 : -1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    /// <summary>数値欄で ↑↓ (Ctrl の有無を問わない) でも増減する。</summary>
    private void ParameterValue_KeyDown(object? sender, KeyEventArgs e)
    {
        int direction = e.Key switch { Key.Up => 1, Key.Down => -1, _ => 0 };
        if (direction == 0 || NumberParameterOf(e.Source) is not { } parameter) return;
        parameter.Nudge(direction, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    private async void ParameterBrowse_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: FilterParameterViewModel parameter }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = parameter.Name,
            AllowMultiple = false,
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) parameter.ValueText = path;
    }
}

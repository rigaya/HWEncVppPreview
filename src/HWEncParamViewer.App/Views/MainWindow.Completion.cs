using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;

using HWEncParamViewer.App.Localization;

namespace HWEncParamViewer.App.Views;

/// <summary>補完の一覧の1行。分類の見出し行は選択できない。</summary>
public sealed class CompletionEntry
{
    public required string Text { get; init; }
    public string Detail { get; init; } = "";
    public bool IsHeader { get; init; }
    public CompletionItem? Item { get; init; }
}

/// <summary>
/// フィルタ行の補完と、数値の上下ドラッグによる増減 (設計書 21.7)。
/// 何を補完するかは Core の FilterCompletion が決め、ここはポップアップの開閉と操作だけを扱う。
/// </summary>
public partial class MainWindow
{
    // 上下ドラッグで1段変える距離と、ドラッグか文字選択かを決める距離 (画面の画素)
    private const double ScrubStepPixels = 8;
    private const double DragDecisionPixels = 4;

    private TextBox? _completionBox;
    private CompletionContext? _completionContext;
    private CompletionOrder _completionOrder = CompletionOrder.Alphabetical;
    // コードからテキストを書き換えている間は、TextChanged で補完を開き直さない
    private bool _suppressCompletion;
    private PressState? _press;

    /// <summary>フィルタ行の上でボタンを押したときの状態。数値なら上下ドラッグの候補になる。</summary>
    private sealed class PressState(TextBox box, Point start, string text, ValueSpan? span)
    {
        public TextBox Box { get; } = box;
        public Point Start { get; } = start;
        public string OriginalText { get; } = text;
        public ValueSpan? Span { get; } = span;
        public bool IsScrubbing { get; set; }
        public int AppliedSteps { get; set; }
    }

    private void InitializeCompletion()
    {
        // ↑↓ Enter Tab Esc は TextBox より先に受ける (tunnel)
        AddHandler(KeyDownEvent, Completion_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextBox.TextChangedEvent, FilterLine_TextChanged, RoutingStrategies.Bubble);
        // TextBox はポインタを自分で処理してキャプチャするので、tunnel かつ処理済みも受ける
        AddHandler(PointerPressedEvent, Value_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, Value_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, Value_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);

        CompletionList.AddHandler(PointerPressedEvent, CompletionList_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        OrderAlphabeticalButton.Click += (_, _) => SetCompletionOrder(CompletionOrder.Alphabetical);
        OrderCategoryButton.Click += (_, _) => SetCompletionOrder(CompletionOrder.Category);
        UpdateOrderButtons();
    }

    private HelpSchema? Schema => ViewModel?.HelpSchema;

    private static bool IsFilterLine(TextBox box) => box.Classes.Contains("filterLine");

    private static TextBox? FilterLineOf(object? source) =>
        source is Visual visual
            ? (visual as TextBox ?? visual.FindAncestorOfType<TextBox>()) is { } box && IsFilterLine(box) ? box : null
            : null;

    // ---- 開閉 ----

    private void RefreshCompletion(TextBox box)
    {
        if (Schema is not { } schema) { CloseCompletion(); return; }
        var context = FilterCompletion.GetContext(box.Text ?? "", box.CaretIndex, schema);
        if (context == null) { CloseCompletion(); return; }
        // 入力済みの語が唯一の候補と完全に一致するなら、もう補うものがない
        // (値を確定した直後の TextChanged で一覧が開き直るのもこれで防ぐ)
        var items = FilterCompletion.GetItems(context, CompletionOrder.Alphabetical);
        if (items.Count == 1 && items[0].Text == context.Prefix) { CloseCompletion(); return; }
        ShowCompletion(box, context);
    }

    private void ShowCompletion(TextBox box, CompletionContext context)
    {
        bool isOption = context.Kind == CompletionKind.Option;
        var items = FilterCompletion.GetItems(context, isOption ? _completionOrder : CompletionOrder.Alphabetical);
        if (items.Count == 0) { CloseCompletion(); return; }

        var entries = new List<CompletionEntry>();
        string? category = null;
        foreach (var item in items)
        {
            // 用途別の並びでは、分類が変わるところに見出しを入れる
            if (isOption && _completionOrder == CompletionOrder.Category && item.Category != category)
            {
                category = item.Category;
                entries.Add(new CompletionEntry
                {
                    Text = category is { } id ? HelpCategories.DisplayName(id) : Localizer.Instance.Text.Uncategorized,
                    IsHeader = true,
                });
            }
            // 値の候補はパラメータの説明を繰り返さず、既定値の印だけにする
            string description = context.Kind == CompletionKind.ParameterValue ? "" : item.Description;
            entries.Add(new CompletionEntry
            {
                Text = item.Text,
                Detail = item.IsDefault ? (description.Length > 0 ? description + Localizer.Instance.Text.DefaultSuffix : Localizer.Instance.Text.DefaultValue) : description,
                Item = item,
            });
        }

        _completionBox = box;
        _completionContext = context;
        CompletionOrderBar.IsVisible = isOption;
        CompletionList.ItemsSource = entries;
        CompletionList.SelectedIndex = entries.FindIndex(entry => !entry.IsHeader);
        CompletionList.ScrollIntoView(CompletionList.SelectedIndex);

        CompletionPopup.PlacementTarget = box;
        // 語の下に出す。ただし一覧がテキストボックスの右端からはみ出さないようにする
        double width = CompletionBorder.Width is double w && double.IsFinite(w) ? w : 0;
        CompletionPopup.HorizontalOffset = Math.Max(0, Math.Min(CaretX(box, context.ReplaceStart), box.Bounds.Width - width));
        CompletionPopup.IsOpen = true;
    }

    private void CloseCompletion()
    {
        CompletionPopup.IsOpen = false;
        _completionContext = null;
        _completionBox = null;
    }

    private bool IsCompletionOpen => CompletionPopup.IsOpen && _completionBox != null && _completionContext != null;

    private void SetCompletionOrder(CompletionOrder order)
    {
        _completionOrder = order;
        UpdateOrderButtons();
        if (_completionBox is { } box && _completionContext is { } context)
        {
            ShowCompletion(box, context);
            box.Focus();
        }
    }

    private void UpdateOrderButtons()
    {
        OrderAlphabeticalButton.Classes.Set("active", _completionOrder == CompletionOrder.Alphabetical);
        OrderCategoryButton.Classes.Set("active", _completionOrder == CompletionOrder.Category);
    }

    private void ApplyCompletion(CompletionItem item)
    {
        if (_completionBox is not { } box || _completionContext is not { } context) return;
        var result = FilterCompletion.Apply(box.Text ?? "", context, item);
        SetBoxText(box, result.Text, result.Caret);
        if (result.Next is { } next) ShowCompletion(box, next);
        else CloseCompletion();
        box.Focus();
    }

    private void SetBoxText(TextBox box, string text, int caret)
    {
        _suppressCompletion = true;
        try
        {
            box.Text = text;
            box.CaretIndex = Math.Clamp(caret, 0, text.Length);
            box.ClearSelection();
        }
        finally { _suppressCompletion = false; }
    }

    private void MoveCompletionSelection(int direction)
    {
        if (CompletionList.ItemsSource is not List<CompletionEntry> entries || entries.Count == 0) return;
        int index = CompletionList.SelectedIndex;
        for (int i = 0; i < entries.Count; i++)
        {
            index = (index + direction + entries.Count) % entries.Count;
            if (!entries[index].IsHeader) break;
        }
        CompletionList.SelectedIndex = index;
        CompletionList.ScrollIntoView(index);
    }

    // ---- イベント ----

    private void FilterLine_LostFocus(object? sender, RoutedEventArgs e)
    {
        // 一覧のクリックで一時的にフォーカスが外れることがあるので、少し待ってから判定する
        Dispatcher.UIThread.Post(() =>
        {
            if (_completionBox is { } box && !box.IsFocused && !CompletionBorder.IsPointerOver) CloseCompletion();
        }, DispatcherPriority.Background);
    }

    private void FilterLine_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressCompletion || _press?.IsScrubbing == true) return;
        if (FilterLineOf(e.Source) is not { } box || !box.IsFocused) return;
        // CaretIndex は Text の変更通知の後で更新されるので、位置が確定してから判定する
        Dispatcher.UIThread.Post(() => { if (box.IsFocused) RefreshCompletion(box); });
    }

    private void Completion_KeyDown(object? sender, KeyEventArgs e)
    {
        if (FilterLineOf(e.Source) is not { } box) return;
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            RefreshCompletion(box);
            e.Handled = true;
            return;
        }
        if (!IsCompletionOpen || !ReferenceEquals(box, _completionBox)) return;
        switch (e.Key)
        {
            case Key.Down: MoveCompletionSelection(1); break;
            case Key.Up: MoveCompletionSelection(-1); break;
            case Key.Enter or Key.Tab:
                if (CompletionList.SelectedItem is CompletionEntry { Item: { } item }) ApplyCompletion(item);
                else CloseCompletion();
                break;
            case Key.Escape: CloseCompletion(); break;
            case Key.Left or Key.Right or Key.Home or Key.End:
                // カーソルが別の語へ移ったら候補を作り直す (キーは TextBox に処理させる)
                Dispatcher.UIThread.Post(() => { if (box.IsFocused) RefreshCompletion(box); });
                return;
            default: return;
        }
        e.Handled = true;
    }

    private void CompletionList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // フォーカスを移さずに、押した行をそのまま確定する
        e.Handled = true;
        if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: CompletionEntry { Item: { } item } })
        {
            ApplyCompletion(item);
        }
    }

    private void AddLine_Click(object? sender, RoutedEventArgs e)
    {
        // 追加した行へフォーカスを移し、空の行なのでオプションの候補を開く (GotFocus)
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel?.SelectedFilterLine is not { } line) return;
            var box = this.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(textBox => IsFilterLine(textBox) && ReferenceEquals(textBox.DataContext, line));
            box?.Focus();
        }, DispatcherPriority.Background);
    }

    /// <summary>空の行にフォーカスが来たら、オプションの候補を開く。</summary>
    private void OpenCompletionIfEmpty(TextBox box)
    {
        if (string.IsNullOrEmpty(box.Text)) Dispatcher.UIThread.Post(() => { if (box.IsFocused) RefreshCompletion(box); });
    }

    // ---- 値のクリックと上下ドラッグ ----

    private void Value_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (FilterLineOf(e.Source) is not { } box || Schema is not { } schema) return;
        if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed || e.ClickCount != 1) return;
        string text = box.Text ?? "";
        int? index = CharIndexAt(box, e);
        var span = index is int i ? FilterCompletion.ValueAt(text, i, schema) : null;
        _press = new PressState(box, e.GetPosition(this), text, span);
    }

    private void Value_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press) return;
        var delta = e.GetPosition(this) - press.Start;
        if (!press.IsScrubbing)
        {
            if (Math.Abs(delta.X) >= DragDecisionPixels && Math.Abs(delta.X) >= Math.Abs(delta.Y))
            {
                // 先に左右へ動いたら、通常の文字選択に任せる
                _press = null;
                return;
            }
            if (Math.Abs(delta.Y) < DragDecisionPixels || !IsNumeric(press.Span)) return;
            press.IsScrubbing = true;
            press.Box.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            CloseCompletion();
        }
        // 文字選択をさせない
        e.Handled = true;
        int steps = (int)Math.Truncate(-delta.Y / ScrubStepPixels);
        if (steps == press.AppliedSteps) return;
        press.AppliedSteps = steps;
        bool coarse = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        // 押した時点の文字列から毎回やり直す (往復しても元の値に戻る)
        string text = press.OriginalText;
        int caret = press.Span!.Start;
        for (int i = 0; i < Math.Abs(steps); i++)
        {
            var result = NumberNudge.TryNudge(text, caret, Math.Sign(steps), coarse, press.Span.Parameter.Range);
            if (result == null) break;
            text = result.Text;
            caret = result.Caret;
        }
        SetBoxText(press.Box, text, caret);
    }

    private void Value_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is not { } press) return;
        _press = null;
        if (press.IsScrubbing)
        {
            press.Box.Cursor = null;
            e.Handled = true;
            return;
        }
        // 動かさずに離した: 選択式の値なら値の候補を開く
        if (press.Span is { } span && HasChoices(span.Parameter) && Schema is { } schema)
        {
            var box = press.Box;
            Dispatcher.UIThread.Post(() =>
            {
                var context = FilterCompletion.GetContext(box.Text ?? "", span.Start, schema);
                if (context is { Kind: CompletionKind.ParameterValue }) ShowCompletion(box, context);
            });
        }
    }

    private static bool IsNumeric(ValueSpan? span) =>
        span?.Parameter.Type is { } type && (type.Contains("int", StringComparison.Ordinal) || type.Contains("float", StringComparison.Ordinal));

    private static bool HasChoices(HelpValueSchema parameter) =>
        parameter.Values.Count > 0 || parameter.Type == "bool";

    // ---- 文字位置と画面位置の変換 ----

    private static TextPresenter? PresenterOf(TextBox box) => box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();

    private static int? CharIndexAt(TextBox box, PointerEventArgs e)
    {
        if (PresenterOf(box) is not { } presenter) return null;
        var hit = presenter.TextLayout.HitTestPoint(e.GetPosition(presenter));
        return Math.Clamp(hit.TextPosition, 0, (box.Text ?? "").Length);
    }

    /// <summary>文字位置の左端の x 座標 (TextBox 基準)。ポップアップをその語の下に出すのに使う。</summary>
    private static double CaretX(TextBox box, int index)
    {
        if (PresenterOf(box) is not { } presenter) return 0;
        var rect = presenter.TextLayout.HitTestTextPosition(Math.Clamp(index, 0, (box.Text ?? "").Length));
        return presenter.TranslatePoint(new Point(rect.X, 0), box)?.X ?? 0;
    }
}

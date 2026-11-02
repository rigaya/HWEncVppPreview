using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.App.Localization;

namespace HWEncParamViewer.App.Views;

/// <summary>追加の一覧の1行。分類の見出し行は選べない。</summary>
public sealed class CatalogEntry
{
    public string Header { get; init; } = "";
    public bool IsHeader { get; init; }
    public FilterCatalogItem? Item { get; init; }
}

/// <summary>
/// フィルタの追加の一覧 (設計書 23.2)。並び (用途別、分類の中は名前順) は ViewModel の FilterCatalog のまま使い、
/// ここでは見出しの挿入と、絞り込み・選択の操作だけを扱う。選んだら <see cref="FilterChosen"/> でオプション名を渡す。
/// </summary>
public partial class FilterCatalogView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<FilterCatalogView, IEnumerable?>(nameof(Items));

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>一覧から選ばれたオプション名 (例: --vpp-nlmeans)。</summary>
    public event EventHandler<string>? FilterChosen;

    private INotifyCollectionChanged? _observed;

    public FilterCatalogView()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => Rebuild();
        // ↑↓ Enter は TextBox より先に受ける (tunnel)。一覧にフォーカスがあるときも同じ操作にする
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // 行のクリックでそのまま追加する。ListBox が処理済みにするので handledEventsToo で受ける
        CatalogList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ItemsProperty) return;
        if (_observed != null) _observed.CollectionChanged -= OnItemsChanged;
        _observed = Items as INotifyCollectionChanged;
        if (_observed != null) _observed.CollectionChanged += OnItemsChanged;
        Rebuild();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    /// <summary>絞り込みを消して、検索欄にフォーカスを移す (Flyout を開いたとき)。</summary>
    public void Reset()
    {
        SearchBox.Text = "";
        Rebuild();
        SearchBox.Focus();
    }

    private void Rebuild()
    {
        string query = (SearchBox.Text ?? "").Trim();
        var entries = new List<CatalogEntry>();
        string? category = null;
        foreach (var item in Items?.OfType<FilterCatalogItem>() ?? [])
        {
            if (query.Length > 0
                && !item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (item.CategoryName != category)
            {
                category = item.CategoryName;
                entries.Add(new CatalogEntry { Header = category.Length > 0 ? category : Localizer.Instance.Text.Uncategorized, IsHeader = true });
            }
            entries.Add(new CatalogEntry { Item = item });
        }
        CatalogList.ItemsSource = entries;
        CatalogList.SelectedIndex = query.Length > 0 ? entries.FindIndex(entry => !entry.IsHeader) : -1;
    }

    private void MoveSelection(int direction)
    {
        if (CatalogList.ItemsSource is not List<CatalogEntry> entries || entries.Count == 0) return;
        int index = CatalogList.SelectedIndex;
        if (index < 0 && direction < 0) index = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            index = (index + direction + entries.Count) % entries.Count;
            if (!entries[index].IsHeader) break;
        }
        CatalogList.SelectedIndex = index;
        CatalogList.ScrollIntoView(index);
    }

    private void Choose(CatalogEntry? entry)
    {
        if (entry?.Item is { } item) FilterChosen?.Invoke(this, item.OptionName);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: MoveSelection(1); break;
            case Key.Up: MoveSelection(-1); break;
            case Key.Enter: Choose(CatalogList.SelectedItem as CatalogEntry); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: CatalogEntry entry })
        {
            Choose(entry);
        }
    }
}

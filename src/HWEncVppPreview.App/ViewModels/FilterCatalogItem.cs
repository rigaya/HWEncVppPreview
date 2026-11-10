namespace HWEncVppPreview.App.ViewModels;

/// <summary>追加候補の表示と、同じオプションが一覧に存在するかを保持する。</summary>
public sealed class FilterCatalogItem(string optionName, string displayName, string categoryName, string description) : ViewModelBase
{
    private bool _isInUse;
    public string OptionName { get; } = optionName;
    public string DisplayName { get; } = displayName;
    public string CategoryName { get; } = categoryName;
    public string Description { get; } = description;
    public bool IsInUse { get => _isInUse; internal set => SetProperty(ref _isInUse, value); }
}

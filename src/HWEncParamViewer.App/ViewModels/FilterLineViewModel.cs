using System.Collections.ObjectModel;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

/// <summary>フィルタ編集欄の1行 (設計書 17.5)。</summary>
public sealed class FilterLineViewModel : ViewModelBase
{
    private bool _isEnabled = true;
    private string _text = "";
    private HelpSchema? _schema;
    private HelpOptionSchema? _parameterOption;
    private string _displayName = "", _categoryName = "", _summary = "", _formUnavailableReason = AppMessages.SchemaUnavailable;
    private bool _isFormAvailable;

    public string DisplayName { get => _displayName; private set => SetProperty(ref _displayName, value); }
    public string CategoryName { get => _categoryName; private set => SetProperty(ref _categoryName, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public bool IsFormAvailable { get => _isFormAvailable; private set => SetProperty(ref _isFormAvailable, value); }
    public string FormUnavailableReason { get => _formUnavailableReason; private set => SetProperty(ref _formUnavailableReason, value); }
    public ObservableCollection<FilterParameterViewModel> Parameters { get; } = new();

    public HelpSchema? HelpSchema
    {
        get => _schema;
        set
        {
            if (ReferenceEquals(_schema, value)) return;
            _schema = value;
            RefreshForm();
        }
    }

    /// <summary>表示言語を変えたときだけ欄を作り直し、入力済みのテキストは保つ。</summary>
    internal void RefreshLanguage()
    {
        _parameterOption = null;
        RefreshForm();
    }

    private void RefreshForm()
    {
        DisplayName = OptionName.StartsWith("--vpp-", StringComparison.OrdinalIgnoreCase) ? OptionName[6..] : OptionName;
        var info = _schema is null ? null : FilterLineEditor.Read(_text, _schema);
        CategoryName = HelpCategories.DisplayName(info?.Option?.Category);
        Summary = info is null ? "" : FilterLineEditor.Summary(info);
        IsFormAvailable = info is not null && info.Form != FilterLineForm.Unavailable;
        FormUnavailableReason = info is null ? AppMessages.SchemaUnavailable : info.UnavailableReason ?? "";
        var option = info?.Option;
        if (!ReferenceEquals(_parameterOption, option))
        {
            _parameterOption = option;
            Parameters.Clear();
            if (option is not null && !option.IsNameOnly)
            {
                if (option.Parameters.Count > 0)
                {
                    foreach (var parameter in option.Parameters)
                    {
                        string name = parameter.Name;
                        Parameters.Add(new(parameter, value =>
                        {
                            if (_schema is not null) Text = FilterLineEditor.SetValue(Text, _schema, name, value);
                        }));
                    }
                }
                else if (option.Type is not null)
                    Parameters.Add(new(option, value =>
                    {
                        if (_schema is not null) Text = FilterLineEditor.SetShortValue(Text, _schema, value);
                    }));
            }
        }
        foreach (var parameter in Parameters)
        {
            string? value = info?.Form == FilterLineForm.ShortValue ? info.ShortValue
                : info is not null && info.Values.TryGetValue(parameter.Name, out string? found) ? found : null;
            parameter.Update(value);
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string Text
    {
        get => _text;
        set
        {
            string text = value ?? "";
            if (_text == text) return;
            _text = text;
            RefreshForm();
            OnPropertyChanged();
            OnPropertyChanged(nameof(OptionName));
        }
    }

    /// <summary>行頭の `--vpp-xxx` の部分。ヘルプの検索に使う。</summary>
    public string OptionName
    {
        get
        {
            var t = _text.TrimStart();
            int end = t.IndexOfAny([' ', '\t', '=']);
            return end < 0 ? t : t[..end];
        }
    }

    /// <summary>
    /// caret 位置の数値を増減する (設計書 4.2)。direction は +1/-1、coarse は Shift (10倍)。
    /// 変更したら Text を書き換えて新しいカーソル位置を返す。数値がなければ null。
    /// </summary>
    public int? Nudge(int caret, int direction, bool coarse)
    {
        var result = NumberNudge.TryNudge(Text, caret, direction, coarse);
        if (result == null) return null;
        Text = result.Text;
        return result.Caret;
    }
}

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
    /// <summary>mainの指定順と、明示された残りの項目。要素はParametersと共用する。</summary>
    public ObservableCollection<FilterParameterViewModel> MainParameters { get; } = new();
    /// <summary>折りたたむ未指定の項目。helpの宣言順を保つ。</summary>
    public ObservableCollection<FilterParameterViewModel> OtherParameters { get; } = new();
    public int OtherParameterCount => OtherParameters.Count;

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
            parameter.UpdateCondition(IsConditionSatisfied(parameter.Condition, info));
        }
        RefreshParameterGroups();
    }

    private static bool IsConditionSatisfied(HelpCondition? condition, FilterLineInfo? info)
    {
        if (condition is null || info?.Option is null) return true;
        bool written = info.Values.TryGetValue(condition.Parameter, out string? explicitValue);
        if (condition.Kind == HelpConditionKind.Present) return written;
        string? effective = written ? explicitValue : info.Option.Parameters
            .FirstOrDefault(parameter => parameter.Name.Equals(condition.Parameter, StringComparison.OrdinalIgnoreCase))?.DefaultValue;
        if (effective is null) return false;
        bool matches = condition.Values.Any(pattern => pattern.EndsWith('*')
            ? effective.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : effective.Equals(pattern, StringComparison.OrdinalIgnoreCase));
        return condition.Kind == HelpConditionKind.NotEquals ? !matches : matches;
    }

    private void RefreshParameterGroups()
    {
        var visible = Parameters.Where(parameter => parameter.IsConditionSatisfied || parameter.IsSet).ToArray();
        var main = new List<FilterParameterViewModel>();
        var selected = new HashSet<FilterParameterViewModel>();
        if (_parameterOption?.MainParameters.Count > 0)
        {
            foreach (string name in _parameterOption.MainParameters)
            {
                var parameter = visible.FirstOrDefault(item => item.Name == name);
                if (parameter is not null && selected.Add(parameter)) main.Add(parameter);
            }
            foreach (var parameter in visible)
                if (parameter.IsSet && selected.Add(parameter)) main.Add(parameter);
        }
        else
        {
            main.AddRange(visible);
            selected.UnionWith(visible);
        }
        var other = visible.Where(parameter => !selected.Contains(parameter)).ToArray();
        int oldOtherCount = OtherParameters.Count;
        // 古い一覧から先に外し、同じ編集項目を二つの一覧へ重複して入れない。
        RemoveUnwanted(MainParameters, selected);
        RemoveUnwanted(OtherParameters, other.ToHashSet());
        Reorder(MainParameters, main);
        Reorder(OtherParameters, other);
        if (oldOtherCount != OtherParameters.Count) OnPropertyChanged(nameof(OtherParameterCount));
    }

    private static void RemoveUnwanted(ObservableCollection<FilterParameterViewModel> collection,
        HashSet<FilterParameterViewModel> wanted)
    {
        for (int index = collection.Count - 1; index >= 0; --index)
            if (!wanted.Contains(collection[index])) collection.RemoveAt(index);
    }

    private static void Reorder(ObservableCollection<FilterParameterViewModel> collection,
        IReadOnlyList<FilterParameterViewModel> wanted)
    {
        // テキスト入力では欄を作り直さず、既存の要素を移動・追加する。
        for (int index = 0; index < wanted.Count; ++index)
        {
            if (index < collection.Count && ReferenceEquals(collection[index], wanted[index])) continue;
            int current = collection.IndexOf(wanted[index]);
            if (current >= 0) collection.Move(current, index);
            else collection.Insert(index, wanted[index]);
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

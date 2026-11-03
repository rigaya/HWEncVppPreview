using System.Globalization;
using System.Windows.Input;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public enum FilterParameterKind { Slider, Number, NumberOrChoice, Choice, Bool, Path, Text, FromTo }

/// <summary>
/// 語と数値の両方を取る欄 (<c>int|auto|off</c> など) の、語の1つ。語ごとのボタンと「数値」のボタンで、どれか1つを選ぶ (設計書 23.3)。
/// </summary>
public sealed class ChoiceOptionViewModel : ViewModelBase
{
    private bool _isSelected;
    internal ChoiceOptionViewModel(string text, bool isDefault, Action select)
    {
        Text = text;
        IsDefault = isDefault;
        SelectCommand = new RelayCommand(select);
    }
    public string Text { get; }
    public bool IsDefault { get; }
    public bool IsSelected { get => _isSelected; internal set => SetProperty(ref _isSelected, value); }
    public ICommand SelectCommand { get; }
}

/// <summary>スキーマの1項目を表示し、操作した項目だけを行のテキストへ戻す。</summary>
public sealed class FilterParameterViewModel : ViewModelBase
{
    private readonly HelpValueSchema _schema;
    private readonly Action<string?> _write;
    private string? _valueText;
    private string? _validationMessage;

    internal FilterParameterViewModel(HelpValueSchema schema, Action<string?> write)
    {
        _schema = schema;
        _write = write;
        Minimum = ParseNumber(schema.Range?.Minimum);
        Maximum = ParseNumber(schema.Range?.Maximum);
        string[] types = schema.Type?.Split('|') ?? [];
        bool numeric = types.Any(type => type is "int" or "float");
        Kind = !schema.IsNameOnly && schema.IsFromTo && schema.Values.Count > 0 ? FilterParameterKind.FromTo
            : schema.IsNameOnly || schema.IsStructured || schema.IsDerivedDefault ? FilterParameterKind.Text
            : types.Contains("bool") ? types.Length == 1 ? FilterParameterKind.Bool : FilterParameterKind.Choice
            : numeric && types.Any(type => type is not ("int" or "float")) ? FilterParameterKind.NumberOrChoice
            : numeric && Minimum.HasValue && Maximum.HasValue ? FilterParameterKind.Slider
            : numeric ? FilterParameterKind.Number
            : schema.Values.Count > 0 ? FilterParameterKind.Choice
            : types.Contains("path") ? FilterParameterKind.Path : FilterParameterKind.Text;
        Choices = types.Contains("bool")
            ? Array.AsReadOnly(schema.Values.Concat(["true", "false"]).Distinct(StringComparer.Ordinal).ToArray())
            : schema.Values;
        string formatted = FilterLineEditor.FormatNumber(0, schema);
        int dot = formatted.IndexOf('.');
        Step = numeric ? Math.Pow(10, dot < 0 ? 0 : -(formatted.Length - dot - 1)) : 1;
        ResetCommand = new RelayCommand(() =>
        {
            _numberRequested = false;
            _hasPendingFromTo = false;
            _pendingFrom = _pendingTo = null;
            ValueText = null;
            RaiseChoiceState();
            RaiseFromToState();
        });
        Words = Kind == FilterParameterKind.NumberOrChoice
            ? Array.AsReadOnly(schema.Values.Select(word => new ChoiceOptionViewModel(word, word == schema.DefaultValue, () =>
            {
                _numberRequested = false;
                ChoiceValue = word;
                RaiseChoiceState();
            })).ToArray())
            : Array.Empty<ChoiceOptionViewModel>();
        SelectNumberCommand = new RelayCommand(() =>
        {
            _numberRequested = true;
            RaiseChoiceState();
        });
        RangeText = Minimum is null && Maximum is null ? ""
            : $"{schema.Range?.Minimum ?? ""}..{schema.Range?.Maximum ?? ""}";
        RaiseChoiceState();
    }

    // 「数値」を押したが、まだ数値を書いていない状態。行は変えずに、数値の欄だけを使えるようにする
    private bool _numberRequested;
    private bool _hasPendingFromTo;
    private string? _pendingFrom, _pendingTo;
    private bool _conditionSatisfied = true, _isInactive;

    /// <summary>明示値があるが、現在の条件では効かない項目。</summary>
    public bool IsInactive { get => _isInactive; private set => SetProperty(ref _isInactive, value); }
    internal bool IsConditionSatisfied => _conditionSatisfied;
    internal HelpCondition? Condition => _schema.Condition;
    internal void UpdateCondition(bool satisfied)
    {
        _conditionSatisfied = satisfied;
        IsInactive = !satisfied && IsSet;
    }

    /// <summary>構造値の変換元と変換先の候補。両側が揃うまで行へ書かない。</summary>
    public IReadOnlyList<string> FromChoices => Kind == FilterParameterKind.FromTo ? _schema.Values : Array.Empty<string>();
    public IReadOnlyList<string> ToChoices => FromChoices;
    public string? FromValue
    {
        get => _hasPendingFromTo ? _pendingFrom : ReadFromTo().From;
        set => SetFromTo(true, value);
    }
    public string? ToValue
    {
        get => _hasPendingFromTo ? _pendingTo : ReadFromTo().To;
        set => SetFromTo(false, value);
    }

    private (string? From, string? To) ReadFromTo()
    {
        string[] parts = (_valueText ?? _schema.DefaultValue)?.Split(':') ?? [];
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, null);
    }

    private void SetFromTo(bool from, string? value)
    {
        // 初期化時のnull通知で既存値を消さず、削除はリセットで行う。
        if (Kind != FilterParameterKind.FromTo || value is null || !FromChoices.Contains(value)) return;
        if (value == (from ? FromValue : ToValue)) return;
        _pendingFrom = FromValue;
        _pendingTo = ToValue;
        if (from) _pendingFrom = value;
        else _pendingTo = value;
        _hasPendingFromTo = true;
        RaiseFromToState();
        if (_pendingFrom is null || _pendingTo is null) return;
        string pair = _pendingFrom + ":" + _pendingTo;
        ValueText = pair;
        if (_valueText != pair)
        {
            // 書き戻せなかった場合は、行のテキストを正として選択を戻す。
            _hasPendingFromTo = false;
            _pendingFrom = _pendingTo = null;
            RaiseFromToState();
        }
    }

    private void RaiseFromToState()
    {
        if (Kind != FilterParameterKind.FromTo) return;
        OnPropertyChanged(nameof(FromValue));
        OnPropertyChanged(nameof(ToValue));
    }


    /// <summary>NumberOrChoice の語の選択肢 (help の values:)。それ以外の種類では空。</summary>
    public IReadOnlyList<ChoiceOptionViewModel> Words { get; }
    /// <summary>NumberOrChoice で数値を選んでいるか (書かれた値・既定値が数値、または「数値」を押した)。</summary>
    public bool IsNumberSelected => _numberRequested || IsNumber(_valueText ?? _schema.DefaultValue);
    /// <summary>NumberOrChoice の数値の欄。語が書かれているときは空。</summary>
    public string? NumberText
    {
        get => IsNumber(_valueText) ? _valueText : null;
        set => ValueText = value;
    }
    /// <summary>範囲の表示 (例: -1..65535)。範囲がなければ空。</summary>
    public string RangeText { get; }
    /// <summary>NumberOrChoice の数値の欄の薄い表示。既定値が数値ならそれ、そうでなければ範囲。</summary>
    public string NumberPlaceholder => IsNumber(_schema.DefaultValue) ? _schema.DefaultValue! : RangeText;
    public ICommand SelectNumberCommand { get; }

    /// <summary>数値の欄からフォーカスが外れたとき。数値を書かなかったなら、「数値」を押す前の表示に戻す。</summary>
    public void EndNumberEntry()
    {
        if (!_numberRequested || IsNumber(_valueText)) return;
        _numberRequested = false;
        RaiseChoiceState();
    }

    private void RaiseChoiceState()
    {
        if (Kind != FilterParameterKind.NumberOrChoice) return;
        string? effective = _valueText ?? _schema.DefaultValue;
        foreach (var word in Words) word.IsSelected = !IsNumberSelected && word.Text == effective;
        OnPropertyChanged(nameof(IsNumberSelected));
        OnPropertyChanged(nameof(NumberText));
    }

    private static bool IsNumber(string? text) => ParseNumber(text) is not null;

    public string Name => _schema.Name;
    public string Description => _schema.Description;
    public FilterParameterKind Kind { get; }
    public double? Minimum { get; }
    public double? Maximum { get; }
    public double Step { get; }
    public IReadOnlyList<string> Choices { get; }
    public string DefaultText => _schema.IsDerivedDefault ? AppMessages.Automatic : _schema.DefaultValue ?? "";
    public bool IsSet => _valueText is not null;
    public string? ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }
    public string? ValueText
    {
        get => _valueText;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) value = null;
            // エンコーダは引用符の内側でもカンマを区切るため、表現できない値を行へ書かない。
            if (value?.Contains(',') == true)
            {
                ValidationMessage = AppMessages.CommaUnavailable;
                return;
            }
            ValidationMessage = null;
            if (_valueText != value) _write(value);
        }
    }
    public double NumberValue
    {
        get => ParseNumber(_valueText) ?? ParseNumber(_schema.DefaultValue) ?? 0;
        set
        {
            if (!double.IsFinite(value)) return;
            if (Minimum is double low) value = Math.Max(low, value);
            if (Maximum is double high) value = Math.Min(high, value);
            string formatted = FilterLineEditor.FormatNumber(value, _schema);
            if (ParseNumber(formatted) == NumberValue) return;
            ValueText = formatted;
        }
    }
    public bool BoolValue
    {
        get => (_valueText ?? _schema.DefaultValue)?.ToLowerInvariant() is "true" or "on" or "1";
        set { if (value != BoolValue) ValueText = value ? "true" : "false"; }
    }
    public string? ChoiceValue
    {
        get => _valueText ?? _schema.DefaultValue;
        set { if (value is not null && value != ChoiceValue) ValueText = value; }
    }
    public ICommand ResetCommand { get; }

    public void Nudge(int direction, bool coarse = false)
    {
        string text = _valueText ?? _schema.DefaultValue ?? FilterLineEditor.FormatNumber(0, _schema);
        var changed = NumberNudge.TryNudge(text, text.Length, direction, coarse, _schema.Range);
        if (changed is not null) ValueText = changed.Text;
    }

    /// <summary>テキストからの更新では書き戻し用setterを呼ばず、循環を防ぐ。</summary>
    internal void Update(string? value)
    {
        ValidationMessage = null;
        if (_valueText == value) return;
        _valueText = value;
        _hasPendingFromTo = false;
        _pendingFrom = _pendingTo = null;
        RaiseFromToState();
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(IsSet));
        OnPropertyChanged(nameof(NumberValue));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(ChoiceValue));
        // 語が書かれたら「数値」の選択は解く。消えた (null) ときは解かない (数値を打ち直す途中で欄が使えなくならないように)
        if (value is not null && !IsNumber(value)) _numberRequested = false;
        RaiseChoiceState();
    }

    private static double? ParseNumber(string? text) => double.TryParse(text, NumberStyles.Float,
        CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
}

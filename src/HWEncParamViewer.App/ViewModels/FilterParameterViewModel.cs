using System.Globalization;
using System.Windows.Input;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public enum FilterParameterKind { Slider, Number, NumberOrChoice, Choice, Bool, Path, Text }

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
        Kind = schema.IsNameOnly || schema.IsStructured || schema.IsDerivedDefault ? FilterParameterKind.Text
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
        ResetCommand = new RelayCommand(() => ValueText = null);
    }

    public string Name => _schema.Name;
    public string Description => _schema.Description;
    public FilterParameterKind Kind { get; }
    public double? Minimum { get; }
    public double? Maximum { get; }
    public double Step { get; }
    public IReadOnlyList<string> Choices { get; }
    public string DefaultText => _schema.IsDerivedDefault ? "自動" : _schema.DefaultValue ?? "";
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
                ValidationMessage = "値にカンマは使えません。エンコーダがパラメータの区切りとして扱います。";
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
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(IsSet));
        OnPropertyChanged(nameof(NumberValue));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(ChoiceValue));
    }

    private static double? ParseNumber(string? text) => double.TryParse(text, NumberStyles.Float,
        CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
}

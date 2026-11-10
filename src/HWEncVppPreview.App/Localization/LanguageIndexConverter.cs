using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace HWEncVppPreview.App.Localization;

/// <summary>固定表示名の言語選択欄を、設定ファイルの言語識別子へ接続する。</summary>
public sealed class LanguageIndexConverter : IValueConverter
{
    public static LanguageIndexConverter Instance { get; } = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch { "ja" => 1, "en" => 2, _ => 0 };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index ? index switch { 0 => "auto", 1 => "ja", 2 => "en", _ => BindingOperations.DoNothing } : BindingOperations.DoNothing;
}

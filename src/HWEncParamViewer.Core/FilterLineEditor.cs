using System.Collections.ObjectModel;
using System.Globalization;
using static HWEncParamViewer.Core.FilterLineSyntax;

namespace HWEncParamViewer.Core;

public enum FilterLineForm { Parameters, ShortValue, None, Unavailable }

/// <summary>テキストを正としたフォームの読み取り結果。値は外側の引用符を外したもの。</summary>
public sealed record FilterLineInfo(string OptionName, HelpOptionSchema? Option, FilterLineForm Form,
    string? UnavailableReason, IReadOnlyDictionary<string, string> Values, string? ShortValue, IReadOnlyList<string> Unknown);

/// <summary>補完と同じ行解析を使い、対象以外の文字を保ってフィルタ行を編集する。</summary>
public static class FilterLineEditor
{
    public static FilterLineInfo Read(string text, HelpSchema schema)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schema);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        var line = ReadLine(text, 0, schema);
        string optionName = line is null ? "" : text[line.OptionStart..line.OptionEnd];
        var option = line?.Option;
        FilterLineInfo Result(FilterLineForm form, string? reason = null, string? shortValue = null) =>
            new(optionName, option, form, reason, new ReadOnlyDictionary<string, string>(values), shortValue, unknown.AsReadOnly());
        if (text.Contains('\n') || text.Contains('\r') || line is null || option is null || option.IsNameOnly)
            return Result(FilterLineForm.Unavailable, CoreText.FormSchemaUnavailable);
        var args = Tokenize(text);
        if (args.Count is < 1 or > 2)
            return Result(FilterLineForm.Unavailable, CoreText.FormMultipleArguments);
        if (option.Type is not null)
        {
            // 値とパラメータ列の混在形式は、推測で書き換えない。
            if (option.Parameters.Count > 0)
                return Result(FilterLineForm.Unavailable, CoreText.FormMixedArguments);
            return Result(FilterLineForm.ShortValue, shortValue: args.Count == 2 ? args[1] : null);
        }
        if (option.Parameters.Count == 0)
            return args.Count == 1 ? Result(FilterLineForm.None)
                : Result(FilterLineForm.Unavailable, CoreText.FormNoArguments);
        foreach (var segment in line.Segments)
        {
            int start = SkipSpace(text, segment.Start, segment.End);
            int end = TrimSpace(text, start, segment.End);
            if (start == end && line.Segments.Count == 1) continue;
            if (segment.Equal < start)
                return Result(FilterLineForm.Unavailable, CoreText.FormParameterInvalid);
            string key = text[start..TrimSpace(text, start, segment.Equal)];
            IReadOnlyList<string> tokens = line.ArgumentQuoted
                ? new[] { text[SkipSpace(text, segment.Equal + 1, end)..end].Replace("\"\"", "\"") }
                : Tokenize(text[SkipSpace(text, segment.Equal + 1, end)..end], allowComment: false);
            string value = tokens.Count == 0 ? "" : tokens[0];
            if (tokens.Count > 1 || value.Contains(','))
                return Result(FilterLineForm.Unavailable, CoreText.FormCommaUnavailable);
            if (key.Length == 0 || !values.TryAdd(key, value))
                return Result(FilterLineForm.Unavailable, CoreText.FormDuplicateParameter);
            if (!option.Parameters.Any(parameter => parameter.Name.Equals(key, StringComparison.OrdinalIgnoreCase))) unknown.Add(key);
        }
        return Result(FilterLineForm.Parameters);
    }

    public static string SetValue(string text, HelpSchema schema, string name, string? value)
    {
        var info = Read(text, schema);
        if (info.Form != FilterLineForm.Parameters || value?.Contains(',') == true || HasNewline(value)
            || !info.Option!.Parameters.Any(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return text;
        var line = ReadLine(text, 0, schema)!;
        var parts = line.Segments.Where(part => SkipSpace(text, part.Start, part.End) < part.End).ToArray();
        int index = Array.FindIndex(parts, part => part.Equal >= 0 &&
            text[SkipSpace(text, part.Start, part.Equal)..TrimSpace(text, part.Start, part.Equal)].Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var part = parts[index];
            if (value is not null)
            {
                int start = SkipSpace(text, part.Equal + 1, part.End);
                int end = TrimSpace(text, start, part.End);
                return text[..start] + (line.ArgumentQuoted ? value.Replace("\"", "\"\"") : Quote(value)) + text[end..];
            }
            if (parts.Length == 1) return text[..line.OptionEnd];
            // 先頭では次のカンマ、それ以外では直前のカンマを対象と一緒に除く。
            int from = index == 0 ? SkipSpace(text, part.Start, part.End) : part.Start - 1;
            int to = index == 0 ? part.End + 1 : part.End;
            string prefix = text[..from];
            return prefix + text[to..];
        }
        if (value is null) return text;
        int insertion = TrimSpace(text, line.OptionEnd, line.Segments[^1].End);
        return text[..insertion] + (parts.Length == 0 ? (line.ArgumentQuoted ? "" : " ") : ",") + name + "=" + (line.ArgumentQuoted ? value.Replace("\"", "\"\"") : Quote(value)) + text[insertion..];
    }

    public static string SetShortValue(string text, HelpSchema schema, string? value)
    {
        var info = Read(text, schema);
        if (info.Form != FilterLineForm.ShortValue || HasNewline(value)) return text;
        var line = ReadLine(text, 0, schema)!;
        if (value is null) return text[..line.OptionEnd];
        int start = SkipSpace(text, line.OptionEnd, text.Length);
        int end = TrimSpace(text, start, text.Length);
        return start == text.Length ? text[..line.OptionEnd] + " " + Quote(value)
            : text[..start] + Quote(value) + text[end..];
    }

    public static string Summary(FilterLineInfo info) => info.Form == FilterLineForm.ShortValue
        ? info.ShortValue ?? "" : string.Join(", ", info.Values.Select(pair => pair.Key + "=" + pair.Value));

    public static string FormatNumber(double value, HelpValueSchema parameter)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        int digits = parameter.Type?.Split('|').Contains("int", StringComparer.Ordinal) == true ? 0
            : new[] { parameter.DefaultValue, parameter.Range?.Minimum, parameter.Range?.Maximum }.Max(DecimalPlaces);
        return value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static int DecimalPlaces(string? text)
    {
        if (text is null) return 0;
        int exponentAt = text.IndexOfAny(['e', 'E']);
        string mantissa = exponentAt < 0 ? text : text[..exponentAt];
        int exponent = exponentAt >= 0 && int.TryParse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        int dot = mantissa.IndexOf('.');
        return (int)Math.Clamp((long)(dot < 0 ? 0 : mantissa.Length - dot - 1) - exponent, 0, 324);
    }
    private static bool HasNewline(string? value) => value?.IndexOfAny(['\r', '\n']) >= 0;
    private static string Quote(string value) => value.Length == 0 || value.Any(character => char.IsWhiteSpace(character) || character == '"')
        ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

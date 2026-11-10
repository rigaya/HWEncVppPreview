using System.Globalization;
using static HWEncVppPreview.Core.FilterLineSyntax;

namespace HWEncVppPreview.Core;

public enum CompletionKind { Option, ParameterName, ParameterValue }
public enum CompletionOrder { Alphabetical, Category }

/// <summary>カーソル位置の補完対象。範囲は元テキストのインデックスで表す。</summary>
public sealed record CompletionContext(CompletionKind Kind, int ReplaceStart, int ReplaceLength, string Prefix,
    HelpOptionSchema? Option, HelpValueSchema? Parameter)
{
    internal HelpSchema? Schema { get; init; }
    internal string? Source { get; init; }
    internal IReadOnlySet<string> UsedParameters { get; init; } = new HashSet<string>();
}

public sealed record CompletionItem(string Text, string? Category, string Description, bool IsDefault = false);
public sealed record CompletionResult(string Text, int Caret, CompletionContext? Next);
/// <summary>値の字句範囲。引用符を含み、Parameterは短い形式ならオプション自身を返す。</summary>
public sealed record ValueSpan(int Start, int Length, HelpValueSchema Parameter);

/// <summary>help分類の表示名と表示順。</summary>
public static class HelpCategories
{
    private static readonly (string Id, Func<string> Name)[] Categories =
    [
        ("deinterlace", () => CoreText.CategoryDeinterlace), ("denoise", () => CoreText.CategoryDenoise), ("edge", () => CoreText.CategoryEdge),
        ("deband", () => CoreText.CategoryDeband), ("color", () => CoreText.CategoryColor), ("resize", () => CoreText.CategoryResize),
        ("framerate", () => CoreText.CategoryFramerate), ("overlay", () => CoreText.CategoryOverlay), ("ai", () => CoreText.CategoryAi), ("other", () => CoreText.CategoryOther)
    ];
    public static string DisplayName(string? id) => Categories.FirstOrDefault(category => category.Id == id).Name?.Invoke() ?? id ?? "";
    internal static int Order(string? id)
    {
        int index = Array.FindIndex(Categories, category => category.Id == id);
        return index < 0 ? Categories.Length : index;
    }
}

/// <summary>フィルタ行の補完と値の位置を、副作用なしで計算する。</summary>
public static class FilterCompletion
{
    public static CompletionContext? GetContext(string text, int caret, HelpSchema schema)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schema);
        var line = ReadLine(text, caret, schema);
        if (line is null) return null;
        CompletionContext Context(CompletionKind kind, int start, int end, string prefix, HelpValueSchema? parameter = null) =>
            new(kind, start, end - start, prefix, line.Option, parameter) { Schema = schema, Source = text };
        if (caret <= line.OptionEnd)
        {
            if (caret < line.OptionStart) return null;
            string prefix = text[line.OptionStart..caret];
            if (prefix.Length > 0 && !"--vpp-".StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !prefix.StartsWith("--vpp-", StringComparison.OrdinalIgnoreCase)) return null;
            return Context(CompletionKind.Option, line.OptionStart, line.OptionEnd, prefix);
        }
        if (line.Option is null) return null;
        Segment? segment = line.Segments.FirstOrDefault(part => caret >= part.Start && caret <= part.End);
        if (segment is null) return null;
        int start = SkipSpace(text, segment.Start, segment.End);
        int end = TrimSpace(text, start, segment.End);
        // 区切り直後の空白も、同じ空の入力欄として扱う。
        int position = Math.Clamp(caret, start, end);
        if (segment.Equal >= 0 && position > segment.Equal)
        {
            string key = text[start..TrimSpace(text, start, segment.Equal)];
            var parameter = line.Option.Parameters.FirstOrDefault(value => value.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (parameter is null) return null;
            return ValueContext(text, position, SkipSpace(text, segment.Equal + 1, end), end,
                (a, b, prefix) => Context(CompletionKind.ParameterValue, a, b, prefix, parameter));
        }
        if (segment.Equal < 0 && ReferenceEquals(segment, line.Segments[0]) && line.Option.Type is not null)
            return ValueContext(text, position, start, end,
                (a, b, prefix) => Context(CompletionKind.ParameterValue, a, b, prefix, line.Option));
        int keyEnd = segment.Equal >= 0 ? TrimSpace(text, start, segment.Equal) : end;
        string keyPrefix = text[start..Math.Clamp(position, start, keyEnd)];
        if (keyPrefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')) return null;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in line.Segments)
        {
            if (ReferenceEquals(other, segment) || other.Equal < 0) continue;
            used.Add(text[SkipSpace(text, other.Start, other.Equal)..TrimSpace(text, other.Start, other.Equal)]);
        }
        return Context(CompletionKind.ParameterName, start, keyEnd, keyPrefix) with { UsedParameters = used };
    }

    public static IReadOnlyList<CompletionItem> GetItems(CompletionContext context, CompletionOrder order)
    {
        ArgumentNullException.ThrowIfNull(context);
        IEnumerable<CompletionItem> items;
        switch (context.Kind)
        {
            case CompletionKind.Option:
                items = context.Schema?.Options.Select(option => Item(option.Name, option.Category, option.Description)) ?? [];
                items = order == CompletionOrder.Category
                    ? items.OrderBy(item => HelpCategories.Order(item.Category)).ThenBy(item => item.Text, StringComparer.Ordinal)
                    : items.OrderBy(item => item.Text, StringComparer.Ordinal);
                break;
            case CompletionKind.ParameterName:
                items = context.Option?.Parameters.Where(parameter => !context.UsedParameters.Contains(parameter.Name))
                    .OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
                    .Select(parameter => Item(parameter.Name, context.Option.Category, parameter.Description)) ?? [];
                break;
            case CompletionKind.ParameterValue:
                var value = context.Parameter;
                if (value is null || value.IsNameOnly || value.IsStructured) return Array.Empty<CompletionItem>();
                IEnumerable<string> values = value.Values;
                if (HasType(value, "bool")) values = values.Concat(["true", "false"]);
                items = values.Distinct(StringComparer.Ordinal).Select(candidate =>
                    Item(candidate, context.Option?.Category, value.Description, IsDefault(value, candidate)));
                break;
            default: return Array.Empty<CompletionItem>();
        }
        return Array.AsReadOnly(items.Where(item => item.Text.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    public static CompletionResult Apply(string text, CompletionContext context, CompletionItem item)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);
        if (context.ReplaceStart < 0 || context.ReplaceLength < 0 || context.ReplaceStart > text.Length - context.ReplaceLength
            || (context.Source is not null && context.Source != text))
            return new(text, Math.Clamp(context.ReplaceStart, 0, text.Length), null);
        string insert = item.Text;
        bool next = false;
        int assignment = SkipSpace(text, context.ReplaceStart + context.ReplaceLength, text.Length);
        bool existingAssignment = context.Kind == CompletionKind.ParameterName && assignment < text.Length && text[assignment] == '=';
        if (context.Kind == CompletionKind.Option)
        {
            insert += " ";
            next = true;
        }
        else if (context.Kind == CompletionKind.ParameterName)
        {
            var parameter = context.Option?.Parameters.FirstOrDefault(value => value.Name == item.Text);
            if (!existingAssignment) insert += "=";
            if (!existingAssignment && parameter is not null && !parameter.IsNameOnly && !parameter.IsStructured)
            {
                bool boolean = parameter.Type == "bool";
                if (parameter.Values.Count > 0 && !boolean) next = true;
                else if (!parameter.IsDerivedDefault && parameter.DefaultValue is { } defaultValue && (boolean || IsNumeric(parameter, defaultValue))) insert += defaultValue;
            }
        }
        string result = text[..context.ReplaceStart] + insert + text[(context.ReplaceStart + context.ReplaceLength)..];
        int caret = context.ReplaceStart + insert.Length;
        if (existingAssignment && context.Schema is not null)
        {
            // 名前だけの置換では入力済みの値を保ち、必要なら全候補を開く。
            int valueStart = assignment + insert.Length - context.ReplaceLength + 1;
            var valueContext = GetContext(result, valueStart, context.Schema);
            var span = ValueAt(result, valueStart, context.Schema);
            caret = span is null ? valueStart : span.Start + span.Length;
            var valueNext = valueContext is not null && GetItems(valueContext, CompletionOrder.Alphabetical).Count > 0 ? valueContext : null;
            return new(result, caret, valueNext);
        }
        // オプション確定時の既存空白を二重にせず、元の引数はそのまま残す。
        if (context.Kind == CompletionKind.Option && caret < result.Length && char.IsWhiteSpace(result[caret]) && result[caret] is not '\r' and not '\n')
            result = result.Remove(caret, 1);
        var follow = next && context.Schema is not null ? GetContext(result, caret, context.Schema) : null;
        if (follow is not null && follow.Kind == CompletionKind.ParameterValue && GetItems(follow, CompletionOrder.Alphabetical).Count == 0) follow = null;
        return new(result, caret, follow);
    }

    public static ValueSpan? ValueAt(string text, int caret, HelpSchema schema)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schema);
        var line = ReadLine(text, caret, schema);
        if (line?.Option is null || caret <= line.OptionEnd) return null;
        foreach (var segment in line.Segments)
        {
            int start = SkipSpace(text, segment.Start, segment.End);
            int end = TrimSpace(text, start, segment.End);
            HelpValueSchema? parameter;
            if (segment.Equal >= 0)
            {
                string key = text[start..TrimSpace(text, start, segment.Equal)];
                parameter = line.Option.Parameters.FirstOrDefault(value => value.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                start = SkipSpace(text, segment.Equal + 1, end);
            }
            else parameter = ReferenceEquals(segment, line.Segments[0]) && line.Option.Type is not null ? line.Option : null;
            if (parameter is not null && start < end && caret >= start && caret <= end) return new(start, end - start, parameter);
        }
        return null;
    }

    private static CompletionContext ValueContext(string text, int caret, int start, int end,
        Func<int, int, string, CompletionContext> create)
    {
        // 候補を選んでも引用符は保つ。未閉鎖の引用符も入力途中として扱う。
        if (start < end && text[start] == '"')
        {
            ++start;
            if (end > start && text[end - 1] == '"') --end;
        }
        return create(start, end, text[start..Math.Clamp(caret, start, end)]);
    }

    private static bool HasType(HelpValueSchema value, string type) => !value.IsStructured && value.Type?.Split('|').Contains(type, StringComparer.Ordinal) == true;
    private static bool IsNumeric(HelpValueSchema value, string text) => (HasType(value, "int") || HasType(value, "float"))
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number);
    private static CompletionItem Item(string text, string? category, string description, bool isDefault = false) =>
        new(text, category, description.Split('\n')[0], isDefault);
    private static bool IsDefault(HelpValueSchema value, string candidate) => value.DefaultValue is { } current &&
        (candidate == current || (HasType(value, "bool") && ((candidate == "false" && current is "off" or "0") || (candidate == "true" && current is "on" or "1"))));
}

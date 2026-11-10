using System.Globalization;
using System.Text.RegularExpressions;

namespace HWEncVppPreview.Core;

/// <summary>数値範囲の両端。省略された端はnull。表記の精度を保つため文字列で保持する。</summary>
public sealed record HelpValueRange(string? Minimum, string? Maximum);

/// <summary>同じオプション内のパラメータに対する条件の種類。</summary>
public enum HelpConditionKind { Present, Equals, NotEquals }

/// <summary>パラメータの表示条件。候補の末尾の*は前方一致を表す。</summary>
public sealed record HelpCondition(string Parameter, HelpConditionKind Kind, IReadOnlyList<string> Values);

/// <summary>値の補完情報。書式外では名前以外を使わない。</summary>
public record HelpValueSchema
{
    public required string Name { get; init; }
    public bool IsNameOnly { get; init; } = true;
    public string Description { get; init; } = "";
    public string? Type { get; init; }
    public bool IsStructured { get; init; }
    public bool IsFromTo { get; init; }
    public HelpCondition? Condition { get; init; }
    /// <summary>依存する既定値ではnull。自動入力せず説明を参照する。</summary>
    public string? DefaultValue { get; init; }
    public bool IsDerivedDefault { get; init; }
    public HelpValueRange? Range { get; init; }
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
}

/// <summary>VPPオプション。分類はhelpの識別子を保持する。</summary>
public sealed record HelpOptionSchema : HelpValueSchema
{
    public string? Category { get; init; }
    public IReadOnlyList<HelpValueSchema> Parameters { get; init; } = Array.Empty<HelpValueSchema>();
    /// <summary>よく使うパラメータの名前。helpに書かれた表示順を保持する。</summary>
    public IReadOnlyList<string> MainParameters { get; init; } = Array.Empty<string>();
}

/// <summary>
/// HelpIndexと同じテキストからVPPの補完情報を読み取る。
/// 分類・宣言・注釈が書式外の部分は、推測せず名前だけを残す。
/// </summary>
public sealed class HelpSchema
{
    private static readonly Regex Option = new(@"^   (?<name>--vpp-[a-z0-9-]+)(?=$| )(?<tail>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Category = new(@"^  \[(?<id>[a-z]+)\] \S.*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "deinterlace", "denoise", "edge", "deband", "color", "resize", "framerate", "overlay", "ai", "other"
    };
    private static readonly Regex Parameter = new(@"^      (?<name>[a-z0-9_][a-z0-9_-]*)=(?<type>\S+)(?: +(?<text>.*))?$", RegexOptions.CultureInvariant);
    // 旧書式のまとめ書きや複数宣言も、浅い行の明示された名前だけを回収する。
    private static readonly Regex ParameterName = new(@"(?:^ {1,7}(?<name>[a-z0-9_][a-z0-9_-]*)\s*=|, *(?<name>[a-z0-9_][a-z0-9_-]*)\s*=\s*<)", RegexOptions.CultureInvariant);
    private static readonly Regex Annotation = new(@"^(?<text>.*?)(?: +)?\(default: (?<value>[^,()]+)(?:, range: (?<range>[^()]+))?\)$", RegexOptions.CultureInvariant);
    private static readonly Regex SimpleType = new(@"^<(?<type>[^<>]+)>$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> BasicTypes = new(StringComparer.Ordinal) { "int", "float", "bool", "string", "path" };
    private readonly Dictionary<string, HelpOptionSchema> _options = new(StringComparer.Ordinal);

    public HelpSchema(string helpText)
    {
        ArgumentNullException.ThrowIfNull(helpText);
        var index = new HelpIndex(helpText);
        var categories = ReadCategories(helpText);
        foreach (string name in index.OptionNames.Where(name => name.StartsWith("--vpp-", StringComparison.Ordinal)))
        {
            string[] lines = index.Lookup(name).Split('\n');
            var parameters = ReadParameterNames(lines).Select(key => new HelpValueSchema { Name = key }).ToList();
            var fallback = new HelpOptionSchema { Name = name, Parameters = parameters.AsReadOnly() };
            if (!categories.TryGetValue(name, out string? category) || category is null)
            {
                _options.Add(name, fallback);
                continue;
            }
            _options.Add(name, ReadOption(fallback, category, lines));
        }
        Options = Array.AsReadOnly(_options.Values.ToArray());
        OptionNames = Array.AsReadOnly(_options.Keys.ToArray());
    }

    public IReadOnlyList<HelpOptionSchema> Options { get; }
    public IReadOnlyList<string> OptionNames { get; }
    public HelpOptionSchema? Lookup(string optionName) => _options.GetValueOrDefault(optionName);

    private static Dictionary<string, string?> ReadCategories(string text)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        bool inVpp = false;
        string? category = null;
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line == "VPP Options:") { inVpp = true; category = null; continue; }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.EndsWith("Options:", StringComparison.Ordinal))
            { inVpp = false; category = null; }
            if (!inVpp)
            {
                var outside = Option.Match(line);
                if (outside.Success) result[outside.Groups["name"].Value] = null;
                continue;
            }
            var heading = Category.Match(line);
            if (line.StartsWith("  [", StringComparison.Ordinal))
                category = heading.Success && Categories.Contains(heading.Groups["id"].Value) ? heading.Groups["id"].Value : null;
            var option = Option.Match(line);
            if (option.Success)
            {
                string name = option.Groups["name"].Value;
                if (!result.TryAdd(name, category)) result[name] = null;
            }
        }
        return result;
    }

    private static IEnumerable<string> ReadParameterNames(IEnumerable<string> lines)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines.Skip(1))
        {
            if (IsMainMetadata(line) || IsWhenMetadata(line)) continue;
            if (line.StartsWith("  [", StringComparison.Ordinal) || (line.Length > 0 && !char.IsWhiteSpace(line[0]))) yield break;
            int indent = line.Length - line.TrimStart(' ').Length;
            if (indent is < 1 or > 7 || !Regex.IsMatch(line, @"^ {1,7}[a-z0-9_][a-z0-9_-]*\s*=", RegexOptions.CultureInvariant)) continue;
            foreach (Match match in ParameterName.Matches(line))
                if (names.Add(match.Groups["name"].Value)) yield return match.Groups["name"].Value;
        }
    }

    private sealed class Field(string name, string declaration, string description)
    {
        public string Name { get; } = name;
        public string Declaration { get; } = declaration;
        public string FirstLine { get; } = description;
        public List<string> Continuation { get; } = [];
        public List<string>? Values { get; set; }
        public bool Invalid { get; set; }
        public string? ConditionLine { get; set; }
        public int ConditionIndent { get; set; }
        public int? ValuesIndent { get; set; }
        public bool InvalidCondition { get; set; }
    }

    private static bool IsWhenMetadata(string line) => line.TrimStart().StartsWith("when:", StringComparison.Ordinal);

    private static bool IsMainMetadata(string line) => line.TrimStart().StartsWith("main:", StringComparison.Ordinal);

    private static IReadOnlyList<string> ReadMainParameters(string[] lines, IReadOnlyList<HelpValueSchema> parameters)
    {
        int mainLine = -1, paramsLine = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (IsWhenMetadata(line)) continue;
            if (!IsMainMetadata(line)
                && (line.StartsWith("  [", StringComparison.Ordinal) || (line.Length > 0 && !char.IsWhiteSpace(line[0])))) break;
            if (line == "    params" && paramsLine < 0) paramsLine = i;
            if (!IsMainMetadata(line)) continue;
            // 複数行や誤った配置を推測で補わず、印だけを無効にする。
            if (mainLine >= 0) return Array.Empty<string>();
            mainLine = i;
        }
        if (mainLine < 0 || paramsLine <= mainLine) return Array.Empty<string>();
        if (lines.Skip(mainLine + 1).Take(paramsLine - mainLine - 1).Any(line => !string.IsNullOrWhiteSpace(line) && !IsWhenMetadata(line)))
            return Array.Empty<string>();
        var match = Regex.Match(lines[mainLine], @"^    main: (?<names>[a-z0-9_][a-z0-9_-]*(?:, *[a-z0-9_][a-z0-9_-]*)*)$", RegexOptions.CultureInvariant);
        if (!match.Success) return Array.Empty<string>();
        string[] names = match.Groups["names"].Value.Split(',').Select(name => name.Trim()).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || names.Any(name => !parameters.Any(parameter => parameter.Name == name)))
            return Array.Empty<string>();
        return Array.AsReadOnly(names);
    }

    private static HelpOptionSchema ReadOption(HelpOptionSchema fallback, string category, string[] lines)
    {
        var match = Option.Match(lines[0]);
        if (!match.Success) return fallback;
        string tail = match.Groups["tail"].Value.Trim();
        string declaration = "";
        string description = tail;
        if (tail.StartsWith('<') || tail.StartsWith('['))
        {
            int end = tail.IndexOf(' ');
            declaration = end < 0 ? tail : tail[..end];
            description = end < 0 ? "" : tail[(end + 1)..].TrimStart();
        }
        bool longForm = declaration.StartsWith("[<param", StringComparison.Ordinal);
        // 任意の単一値と、先頭の値に続くパラメータ列を区別する。
        if (!longForm && declaration.StartsWith("[<", StringComparison.Ordinal) && declaration.EndsWith(">]", StringComparison.Ordinal))
            declaration = declaration[1..^1];
        int parameterSuffix = declaration.IndexOf("[,<param", StringComparison.Ordinal);
        if (parameterSuffix >= 0) declaration = declaration[..parameterSuffix];
        var option = new Field(fallback.Name, longForm ? "" : declaration, description);
        var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
        var duplicate = new HashSet<string>(StringComparer.Ordinal);
        Field? current = option;
        bool inParameters = false;
        foreach (string line in lines.Skip(1))
        {
            // 不正な印も説明文や型の検証へ混ぜず、既存の補完情報を維持する。
            if (IsMainMetadata(line)) continue;
            // 誤った字下げや重複も条件だけを無効にし、型や説明は維持する。
            if (IsWhenMetadata(line))
            {
                if (inParameters && current is not null)
                {
                    if (current.ConditionLine is not null) current.InvalidCondition = true;
                    current.ConditionLine = line;
                    current.ConditionIndent = line.Length - line.TrimStart(' ').Length;
                }
                continue;
            }
            if (line.StartsWith("  [", StringComparison.Ordinal) || (line.Length > 0 && !char.IsWhiteSpace(line[0]))) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line == "    params") { inParameters = true; current = null; continue; }
            var parameter = Parameter.Match(line);
            int indent = line.Length - line.TrimStart(' ').Length;
            if (inParameters && indent < 8)
            {
                current = null;
                if (parameter.Success)
                {
                    string name = parameter.Groups["name"].Value;
                    current = new Field(name, parameter.Groups["type"].Value, parameter.Groups["text"].Value);
                    if (!fields.TryAdd(name, current)) duplicate.Add(name);
                }
                continue;
            }
            if (current is null) continue;
            string content = line.Trim();
            if (indent >= 8 && content.StartsWith("values:", StringComparison.Ordinal))
            {
                if (current.Values is not null) current.Invalid = true;
                if (current.ConditionLine is not null) current.InvalidCondition = true;
                current.ValuesIndent = indent;
                current.Values = content[7..].Split(',').Select(value => value.Trim()).ToList();
            }
            else if (indent >= 8 || (!inParameters && indent == 5))
            {
                if (current.ConditionLine is not null) current.InvalidCondition = true;
                current.Continuation.Add(content);
            }
            else current.Invalid = true;
        }
        var parameters = fallback.Parameters.Select(parameter =>
            fields.TryGetValue(parameter.Name, out var field) && !duplicate.Contains(parameter.Name)
                ? ReadValue(field) ?? parameter : parameter).ToArray();
        // 全パラメータの型と候補を読み終えてから参照先を解決し、宣言順への依存を避ける。
        parameters = parameters.Select(parameter => fields.TryGetValue(parameter.Name, out var field)
            && !parameter.IsNameOnly ? parameter with { Condition = ReadCondition(field, parameters) } : parameter).ToArray();
        IReadOnlyList<string> mainParameters = ReadMainParameters(lines, parameters);
        HelpValueSchema? value = ReadValue(option);
        if (value is null) return fallback with { Parameters = Array.AsReadOnly(parameters), MainParameters = mainParameters };
        return new HelpOptionSchema
        {
            Name = fallback.Name, IsNameOnly = false, Category = category, Description = value.Description,
            Type = value.Type, IsStructured = value.IsStructured, IsFromTo = value.IsFromTo, DefaultValue = value.DefaultValue,
            IsDerivedDefault = value.IsDerivedDefault, Range = value.Range, Values = value.Values,
            Parameters = Array.AsReadOnly(parameters), MainParameters = mainParameters
        };
    }

    private static HelpCondition? ReadCondition(Field field, IReadOnlyList<HelpValueSchema> parameters)
    {
        if (field.ConditionLine is null || field.InvalidCondition || field.ConditionIndent < 8
            || (field.ValuesIndent is int valuesIndent && valuesIndent != field.ConditionIndent)) return null;
        var match = Regex.Match(field.ConditionLine,
            @"^ {8,}when: (?<name>[a-z0-9_][a-z0-9_-]*)(?:(?<operator>!=|=)(?<values>[^\s,=*!=]+\*?(?:, *[^\s,=*!=]+\*?)*))?$",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        string name = match.Groups["name"].Value;
        HelpValueSchema? referenced = parameters.FirstOrDefault(parameter => parameter.Name == name);
        if (referenced is null) return null;
        if (!match.Groups["operator"].Success)
            return new(name, HelpConditionKind.Present, Array.Empty<string>());
        string[] values = match.Groups["values"].Value.Split(',').Select(value => value.Trim()).ToArray();
        if (referenced.IsNameOnly || values.Any(value => !referenced.Values.Any(candidate =>
            value.EndsWith('*') ? candidate.StartsWith(value[..^1], StringComparison.Ordinal) : candidate == value))) return null;
        return new(name, match.Groups["operator"].Value == "=" ? HelpConditionKind.Equals : HelpConditionKind.NotEquals,
            Array.AsReadOnly(values));
    }

    private static HelpValueSchema? ReadValue(Field field)
    {
        if (field.Invalid) return null;
        string text = field.FirstLine.Trim();
        string? defaultValue = null;
        bool derived = false;
        HelpValueRange? range = null;
        var annotation = Annotation.Match(text);
        if (annotation.Success)
        {
            defaultValue = annotation.Groups["value"].Value.Trim();
            derived = defaultValue == "derived";
            if (derived) defaultValue = null;
            text = annotation.Groups["text"].Value.TrimEnd();
            if (annotation.Groups["range"].Success)
            {
                string[] bounds = annotation.Groups["range"].Value.Split("..", StringSplitOptions.None);
                if (bounds.Length != 2 || bounds.All(string.IsNullOrWhiteSpace)) return null;
                string? low = NullIfEmpty(bounds[0]), high = NullIfEmpty(bounds[1]);
                if ((low is not null && !Number(low)) || (high is not null && !Number(high))) return null;
                if (low is not null && high is not null && double.Parse(low, CultureInfo.InvariantCulture) > double.Parse(high, CultureInfo.InvariantCulture)) return null;
                range = new(low, high);
            }
        }
        else if (Regex.IsMatch(text, @"[\[(]\s*default\s*:|range:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)) return null;
        var values = field.Values ?? [];
        if (values.Any(string.IsNullOrWhiteSpace)) return null;
        // 別バックエンドで同じ候補名を使うhelpもあるため、順序を保って重複を除く。
        values = values.Distinct(StringComparer.Ordinal).ToList();
        string? type = null;
        bool structured = false;
        if (field.Declaration.Length > 0)
        {
            if (!Regex.IsMatch(field.Declaration, @"^<[^<>\s]+>(?:[^<>\s]*<[^<>\s]+>)*[^<>\s]*$", RegexOptions.CultureInvariant)) return null;
            var simple = SimpleType.Match(field.Declaration);
            string raw = simple.Success ? simple.Groups["type"].Value : field.Declaration;
            string[] parts = raw.Split('|');
            structured = !simple.Success || !parts.Any(BasicTypes.Contains);
            if (!structured && parts.Any(part => part.Length == 0 || !Regex.IsMatch(part, @"^[a-z0-9_-]+$", RegexOptions.CultureInvariant))) return null;
            type = raw;
            if (!structured)
            {
                if ((parts.Any(part => part is "int" or "float" or "bool") || (parts.Contains("string") && values.Count > 0)) && !annotation.Success) return null;
                if (parts.Where(part => !BasicTypes.Contains(part)).Any(part => !values.Contains(part, StringComparer.Ordinal))) return null;
                if (defaultValue is not null)
                {
                    bool valid = values.Contains(defaultValue, StringComparer.Ordinal) || parts.Any(part => part switch
                    {
                        "int" => long.TryParse(defaultValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
                        "float" => Number(defaultValue), "bool" => defaultValue is "true" or "false" or "on" or "off" or "0" or "1",
                        "string" => values.Count == 0, "path" => true, _ => false
                    });
                    if (!valid) return null;
                }
            }
        }
        return new HelpValueSchema
        {
            Name = field.Name, IsNameOnly = false, Description = string.Join("\n", new[] { text }.Concat(field.Continuation).Where(line => line.Length > 0)),
            Type = type, IsStructured = structured, IsFromTo = field.Declaration == "<from>:<to>", DefaultValue = defaultValue, IsDerivedDefault = derived,
            Range = range, Values = Array.AsReadOnly(values.ToArray())
        };
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static bool Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value);
}

using System.Text.RegularExpressions;

namespace HWEncParamViewer.Core;

/// <summary>エンコーダのヘルプをオプションごとに切り出す。未知のオプションには空文字列を返す。</summary>
public sealed class HelpIndex
{
    // 実ヘルプではresizeだけ空白なしの見出しもあるため、通常の2〜4空白に加えて行頭も受け付ける。
    private static readonly Regex Heading = new(@"^(?: {2,4})?(?<name>--[a-z0-9][a-z0-9-]*)(?=$|[\s,<\[/])", RegexOptions.CultureInvariant);
    private static readonly Regex Alias = new(@"\G(?:\s*[,/]\s*| +)(?<name>--[a-z0-9][a-z0-9-]*)(?=$|[\s,<\[/])", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, string> _sections = new(StringComparer.Ordinal);

    public HelpIndex(string helpText)
    {
        ArgumentNullException.ThrowIfNull(helpText);
        var lines = helpText.Replace("\r\n", "\n").Split('\n');
        var headings = new List<(int Line, string[] Names)>();
        for (int line = 0; line < lines.Length; ++line)
        {
            var match = Heading.Match(lines[line]);
            if (!match.Success) continue;
            var names = new List<string> { match.Groups["name"].Value };
            int end = match.Length;
            while (true)
            {
                var alias = Alias.Match(lines[line], end);
                if (!alias.Success) break;
                names.Add(alias.Groups["name"].Value);
                end += alias.Length;
            }
            headings.Add((line, names.ToArray()));
        }
        for (int i = 0; i < headings.Count; ++i)
        {
            int end = i + 1 < headings.Count ? headings[i + 1].Line : lines.Length;
            string text = string.Join("\n", lines[headings[i].Line..end]).TrimEnd();
            foreach (string name in headings[i].Names) _sections.TryAdd(name, text);
        }
        OptionNames = Array.AsReadOnly(_sections.Keys.ToArray());
    }

    public IReadOnlyList<string> OptionNames { get; }
    public string Lookup(string optionName) => _sections.GetValueOrDefault(optionName, "");
}

using System.Text;

namespace HWEncVppPreview.Core;

/// <summary>フィルタ行の引用符と区切りを共通の規則で読む。</summary>
internal static class FilterLineSyntax
{
    internal sealed record Segment(int Start, int End, int Equal);
    internal sealed record Line(int Start, int End, int OptionStart, int OptionEnd, HelpOptionSchema? Option, IReadOnlyList<Segment> Segments, bool ArgumentQuoted = false);
    internal static Line? ReadLine(string text, int caret, HelpSchema schema)
    {
        if (caret < 0 || caret > text.Length) return null;
        int start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        int end = text.IndexOf('\n', caret);
        if (end < 0) end = text.Length;
        if (end > start && text[end - 1] == '\r') --end;
        if (caret > end) return null;
        int optionStart = SkipSpace(text, start, end);
        int optionEnd = optionStart;
        while (optionEnd < end && !char.IsWhiteSpace(text[optionEnd])) ++optionEnd;
        if (optionStart == end) return new(start, end, caret, caret, null, []);
        string name = text[optionStart..optionEnd];
        var option = schema.Lookup(name.ToLowerInvariant());
        var segments = new List<Segment>();
        int argumentStart = SkipSpace(text, optionEnd, end);
        int argumentEnd = TrimSpace(text, argumentStart, end);
        bool argumentQuoted = WholeQuoted(text, argumentStart, argumentEnd);
        int partsEnd = argumentQuoted ? argumentEnd - 1 : end;
        int partStart = argumentQuoted ? argumentStart + 1 : optionEnd, equal = -1;
        bool quoted = false;
        for (int i = partStart; i < partsEnd; ++i)
        {
            if (text[i] == '"')
            {
                if (quoted && i + 1 < partsEnd && text[i + 1] == '"') { ++i; continue; }
                quoted = !quoted;
            }
            if (quoted) continue;
            if (text[i] == '=' && equal < 0) equal = i;
            if (text[i] != ',') continue;
            segments.Add(new(partStart, i, equal));
            partStart = i + 1;
            equal = -1;
        }
        segments.Add(new(partStart, partsEnd, equal));
        return new(start, end, optionStart, optionEnd, option, segments, argumentQuoted);
    }

    // 引数全体の引用符と、値だけを囲む引用符を区別する。
    private static bool WholeQuoted(string text, int start, int end)
    {
        if (end - start < 2 || text[start] != '"' || text[end - 1] != '"') return false;
        for (int i = start + 1; i < end; ++i)
        {
            if (text[i] != '"') continue;
            if (i + 1 < end && text[i + 1] == '"') { ++i; continue; }
            return i == end - 1;
        }
        return false;
    }

    internal static int SkipSpace(string text, int start, int end) { while (start < end && char.IsWhiteSpace(text[start])) ++start; return start; }
    internal static int TrimSpace(string text, int start, int end) { while (end > start && char.IsWhiteSpace(text[end - 1])) --end; return end; }

    // 外側の引数の引用符を外す。バックスラッシュはWindowsのパス区切りとして保つ。
    internal static IReadOnlyList<string> Tokenize(string line, bool allowComment = true)
    {
        var result = new List<string>();
        if (allowComment && line.TrimStart().StartsWith('#')) return result;
        var value = new StringBuilder();
        bool quoted = false, token = false;
        for (int i = 0; i < line.Length; ++i)
        {
            char c = line[i];
            if (c == '"')
            {
                token = true;
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); ++i; }
                else quoted = !quoted;
            }
            else if (!quoted && c is ' ' or '\t')
            {
                if (token) { result.Add(value.ToString()); value.Clear(); token = false; }
            }
            else { value.Append(c); token = true; }
        }
        if (token) result.Add(value.ToString());
        return result;
    }
}

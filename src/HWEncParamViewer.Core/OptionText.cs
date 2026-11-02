namespace HWEncParamViewer.Core;

/// <summary>オプションテキストの1行と、その有効フラグ (チェックボックス)。</summary>
public sealed record OptionLine(string Text, bool Enabled = true);

/// <summary>vpp オプションのテキスト (設計書 4.1) を、エンコーダに渡す引数の配列にする。値の検証はしない。</summary>
public static class OptionTextParser
{
    /// <summary>有効な行を順に分割して連結する。無効な行、空行、先頭が # の行は無視する。</summary>
    public static string[] Parse(IEnumerable<OptionLine> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (!line.Enabled) continue;
            result.AddRange(TokenizeLine(line.Text));
        }
        return result.ToArray();
    }

    /// <summary>複数行の文字列を、すべて有効な行として扱う便利版。</summary>
    public static string[] ParseText(string text) =>
        Parse(text.Split('\n').Select(l => new OptionLine(l.TrimEnd('\r'))));

    /// <summary>
    /// 1行を引数に分割する。空白 (スペース、タブ) 区切り。ダブルクォートで囲むと空白を含めて1つの引数になり、
    /// クォート自体は取り除く。クォート内の "" はダブルクォート1文字。
    /// </summary>
    /// <remarks>
    /// バックスラッシュはエスケープに使わない (Windows のパス区切りのため)。
    /// 閉じていないクォートは、編集途中の行でも例外にせず、行末までを1つの引数として扱う。
    /// # は行頭 (前の空白を除く) のときだけコメント。行の途中の # は引数の一部。
    /// </remarks>
    public static IReadOnlyList<string> TokenizeLine(string line) => FilterLineSyntax.Tokenize(line);
}

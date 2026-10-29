using System.Globalization;
using System.Text.RegularExpressions;

namespace HWEncParamViewer.Core;

public sealed record NumberNudgeResult(string Text, int Caret);

/// <summary>カーソル位置の数値を最下位桁の単位で増減する。指数表記と識別子内の数字は対象外。</summary>
public static class NumberNudge
{
    private static readonly Regex Number = new(@"-?\d+(?:\.\d+)?", RegexOptions.CultureInvariant);

    public static NumberNudgeResult? TryNudge(string text, int caret, int direction, bool coarse = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (caret < 0 || caret > text.Length) return null;
        foreach (Match match in Number.Matches(text))
        {
            if (caret < match.Index || caret > match.Index + match.Length) continue;
            int end = match.Index + match.Length;
            if (match.Index > 0 && IsIdentifierPart(text[match.Index - 1])) continue;
            if (end < text.Length && IsIdentifierPart(text[end])) continue;
            string token = match.Value;
            int dot = token.IndexOf('.');
            int scale = dot >= 0 ? token.Length - dot - 1 : 0;
            if (scale > 28 || !decimal.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal value)) return null;
            string unsignedToken = token.StartsWith('-') ? token[1..] : token;
            int unsignedDot = unsignedToken.IndexOf('.');
            string integer = (unsignedDot >= 0 ? unsignedToken[..unsignedDot] : unsignedToken).TrimStart('0');
            if (integer.Length == 0) integer = "0";
            string canonical = (value < 0 ? "-" : "") + integer + (unsignedDot >= 0 ? unsignedToken[unsignedDot..] : "");
            // decimalが入力を丸めた場合は、別の値を書き戻す代わりに増減を行わない。
            if (value.ToString("F" + scale, CultureInfo.InvariantCulture) != canonical) return null;
            try
            {
                decimal step = coarse ? 10m : 1m;
                for (int i = 0; i < scale; ++i) step /= 10m;
                decimal changed = checked(value + direction * step);
                if (changed - value != direction * step) return null;
                string replacement = changed.ToString("F" + scale, CultureInfo.InvariantCulture);
                // 小数の桁と整数部の先頭ゼロを維持し、桁が増える場合は必要な分だけ広げる。
                int integerDigits = (dot >= 0 ? dot : token.Length) - (token.StartsWith('-') ? 1 : 0);
                bool negative = replacement.StartsWith('-');
                string unsigned = negative ? replacement[1..] : replacement;
                int newDot = unsigned.IndexOf('.');
                int newIntegerDigits = newDot >= 0 ? newDot : unsigned.Length;
                if (newIntegerDigits < integerDigits) unsigned = new string('0', integerDigits - newIntegerDigits) + unsigned;
                replacement = (negative ? "-" : "") + unsigned;
                string result = text[..match.Index] + replacement + text[end..];
                int position = match.Index + Math.Clamp(caret - match.Index + replacement.Length - match.Length, 0, replacement.Length);
                return new NumberNudgeResult(result, position);
            }
            catch (OverflowException) { return null; }
        }
        return null;
    }

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value is '_' or '.' or '+' or '-';
}

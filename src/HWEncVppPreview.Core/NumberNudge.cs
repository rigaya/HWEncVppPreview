using System.Globalization;
using System.Text.RegularExpressions;

namespace HWEncVppPreview.Core;

public sealed record NumberNudgeResult(string Text, int Caret);

/// <summary>カーソル位置の数値を最下位桁の単位で増減し、指定された範囲の端で止める。指数表記と識別子内の数字は対象外。</summary>
public static class NumberNudge
{
    private static readonly Regex Number = new(@"-?\d+(?:\.\d+)?", RegexOptions.CultureInvariant);
    private static readonly Regex Bound = new(@"^(?<sign>[+-]?)(?<whole>[0-9]*)(?:\.(?<fraction>[0-9]*))?(?:[eE](?<exponent>[+-]?[0-9]+))?$", RegexOptions.CultureInvariant);

    public static NumberNudgeResult? TryNudge(string text, int caret, int direction, bool coarse = false, HelpValueRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (caret < 0 || caret > text.Length) return null;
        decimal? minimum = null, maximum = null;
        if (range is not null)
        {
            if (range.Minimum is not null)
            {
                if (!TryBound(range.Minimum, out decimal low)) return null;
                minimum = low;
            }
            if (range.Maximum is not null)
            {
                if (!TryBound(range.Maximum, out decimal high)) return null;
                maximum = high;
            }
            if (minimum is not null && maximum is not null && minimum > maximum) return null;
        }
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
                decimal changed;
                // 端に達している場合は加算せず止める。decimalの端でもオーバーフローさせない。
                if (direction > 0 && maximum is decimal upper && value >= upper) changed = upper;
                else if (direction < 0 && minimum is decimal lower && value <= lower) changed = lower;
                else
                {
                    try
                    {
                        changed = checked(value + direction * step);
                        if (changed - value != direction * step) return null;
                    }
                    catch (OverflowException)
                    {
                        if (direction > 0 && maximum is decimal max) changed = max;
                        else if (direction < 0 && minimum is decimal min) changed = min;
                        else return null;
                    }
                }
                if (minimum is decimal low && changed < low) changed = low;
                if (maximum is decimal high && changed > high) changed = high;
                // 境界のほうが細かい桁を持つ場合、丸めて範囲外へ戻さないよう必要な桁だけ増やす。
                int changedScale = 0;
                decimal scaled = changed;
                while (scaled != decimal.Truncate(scaled)) { scaled *= 10m; ++changedScale; }
                scale = Math.Max(scale, changedScale);
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

    private static bool TryBound(string text, out decimal value)
    {
        value = 0;
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && NormalizeBound(text) is string normalized
            && normalized == NormalizeBound(value.ToString(CultureInfo.InvariantCulture));
    }

    // decimal.TryParseは表現できない端数を丸めることがあるため、元の係数と指数も照合する。
    private static string? NormalizeBound(string text)
    {
        var match = Bound.Match(text.Trim());
        if (!match.Success) return null;
        string digits = match.Groups["whole"].Value + match.Groups["fraction"].Value;
        if (digits.Length == 0) return null;
        int exponent = 0;
        if (match.Groups["exponent"].Success
            && !int.TryParse(match.Groups["exponent"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return null;
        digits = digits.TrimStart('0');
        if (digits.Length == 0) return "0";
        int zeros = digits.Length - digits.TrimEnd('0').Length;
        string sign = match.Groups["sign"].Value == "-" ? "-" : "";
        long power = (long)exponent - match.Groups["fraction"].Value.Length + zeros;
        return sign + digits[..(digits.Length - zeros)] + "e" + power.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value is '_' or '.' or '+' or '-';
}

using System.Text.RegularExpressions;

namespace HWEncVppPreview.Core;

internal static class EncoderErrorDetails
{
    private const int MaxDetailLength = 240;
    private static readonly Regex AnsiSequence = new(@"\x1B\[[0-?]*[ -/]*[@-~]");
    private static readonly Regex ErrorWords = new(@"\b(?:error|fail(?:ed|ure)?|unavailable|not found)\b|失敗|エラー",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Append(string message, string standardError)
    {
        string? lastLine = null;
        string? lastError = null;
        using var reader = new StringReader(standardError);
        while (reader.ReadLine() is { } raw)
        {
            string line = AnsiSequence.Replace(raw, "").Trim();
            // ストリーム境界を示す制御行はエンコーダの診断から除く。
            if (line.Length == 0 || line.StartsWith("RGYPREVIEW ", StringComparison.Ordinal)) continue;
            lastLine = line;
            // 原因を含まない終了通知より、直前の具体的なエラーを優先する。
            if (ErrorWords.IsMatch(line) && !line.EndsWith(" finished with error!", StringComparison.OrdinalIgnoreCase))
                lastError = line;
        }
        string? detail = lastError ?? lastLine;
        if (detail == null) return message;
        if (detail.Length > MaxDetailLength)
        {
            int length = char.IsHighSurrogate(detail[MaxDetailLength - 1]) ? MaxDetailLength - 1 : MaxDetailLength;
            detail = detail[..length] + "…";
        }
        return message + " (" + detail + ")";
    }
}

namespace HWEncVppPreview.Core;

/// <summary>例外の型名と元のメッセージを外側から原因の順に保持する。</summary>
public static class ExceptionDiagnostics
{
    public static string[] Lines(Exception? exception)
    {
        var lines = new List<string>();
        for (Exception? current = exception; current != null; current = current.InnerException)
            lines.Add($"{current.GetType().Name}: {current.Message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ')}");
        return lines.ToArray();
    }

    public static string Format(Exception? exception)
    {
        string[] lines = Lines(exception);
        return lines.Length == 0 ? "" : CoreText.ExceptionDetails + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}

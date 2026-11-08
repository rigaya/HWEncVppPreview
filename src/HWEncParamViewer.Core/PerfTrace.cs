using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HWEncParamViewer.Core;

/// <summary>環境変数で明示的に有効化した計測だけを、プロセス共通の時系列へ記録する。</summary>
/// <remarks>呼び出し側もEnabledで分岐し、通常実行では計測データを生成しない。終了時にFlushを呼ぶ。</remarks>
public static class PerfTrace
{
    private static readonly object Sync = new();
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly StreamWriter? Writer = OpenWriter();
    // 末尾が開いた区間のSafeEndなど、計測値にはInfinityやNaNが入ることがある。
    // 既定のJSONはこれらを書けずに例外になり、計測を有効にした時だけ要求が失敗していたため、文字列として書く。
    private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static bool Enabled => Writer != null;

    public static void Write(string eventName, object? data = null)
    {
        if (Writer == null) return;
        double elapsedMs = Stopwatch.GetElapsedTime(Origin).TotalMilliseconds;
        int threadId = Environment.CurrentManagedThreadId;
        lock (Sync)
        {
            Writer.WriteLine(JsonSerializer.Serialize(new { elapsedMs, eventName, threadId, data }, JsonOptions));
        }
    }

    /// <summary>測定区間の終了時に、バッファをファイルへ確実に反映する。</summary>
    public static void Flush()
    {
        lock (Sync) Writer?.Flush();
    }

    private static StreamWriter? OpenWriter()
    {
        string? path = Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory != null) Directory.CreateDirectory(directory);
        // フレーム受信ごとのディスク待ちを避け、終了時にまとめて書き出す。
        var writer = new StreamWriter(new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false), 1 << 16);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        return writer;
    }
}

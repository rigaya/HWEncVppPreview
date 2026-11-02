using System.Globalization;

namespace HWEncParamViewer.Core;

/// <summary>保存する言語の指定と、解決済みの表示言語を保持する。Coreは画面の実装へ依存しない。</summary>
public static class UiLanguage
{
    private static readonly object Sync = new();
    private static string _preference = "auto";
    private static string _current = Resolve("auto");

    public static string Preference { get { lock (Sync) return _preference; } }
    public static string Current { get { lock (Sync) return _current; } }
    public static event Action? Changed;

    /// <summary>未知の指定は自動として扱い、設定ファイルの破損で起動を妨げない。</summary>
    public static string Normalize(string? language) => language is "ja" or "en" ? language : "auto";

    /// <summary>自動のときだけOSのUI言語を使う。数値や時刻の書式用カルチャは変えない。</summary>
    public static string Resolve(string? language, CultureInfo? culture = null) => Normalize(language) switch
    {
        "ja" => "ja",
        "en" => "en",
        _ => (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "ja" ? "ja" : "en"
    };

    public static void Set(string? language)
    {
        string preference = Normalize(language);
        string current = Resolve(preference);
        lock (Sync)
        {
            if (_preference == preference && _current == current) return;
            _preference = preference;
            _current = current;
        }
        // ハンドラから現在値を参照できるよう、ロックの外で通知する。
        Changed?.Invoke();
    }
}

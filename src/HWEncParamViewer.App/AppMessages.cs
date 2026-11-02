using HWEncParamViewer.Core;

namespace HWEncParamViewer.App;

/// <summary>画面の処理で作る文言の日英対応。helpとエンコーダの診断は変えない。</summary>
public static class AppMessages
{
    private static string T(string japanese, string english) => UiLanguage.Current == "ja" ? japanese : english;
    public static string OneShot => T("都度起動", "One-shot");
    public static string Server => T("常駐", "Server");
    public static string SaveSettingsFailed => T("設定を保存できませんでした", "Could not save settings");
    public static string NoDisplayableFrames => T("表示できるフレームがありません", "No displayable frames");
    public static string DoneTargetNotReached => T("完了（目標時刻に届かなかったため最終フレームを表示）", "Done (last frame; target not reached)");
    public static string DoneCache => T("完了（キャッシュ）", "Done (cached)");
    public static string Done => T("完了", "Done");
    public static string Error => T("エラー", "Error");
    public static string Start => T("先頭", "Start");
    public static string End => T("終端", "End");
    public static string InvalidTime => T("時刻は秒数、mm:ss、h:mm:ss.fffで指定してください", "Enter seconds, mm:ss, or h:mm:ss.fff");
    public static string OperationFailed => T("操作に失敗しました", "Operation failed");
    public static string HelpUnavailable => T("ヘルプを取得できませんでした: ", "Could not retrieve help: ");
    public static string Processing => T("処理中…", "Processing…");
    public static string SelectEncoderAndInput => T("エンコーダと入力ファイルを選択してください", "Select an encoder and input file");
    public static string ProbingInput => T("入力情報を取得中…", "Reading input info…");
    public static string ImageDisplayFailed => T("画像を表示できませんでした", "Could not display image");
    public static string SelectEncoder => T("エンコーダを選択", "Select encoder");
    public static string SelectInput => T("入力ファイルを選択", "Select input file");
    public static string LoadFilters => T("フィルタのテキストを読み込み", "Load filter text");
    public static string SaveFilters => T("フィルタのテキストを保存", "Save filter text");
    public static string Automatic => T("自動", "Auto");
    public static string CommaUnavailable => T("値にカンマは使えません。エンコーダがパラメータの区切りとして扱います。", "Commas cannot be used in values; the encoder treats them as separators.");
    public static string SchemaUnavailable => T("helpのスキーマを取得していません。テキストで編集してください。", "Help schema is unavailable. Edit the text instead.");
    public static string Elapsed(double seconds) => $"{seconds:0.00}" + T("秒", " s");
    public static string EncoderInfoFailed(int exitCode) => T("エンコーダの情報取得に失敗しました: ", "Could not read encoder info: ") + exitCode;
    public static string BackendMode(string mode) => mode switch { "oneshot" => OneShot, "server" => Server, _ => mode };
}

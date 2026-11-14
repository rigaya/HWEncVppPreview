using System.Globalization;

namespace HWEncVppPreview.Core;

/// <summary>利用者へ表示するCoreの文言。日本語と英語を必ず対で保持する。</summary>
public static class CoreText
{
    private static string T(string japanese, string english) => UiLanguage.Current == "ja" ? japanese : english;
    public static string ExceptionDetails => T("例外の詳細:", "Exception details:");
    public static string OneShotReadFailed => T("都度起動の出力を読み取れませんでした", "Could not read one-shot output.");
    public static string OneShotExitTimedOut => T("都度起動プロセスの終了待機が期限切れになりました", "Timed out waiting for the one-shot process to exit.");
    public static string StandardErrorTimedOut => T("stderrの完了待機が期限切れになりました", "Timed out waiting for stderr to finish.");
    public static string OneShotCleanupTimedOut => T("都度起動プロセスの回収が期限内に完了しませんでした", "Timed out cleaning up the one-shot process.");
    public static string ImageSizePositive => T("幅と高さは正の値が必要です", "Width and height must be positive.");
    public static string BitDepthUnsupported => T("ビット深度は8〜16が必要です", "Bit depth must be between 8 and 16.");
    public static string ChromaUnsupported => T("未対応のクロマ形式です", "Unsupported chroma format.");
    public static string ImageTooLarge => T("BGRA画像が大きすぎます", "The BGRA image is too large.");
    public static string OutputTimedOut => T("出力が進まないためタイムアウトしました", "Timed out because output stopped advancing.");
    public static string FrameUnavailable => T("目標時刻のフレームを取得できませんでした", "Could not obtain a frame at the target time.");
    public static string TargetCacheFull => T("表示目標までのフレームをキャッシュ上限内に保持できませんでした", "The frames up to the target exceed the cache limit.");
    public static string ReadAheadCacheFull => T("現在の画像と先読みをキャッシュ上限内に保持できませんでした", "The current frame and read-ahead exceed the cache limit.");
    public static string FpsPositive => T("Fps は正の値が必要です", "Fps must be positive.");
    public static string TimeMarginNonnegative => T("時刻とマージンは 0 以上が必要です", "Time and margins must be nonnegative.");
    public static string SeekNonnegative => T("seekは有限の0以上が必要です", "Seek must be finite and nonnegative.");
    public static string InputProbeTimedOut => T("入力情報の取得がタイムアウトしました", "Timed out reading input information.");
    public static string InputHeaderUnavailable => T("入力情報の取得で有効なfps付きヘッダとフレームを受信できませんでした", "No frame with a valid frame-rate header was received.");
    public static string InputDurationTimedOut => T("入力の長さの取得がタイムアウトしました", "Timed out reading the input duration.");
    public static string Y4mEmpty => T("y4m ストリームが空です", "The y4m stream is empty.");
    public static string Y4mLineTooLong => T("y4m の行が長すぎます (4096 バイト超)", "A y4m line exceeds 4096 bytes.");
    public static string Y4mMissingNewline => T("y4m の行が改行なしで終わっています", "A y4m line ends without a newline.");
    public static string EncoderStartFailed => T("エンコーダを起動できませんでした", "Could not start the encoder.");
    public static string DuplicateY4mHeader => T("1つの要求の中で y4m ヘッダが2回来ました", "Received two y4m headers in one request.");
    public static string FrameBeforeY4mHeader => T("y4m ヘッダの前に FRAME が来ました", "Received FRAME before the y4m header.");
    public static string BeginBeforeEnd => T("END なしで次の BEGIN が来ました", "Received another BEGIN before END.");
    public static string EndBeforeBegin => T("BEGIN なしで END が来ました", "Received END without BEGIN.");
    public static string ServerRequestFailed => T("常駐サーバの要求を完了できませんでした", "Could not complete the server request.");
    public static string ServerEventMismatch => T("要求と一致しない常駐プロトコルのイベントを受信しました", "Received a server event for a different request.");
    public static string EncoderExitedBeforeEnd => T("ENDの前にエンコーダが終了しました", "The encoder exited before END.");
    public static string ServerReadyUnavailable => T("対応するREADYを受信できませんでした", "No compatible READY response was received.");
    public static string ServerHandshakeFailed => T("常駐サーバのハンドシェイクに失敗しました", "The server handshake failed.");
    public static string ServerStopTimedOut => T("常駐サーバの停止が期限内に完了しませんでした", "Timed out stopping the server.");
    public static string ServerCleanupTimedOut => T("常駐サーバの回収が期限内に完了しませんでした", "Timed out cleaning up the server.");
    public static string ServerCommandTimedOut => T("常駐サーバへのコマンド送信が期限内に完了しませんでした", "Timed out sending a server command.");
    public static string FormSchemaUnavailable => T("スキーマに対応していない行です。テキストで編集してください。", "This line has no supported schema. Edit it as text.");
    public static string FormMultipleArguments => T("引数が複数あります。テキストで編集してください。", "Multiple arguments are present. Edit this line as text.");
    public static string FormMixedArguments => T("短い値とパラメータの混在はテキストで編集してください。", "Edit mixed short values and parameters as text.");
    public static string FormNoArguments => T("引数を取らないオプションです。テキストで編集してください。", "This option takes no arguments. Edit it as text.");
    public static string FormParameterInvalid => T("パラメータの形式を読み取れません。テキストで編集してください。", "Could not read the parameters. Edit this line as text.");
    public static string FormCommaUnavailable => T("パラメータ値にカンマは使えません。テキストはそのまま保持します。", "Parameter values cannot contain commas. The text is preserved.");
    public static string FormDuplicateParameter => T("空の名前または重複したパラメータがあります。テキストで編集してください。", "Empty or duplicate parameter names are present. Edit this line as text.");
    public static string CategoryDeinterlace => T("インターレース解除", "Deinterlace");
    public static string CategoryDenoise => T("ノイズ除去", "Denoise");
    public static string CategoryEdge => T("エッジ強調", "Sharpen");
    public static string CategoryDeband => T("バンディング", "Deband");
    public static string CategoryColor => T("色調整", "Color");
    public static string CategoryResize => T("リサイズ・幾何変換", "Resize / geometry");
    public static string CategoryFramerate => T("フレームレート・補間", "Frame rate / interpolation");
    public static string CategoryOverlay => T("ロゴ・字幕・合成", "Logo / subtitles / overlay");
    public static string CategoryAi => T("AI", "AI");
    public static string CategoryOther => T("その他・設定", "Other / settings");
    public static string PlaneDataLengthInvalid(object? value0, object? value1) =>
        string.Format(CultureInfo.InvariantCulture, T("平面データ長が不正です: 期待{0}バイト、実際{1}バイト", "Invalid plane data length: expected {0} bytes, received {1}."), value0, value1);
    public static string EncoderExited(object? value0) =>
        string.Format(CultureInfo.InvariantCulture, T("エンコーダがエラー終了しました: {0}", "The encoder exited with an error: {0}"), value0);
    public static string InputProbeEncoderExited(object? value0) =>
        string.Format(CultureInfo.InvariantCulture, T("入力情報の取得でエンコーダがエラー終了しました: {0}", "The encoder exited while reading input information: {0}"), value0);
    public static string Y4mFrameTruncated(object? value0, object? value1) =>
        string.Format(CultureInfo.InvariantCulture, T("y4m のフレームが途中で切れています (期待 {0} バイト、実際 {1} バイト)", "The y4m frame is truncated (expected {0} bytes, received {1} bytes)."), value0, value1);
    public static string Y4mMagicInvalid(object? value0) =>
        string.Format(CultureInfo.InvariantCulture, T("y4m ヘッダが YUV4MPEG2 で始まっていません: {0}", "The y4m header does not start with YUV4MPEG2: {0}"), value0);
    public static string Y4mSizeInvalid(object? value0) =>
        string.Format(CultureInfo.InvariantCulture, T("y4m ヘッダに有効な W / H がありません: {0}", "The y4m header has no valid W / H: {0}"), value0);
    public static string ColorSpaceUnsupported(object? value0) =>
        string.Format(CultureInfo.InvariantCulture, T("未対応のカラースペース: C{0}", "Unsupported color space: C{0}"), value0);
    public static string Y4mTagInvalid(object? value0) =>
        string.Format(CultureInfo.InvariantCulture, T("y4m ヘッダのタグが不正です: {0}", "Invalid y4m header tag: {0}"), value0);
    public static string Y4mFrameExpected(object? value0) =>
        string.Format(CultureInfo.InvariantCulture, T("FRAME 行が期待されましたが、別の内容でした: {0}", "Expected a FRAME line, received: {0}"), value0);
    public static string ProtocolLineOutsideBegin(object? value0) =>
        string.Format(CultureInfo.InvariantCulture, T("BEGIN の外で想定外の行を受信しました: {0}", "Received an unexpected line outside BEGIN: {0}"), value0);
    public static string ProtocolLineUnexpected(object? value0) =>
        string.Format(CultureInfo.InvariantCulture, T("想定外の行を受信しました: {0}", "Received an unexpected line: {0}"), value0);
    public static string ProtocolControlUnknown(object? value0) =>
        string.Format(CultureInfo.InvariantCulture, T("不明な制御行: {0}", "Unknown control line: {0}"), value0);
    public static string ProtocolNumberInvalid(object? value0, object? value1) =>
        string.Format(CultureInfo.InvariantCulture, T("{0}= が無い、または数値ではありません: {1}", "Missing or invalid number for {0}=: {1}"), value0, value1);
}

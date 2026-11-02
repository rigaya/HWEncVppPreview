using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.Localization;

/// <summary>画面の固定文言。表示中の言語を保持し、切り替え時にはインスタンスごと差し替える。</summary>
public sealed class UiText
{
    private readonly bool _japanese = UiLanguage.Current == "ja";
    private string T(string japanese, string english) => _japanese ? japanese : english;

    public string WindowTitle => T("HWEnc パラメータビューワー", "HWEnc Parameter Viewer");
    public string Encoder => T("エンコーダ", "Encoder");
    public string BrowseEncoder => T("エンコーダを選択", "Choose encoder");
    public string Input => T("入力", "Input");
    public string InputFile => T("入力ファイル", "Input file");
    public string BrowseInput => T("入力ファイルを選択", "Choose input file");
    public string Before => T("前", "Before");
    public string After => T("後", "After");
    public string ChooseInputHint => T("エンコーダと入力ファイルを指定してください (ここへのドロップでも指定できます)", "Choose an encoder and input file, or drop them here");
    public string BeforeFilter => T("フィルタ前", "Before filters");
    public string LoadingBefore => T("フィルタ前を取得中…", "Loading before…");
    public string Processing => T("処理中…", "Processing…");
    public string GoToStart => T("先頭 (Home)", "Start (Home)");
    public string PreviousFrame => T("前のフレーム (←)", "Previous frame (←)");
    public string TimeHint => T("時刻 (h:mm:ss.fff / mm:ss / 秒)。Enter で移動", "Time (h:mm:ss.fff / mm:ss / seconds). Enter to seek");
    public string NextFrame => T("次のフレーム (→)", "Next frame (→)");
    public string GoToEnd => T("終端 (End)", "End (End)");
    public string Single => T("単一", "Single");
    public string SingleHint => T("フィルタ後だけ (1)。Space を押している間はフィルタ前", "After filters (1). Hold Space to show before");
    public string Split => T("分割", "Split");
    public string SplitHint => T("左がフィルタ前、右がフィルタ後 (2)。境界はドラッグで移動", "Before on left, after on right (2). Drag the divider");
    public string SideBySide => T("並べる", "Side by side");
    public string SideBySideHint => T("フィルタ前と後を並べて表示 (3)", "Show before and after side by side (3)");
    public string ZoomFitHint => T("クリックでウィンドウに合わせる (0)。画像の上で Ctrl+ホイールで拡大縮小、ホイールでフレーム移動", "Fit to window (0). Ctrl+wheel to zoom; wheel to step frames");
    public string ActualSizeHint => T("等倍 (Ctrl+0)", "Actual size (Ctrl+0)");
    public string Filters => T("フィルタ", "Filters");
    public string AddFilter => T("＋ フィルタを追加", "+ Add filter");
    public string AddFilterHint => T("用途別の一覧から選んで追加", "Add a filter by category");
    public string TextMode => T("テキスト", "Text");
    public string TextModeHint => T("1行1フィルタのテキストで一覧を編集する (貼り付けやまとめての編集用)", "Edit one filter per line; paste or edit several at once");
    public string Load => T("読み込み", "Load");
    public string LoadHint => T("テキストファイルから読み込み (Ctrl+O)", "Load a text file (Ctrl+O)");
    public string Save => T("保存", "Save");
    public string SaveHint => T("テキストファイルに保存 (Ctrl+S)", "Save a text file (Ctrl+S)");
    public string EnableDisable => T("有効 / 無効", "Enable / disable");
    public string EmptyLine => T("(空の行)", "(empty line)");
    public string Defaults => T("既定値", "Defaults");
    public string DeleteFilter => T("このフィルタを削除", "Remove this filter");
    public string DeleteLine => T("この行を削除", "Remove this line");
    public string AddLine => T("＋ 行を追加", "+ Add line");
    public string TextEditingHint => T("1行に1つ、--vpp-xxx 形式でフィルタを書きます。入力中は候補が出ます (Ctrl+Space でも表示)。チェックを外した行は無効になります。数値は上下にドラッグ、またはホイール / Ctrl+↑↓ で増減できます。", "Enter one --vpp-xxx filter per line. Suggestions appear as you type (or press Ctrl+Space). Uncheck a line to disable it. Drag numbers vertically, use the wheel, or press Ctrl+↑↓ to adjust them.");
    public string NoSchemaHint => T("エンコーダを指定すると、追加できるフィルタの一覧が出ます。ヘルプを読めないエンコーダでは、「テキスト」で --vpp-xxx 形式のまま編集できます。", "Choose an encoder to list available filters. If its help cannot be read, use Text to edit --vpp-xxx options.");
    public string SelectFilterHint => T("フィルタを選ぶと、ここでパラメータを調整できます。", "Select a filter to adjust its parameters here.");
    public string NoParameters => T("パラメータはありません。", "No parameters.");
    public string Number => T("数値", "Number");
    public string BrowseFile => T("ファイルを選択", "Choose file");
    public string ResetHint => T("既定に戻す (行から消す)", "Reset to default (remove from the line)");
    public string DefaultLabel => T("既定", "Default");
    public string FullHelp => T("ヘルプ全文", "Full help");
    public string EmptyFiltersHint => T("フィルタを選んで追加します。追加すると既定値ですぐにプレビューされ、パラメータを調整できます。", "Select a filter to add it. Preview starts with its defaults; then adjust its parameters.");
    public string InputArguments => T("入力の追加引数", "Input arguments");
    public string SarHint => T("オン: SAR を考慮した表示サイズ / オフ: 正方画素 (画素単位の確認用)", "On: use SAR for display size. Off: square pixels for pixel inspection");
    public string Log => T("ログ", "Log");
    public string CopyCommand => T("コマンドをコピー", "Copy command");
    public string CopyCommandHint => T("有効なフィルタ行を連結した vpp オプションをコピー", "Copy VPP options from enabled filter lines");
    public string ByCategory => T("用途別", "Category");
    public string SearchFiltersHint => T("名前や説明で絞り込み (↑↓ で選択、Enter で追加)", "Search filters (↑↓ select, Enter add)");
    public string InUse => T("使用中", "In use");
    public string Uncategorized => T("分類なし", "Uncategorized");
    public string DefaultValue => T("既定", "Default");
    public string DefaultSuffix => T(" (既定)", " (default)");
    public string Language => T("表示言語", "Language");
    // 選択欄の「自動」だけは今の言語で出す (日本語・English は常にその言語の名前)
    public string LanguageAuto => T("自動", "Auto");
}

using System.Text;
using System.Text.Json;

namespace HWEncParamViewer.Core;

public sealed record FilterSetting(string Text, bool IsEnabled = true);

/// <summary>前回の入力・編集状態・ウィンドウサイズ。フィルタの保存ファイルとは分けて保持する。</summary>
public sealed record AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string EncoderPath { get; init; } = "";
    public string InputPath { get; init; } = "";
    public string InputArgsText { get; init; } = "--avhw";
    public IReadOnlyList<FilterSetting> FilterLines { get; init; } = Array.Empty<FilterSetting>();
    public double WindowWidth { get; init; } = 1440;
    public double WindowHeight { get; init; } = 900;
    public double CurrentSeconds { get; init; }
    public bool IsTextListMode { get; init; }
    public string Language { get; init; } = "auto";

    public static string DefaultPath => Path.Combine(OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "HWEncParamViewer", "settings.json");

    /// <summary>不在・破損・読み取り不能なら既定値を返す。保存失敗はSaveの呼び出し元で扱う。</summary>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? DefaultPath)) ?? new());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>同じディレクトリの一時ファイルを置き換え、保存途中の設定を次回起動時に読まないようにする。</summary>
    public void Save(string? path = null)
    {
        string fullPath = Path.GetFullPath(path ?? DefaultPath);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(this), JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static AppSettings Normalize(AppSettings settings) => settings with
    {
        Language = UiLanguage.Normalize(settings.Language),
        EncoderPath = settings.EncoderPath ?? "",
        InputPath = settings.InputPath ?? "",
        InputArgsText = settings.InputArgsText ?? "--avhw",
        FilterLines = Array.AsReadOnly((settings.FilterLines ?? Array.Empty<FilterSetting>())
            .Where(line => line != null).Select(line => line with { Text = line.Text ?? "" }).ToArray()),
        WindowWidth = double.IsFinite(settings.WindowWidth) && settings.WindowWidth > 0 ? Math.Max(960, settings.WindowWidth) : 1440,
        WindowHeight = double.IsFinite(settings.WindowHeight) && settings.WindowHeight > 0 ? Math.Max(600, settings.WindowHeight) : 900,
        CurrentSeconds = double.IsFinite(settings.CurrentSeconds) && settings.CurrentSeconds >= 0 ? settings.CurrentSeconds : 0,
    };
}

using Avalonia;
using Avalonia.Media;

namespace HWEncVppPreview.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 版の照合だけなら画面や描画の初期化は不要で、画面のない環境でも確認できる。
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine("HWEncVppPreview " + ApplicationVersion.Value);
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Avalonia 12 は FluentTheme の既定フォント Inter を同梱しないため、明示して登録する。
            .WithInterFont()
            // Typeface.Default（$Default）の解決先も Inter にそろえる。未指定だと Linux で GlyphTypeface を作れず描画時に落ちる。
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();
}

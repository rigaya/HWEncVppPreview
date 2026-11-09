using Avalonia;

namespace HWEncParamViewer.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 版の照合だけなら画面や描画の初期化は不要で、画面のない環境でも確認できる。
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine("HWEncParamViewer " + ApplicationVersion.Value);
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

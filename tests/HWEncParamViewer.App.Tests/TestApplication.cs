using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(HWEncParamViewer.App.Tests.TestApplication))]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace HWEncParamViewer.App.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HWEncParamViewer.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(HWEncVppPreview.App.Tests.TestApplication))]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace HWEncVppPreview.App.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HWEncVppPreview.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

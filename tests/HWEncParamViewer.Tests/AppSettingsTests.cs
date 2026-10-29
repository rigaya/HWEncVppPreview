using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void RoundTripsPathsFilterStateAndWindowSize()
    {
        string directory = Path.Combine(Path.GetTempPath(), "viewer-settings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "nested", "settings.json");
        try
        {
            var expected = new AppSettings
            {
                EncoderPath = @"C:\エンコーダ\QSVEncC64.exe", InputPath = @"X:\入力\長い.ts",
                InputArgsText = "--avsw", FilterLines = new[] { new FilterSetting("--vpp-nlmeans sigma=0.05"), new FilterSetting("--vpp-deband", false) },
                WindowWidth = 1280, WindowHeight = 720, CurrentSeconds = 123.45,
            };
            expected.Save(path);
            var actual = AppSettings.Load(path);
            Assert.Equal(expected.EncoderPath, actual.EncoderPath);
            Assert.Equal(expected.InputPath, actual.InputPath);
            Assert.Equal(expected.InputArgsText, actual.InputArgsText);
            Assert.Equal(expected.FilterLines, actual.FilterLines);
            Assert.Equal(1280, actual.WindowWidth);
            Assert.Equal(720, actual.WindowHeight);
            Assert.Equal(123.45, actual.CurrentSeconds);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            (expected with { InputPath = "other.mp4" }).Save(path);
            Assert.Equal("other.mp4", AppSettings.Load(path).InputPath);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MissingInvalidAndNullJsonUseDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "viewer-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Equal(1440, AppSettings.Load(path).WindowWidth);
            File.WriteAllText(path, "{invalid");
            Assert.Equal("--avhw", AppSettings.Load(path).InputArgsText);
            File.WriteAllText(path, "null");
            Assert.Empty(AppSettings.Load(path).FilterLines);
            File.WriteAllText(path, "{\"EncoderPath\":null,\"InputArgsText\":null,\"FilterLines\":[null,{\"Text\":null}],\"WindowWidth\":10,\"WindowHeight\":-1,\"CurrentSeconds\":-9}");
            var actual = AppSettings.Load(path);
            Assert.Equal("", actual.EncoderPath);
            Assert.Equal("--avhw", actual.InputArgsText);
            Assert.Single(actual.FilterLines);
            Assert.Equal("", actual.FilterLines[0].Text);
            Assert.Equal(960, actual.WindowWidth);
            Assert.Equal(900, actual.WindowHeight);
            Assert.Equal(0, actual.CurrentSeconds);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DefaultPathUsesApplicationDirectory()
    {
        Assert.EndsWith(Path.Combine("HWEncParamViewer", "settings.json"), AppSettings.DefaultPath);
        if (!OperatingSystem.IsWindows()) Assert.Contains(Path.DirectorySeparatorChar + ".config" + Path.DirectorySeparatorChar, AppSettings.DefaultPath);
    }
}

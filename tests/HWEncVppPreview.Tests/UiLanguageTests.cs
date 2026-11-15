using System.Globalization;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection { }

[Collection("Language")]
public sealed class UiLanguageTests
{
    [Theory]
    [InlineData("auto", "ja-JP", "ja")]
    [InlineData("auto", "ja", "ja")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "fr-FR", "en")]
    [InlineData("ja", "en-US", "ja")]
    [InlineData("en", "ja-JP", "en")]
    public void 自動設定はOSのUI言語で日本語か英語を選ぶ(string preference, string culture, string expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(preference, CultureInfo.GetCultureInfo(culture)));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void CLIのwatchdog判定は表示言語に依存しない(string language)
    {
        string previous = UiLanguage.Preference;
        try
        {
            UiLanguage.Set(language);
            var result = new HWEncVppPreview.Cli.Program.CliResult();
            HWEncVppPreview.Cli.Program.ApplyUnexpectedFailure(result, new TimeoutException(CoreText.OutputTimedOut));
            Assert.True(result.TimedOut);
            Assert.Equal(CoreText.OutputTimedOut, result.Error);
            Assert.Equal(3, result.ExitCode);
        }
        finally { UiLanguage.Set(previous); }
    }

    [Fact]
    public void 切り替えると分類名と利用者向けの理由が変わる()
    {
        string previous = UiLanguage.Preference;
        try
        {
            UiLanguage.Set("ja");
            string categoryJa = HelpCategories.DisplayName("denoise");
            string? reasonJa = FilterLineEditor.Read("--vpp-unknown", new HelpSchema("")).UnavailableReason;
            Assert.Equal("ノイズ除去", categoryJa);
            Assert.Contains("テキスト", reasonJa);
            UiLanguage.Set("en");
            Assert.Equal("en", UiLanguage.Current);
            Assert.Equal("Denoise", HelpCategories.DisplayName("denoise"));
            string? reasonEn = FilterLineEditor.Read("--vpp-unknown", new HelpSchema("")).UnavailableReason;
            Assert.NotEqual(reasonJa, reasonEn);
            Assert.Contains("text", reasonEn!, StringComparison.OrdinalIgnoreCase);
        }
        finally { UiLanguage.Set(previous); }
    }

    [Fact]
    public void 自動設定は同じ指定でもUI言語が変われば再解決し通知する()
    {
        string previous = UiLanguage.Preference;
        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        int notifications = 0;
        void Changed() => ++notifications;
        try
        {
            UiLanguage.Set("ja");
            UiLanguage.Changed += Changed;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            UiLanguage.Set("auto");
            Assert.Equal("en", UiLanguage.Current);
            Assert.Equal(1, notifications);
            UiLanguage.Set("auto");
            Assert.Equal(1, notifications);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            UiLanguage.Set("auto");
            Assert.Equal("ja", UiLanguage.Current);
            Assert.Equal(2, notifications);
        }
        finally
        {
            UiLanguage.Changed -= Changed;
            CultureInfo.CurrentUICulture = previousCulture;
            UiLanguage.Set(previous);
        }
    }

    [Fact]
    public void 不明な言語設定は自動へ正規化する()
    {
        Assert.Equal("auto", UiLanguage.Normalize("unknown"));
        Assert.Equal("auto", UiLanguage.Normalize(null));
        Assert.Equal("en", UiLanguage.Resolve("unknown", CultureInfo.GetCultureInfo("fr-FR")));
    }

    [Fact]
    public void 言語設定を保存して再読み込みする()
    {
        string directory = Path.Combine(Path.GetTempPath(), "viewer-language-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Assert.Equal("auto", new AppSettings().Language);
            new AppSettings { Language = "en" }.Save(path);
            Assert.Equal("en", AppSettings.Load(path).Language);
            new AppSettings { Language = "ja" }.Save(path);
            Assert.Equal("ja", AppSettings.Load(path).Language);
            new AppSettings { Language = "unknown" }.Save(path);
            Assert.Equal("auto", AppSettings.Load(path).Language);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }
}

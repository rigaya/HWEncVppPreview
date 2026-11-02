using System.Globalization;
using System.Runtime.CompilerServices;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

/// <summary>既存の文言の期待値とautoの判定を、日本語環境に固定する。</summary>
internal static class LanguageTestSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var japanese = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.CurrentUICulture = japanese;
        CultureInfo.DefaultThreadCurrentUICulture = japanese;
        UiLanguage.Set("ja");
    }
}

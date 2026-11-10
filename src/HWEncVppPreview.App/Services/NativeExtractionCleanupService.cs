using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Services;

/// <summary>画面表示後だけ、単一ファイル配布物の古いnative展開先を背景で掃除する。</summary>
internal static class NativeExtractionCleanupService
{
    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "単一ファイルで空になるLocationを判別にだけ使い、展開先はRuntimeの検索情報から取る。")]
    internal static void Start()
    {
        try
        {
            var entry = Assembly.GetEntryAssembly();
            if (entry == null || entry.Location.Length != 0) return;
            _ = Task.Run(Clean);
        }
        catch { /* 掃除を始められなくても、通常の起動を続ける。 */ }
    }

    private static void Clean()
    {
        try
        {
            string? search = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string;
            string? directory = NativeExtractionCleanup.ResolveCurrentDirectory(true, search);
            NativeExtractionCleanup.Clean(directory);
        }
        catch { /* 場所の取得や使用中ファイルの問題は、次回に回す。 */ }
    }

}

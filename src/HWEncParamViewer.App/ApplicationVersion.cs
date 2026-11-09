using System.Reflection;

namespace HWEncParamViewer.App;

/// <summary>画面と起動引数の版を、ビルドで付与した同じ文字列から取得する。</summary>
internal static class ApplicationVersion
{
    internal static string Value { get; } = typeof(ApplicationVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("製品の版情報が設定されていません。");
}

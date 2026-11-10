using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Services;

/// <summary>実行環境の情報だけを集め、探索の順序はCoreの共通関数へ任せる。</summary>
internal static class EncoderDiscoveryEnvironment
{
    internal static string? Find()
    {
        EncoderPlatform platform = OperatingSystem.IsWindows() ? EncoderPlatform.Windows
            : OperatingSystem.IsLinux() ? EncoderPlatform.Linux : EncoderPlatform.Other;
        return EncoderDiscovery.Find(AppContext.BaseDirectory, platform,
            platform == EncoderPlatform.Linux ? Environment.GetEnvironmentVariable("PATH") : null,
            File.Exists, HasExecutePermission);
    }

    private static bool HasExecutePermission(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        // 外部ライブラリを呼ばず、いずれかの実行ビットがあれば候補にする。
        // 所有者などによる実際の実行可否は、起動時のエラーとして扱う。
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        // 存在確認後に消えた場合や権限で読めない候補は、起動時の探索から外す。
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

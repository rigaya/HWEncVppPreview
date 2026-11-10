namespace HWEncVppPreview.Core;

/// <summary>探索先の名前と経路表記を、実行中の環境から独立して選ぶ。</summary>
public enum EncoderPlatform
{
    Windows,
    Linux,
    Other,
}

/// <summary>配布先と絶対パスの検索経路からエンコーダを探す。</summary>
public static class EncoderDiscovery
{
    private static readonly string[] WindowsNames = ["QSVEncC64.exe", "NVEncC64.exe", "VCEEncC64.exe"];
    private static readonly string[] LinuxNames = ["qsvencc", "nvencc", "vceencc", "rkmppenc"];

    /// <summary>
    /// 本体と同じフォルダを先に探し、Linuxだけ検索経路を続けて探す。
    /// 明示指定を優先する判断は呼出し側で行う。
    /// ファイルの存在と実行権限を外から渡すので、探索順を実ファイルなしで検証できる。
    /// </summary>
    public static string? Find(string baseDirectory, EncoderPlatform platform,
        string? searchPath, Func<string, bool> fileExists, Func<string, bool> isExecutable)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(isExecutable);
        string[] names = platform switch
        {
            EncoderPlatform.Windows => WindowsNames,
            EncoderPlatform.Linux => LinuxNames,
            _ => [],
        };
        if (baseDirectory.Length != 0)
        {
            foreach (string name in names)
            {
                string candidate = Combine(baseDirectory, name, platform);
                if (Usable(candidate)) return candidate;
            }
        }
        if (platform != EncoderPlatform.Linux || string.IsNullOrEmpty(searchPath)) return null;
        // 起動時のカレントに依存する空要素と相対フォルダは探索しない。
        // 候補名の優先順を先に適用し、同じ名前を各フォルダの順に調べる。
        string[] directories = searchPath.Split(':');
        foreach (string name in names)
        {
            foreach (string directory in directories)
            {
                if (!directory.StartsWith('/')) continue;
                string candidate = Combine(directory, name, platform);
                if (Usable(candidate)) return candidate;
            }
        }
        return null;

        bool Usable(string candidate) => fileExists(candidate)
            && (platform != EncoderPlatform.Linux || isExecutable(candidate));
    }

    private static string Combine(string directory, string name, EncoderPlatform platform)
    {
        if (directory.Length == 0) return name;
        // 別の環境で探索順を試験できるよう、実行中の環境の区切り文字へ依存しない。
        return platform == EncoderPlatform.Windows
            ? directory.TrimEnd('\\', '/') + "\\" + name
            : directory.TrimEnd('/') + "/" + name;
    }
}

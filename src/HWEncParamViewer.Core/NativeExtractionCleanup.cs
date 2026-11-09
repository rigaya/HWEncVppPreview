namespace HWEncParamViewer.Core;

/// <summary>列挙した展開先の候補。時刻とリンク属性を選択処理から分離する。</summary>
internal sealed record NativeExtractionDirectory(string Path, DateTimeOffset LastWriteUtc, bool IsReparsePoint);

/// <summary>現在の展開先を守り、古い兄弟のキャッシュだけを可能な範囲で回収する。</summary>
internal static class NativeExtractionCleanup
{
    private const string ApplicationDirectoryName = "HWEncParamViewer";
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(10);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string? ResolveCurrentDirectory(bool isSingleFile, string? nativeSearchDirectories)
    {
        if (!isSingleFile) return null;
        var candidates = (nativeSearchDirectories ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeDirectory).Where(path => CacheParent(path) != null).Cast<string>()
            .Distinct(PathComparer).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    internal static IReadOnlyList<string> SelectDirectories(string? currentDirectory,
        IEnumerable<NativeExtractionDirectory> candidates, DateTimeOffset now)
    {
        string? current = NormalizeDirectory(currentDirectory);
        string? parent = CacheParent(current);
        if (parent == null) return Array.Empty<string>();
        var selected = new List<string>();
        foreach (var candidate in candidates)
        {
            string? path = NormalizeDirectory(candidate.Path);
            if (path == null || candidate.IsReparsePoint || PathComparer.Equals(path, current)
                || !PathComparer.Equals(Path.GetDirectoryName(path), parent)
                || now - candidate.LastWriteUtc <= GracePeriod) continue;
            if (!selected.Contains(path, PathComparer)) selected.Add(path);
        }
        return selected;
    }

    internal static void Clean(string? currentDirectory, TimeProvider? timeProvider = null)
    {
        try
        {
            string? current = NormalizeDirectory(currentDirectory);
            string? parent = CacheParent(current);
            if (parent == null || !Directory.Exists(current)) return;
            DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            foreach (string path in Directory.EnumerateDirectories(parent))
            {
                try
                {
                    var candidate = ReadDirectory(path);
                    if (SelectDirectories(current, new[] { candidate }, now).Count == 0) continue;
                    Directory.Delete(path, recursive: true);
                }
                catch { /* 使用中や列挙途中の変化は、その候補だけ次回へ回す。 */ }
            }
        }
        catch { /* 取得できない展開先や終了途中の失敗は、起動を妨げない。 */ }
    }

    private static string? NormalizeDirectory(string? path)
    {
        try
        {
            return string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) ? null
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch { return null; }
    }

    private static string? CacheParent(string? directory)
    {
        if (directory == null) return null;
        string? parent = Path.GetDirectoryName(directory);
        return parent != null && string.Equals(Path.GetFileName(parent), ApplicationDirectoryName,
            StringComparison.Ordinal) ? parent : null;
    }

    private static NativeExtractionDirectory ReadDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0) throw new IOException("候補がディレクトリではありません。");
        return new(path, new DateTimeOffset(Directory.GetLastWriteTimeUtc(path)),
            (attributes & FileAttributes.ReparsePoint) != 0);
    }

}

namespace HWEncVppPreview.Core;

/// <summary>列挙した展開先の候補。時刻とリンク属性を選択処理から分離する。</summary>
internal sealed record NativeExtractionDirectory(string Path, DateTimeOffset LastWriteUtc, bool IsReparsePoint);

/// <summary>現在の展開先を守り、古い兄弟のキャッシュだけを可能な範囲で回収する。</summary>
internal static class NativeExtractionCleanup
{
    private const string ApplicationDirectoryName = "HWEncVppPreview";
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(10);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string? ResolveCurrentDirectory(bool isSingleFile, string? nativeSearchDirectories,
        IReadOnlyList<string>? extractionBases = null)
    {
        if (!isSingleFile) return null;
        // 検索一覧には実行ファイルの場所も入る。親名が一致しても、既定の .net の外は展開先ではないので捨てる。
        IReadOnlyList<string> bases = extractionBases ?? ExtractionBases();
        var candidates = (nativeSearchDirectories ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeDirectory).Where(path => IsUnderBase(path, bases) && CacheParent(path) != null).Cast<string>()
            .Distinct(PathComparer).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    internal static IReadOnlyList<string> ExtractionBases()
    {
        string? root = NormalizeDirectory(DefaultExtractionRoot());
        return root == null ? Array.Empty<string>() : new[] { root };
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
        => Clean(currentDirectory, Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"), timeProvider);

    internal static void Clean(string? currentDirectory, string? configuredBase, TimeProvider? timeProvider)
    {
        // 展開先を利用者が指定した場合、その中に何があるかは利用者の管理なので一切削除しない。
        if (!string.IsNullOrEmpty(configuredBase)) return;
        try
        {
            string? current = NormalizeDirectory(currentDirectory);
            // 展開根の外は、親名が一致しても利用者のディレクトリなので触らない。
            if (!IsUnderBase(current, ExtractionBases())) return;
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

    internal static string DefaultExtractionRoot()
    {
        string root = OperatingSystem.IsWindows()
            ? Path.GetTempPath()
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(root, ".net");
    }

    private static bool IsUnderBase(string? path, IReadOnlyList<string> bases)
    {
        if (path == null) return false;
        foreach (string root in bases)
        {
            if (PathComparer.Equals(path, root)
                || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)) return true;
        }
        return false;
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

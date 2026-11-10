using System.Diagnostics;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class NativeExtractionCleanupTests
{
    private static readonly DateTimeOffset Now = new(2040, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(599, false)]
    [InlineData(600, false)]
    [InlineData(601, true)]
    public void Selectは未来と10分以内を残す(int secondsOld, bool selected)
    {
        string parent = VirtualPath("HWEncVppPreview");
        string old = Path.Combine(parent, "old");
        var result = Select(Path.Combine(parent, "current"), new NativeExtractionDirectory(old, Now.AddSeconds(-secondsOld), false));
        Assert.Equal(selected ? new[] { old } : Array.Empty<string>(), result);
    }

    [Fact]
    public void Selectの10分境界は1tick超過で初めて対象になる()
    {
        string parent = VirtualPath("HWEncVppPreview");
        string boundary = Path.Combine(parent, "boundary");
        string beyond = Path.Combine(parent, "beyond");
        Assert.Equal(new[] { beyond }, Select(Path.Combine(parent, "current"),
            new NativeExtractionDirectory(boundary, Now.AddMinutes(-10), false),
            new NativeExtractionDirectory(beyond, Now.AddMinutes(-10).AddTicks(-1), false)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Selectは現行の場所が不明なら何も選ばない(string? current)
    {
        Assert.Empty(Select(current, new NativeExtractionDirectory(VirtualPath("HWEncVppPreview", "old"), Now.AddHours(-1), false)));
    }

    [Theory]
    [InlineData("other")]
    [InlineData("hwencvpppreview")]
    [InlineData("HWENCVPPPREVIEW")]
    [InlineData("HWEncVppPreview-extra")]
    public void Selectは親名がOrdinalで完全一致する場合だけ選ぶ(string parentName)
    {
        string parent = VirtualPath(parentName);
        Assert.Empty(Select(Path.Combine(parent, "current"), new NativeExtractionDirectory(Path.Combine(parent, "old"), Now.AddHours(-1), false)));
    }

    [Fact]
    public void Selectは自分と外側と孫とprefixが似た場所とリンクを除く()
    {
        string parent = VirtualPath("HWEncVppPreview");
        string current = Path.Combine(parent, "current");
        string valid = Path.Combine(parent, "valid");
        Assert.Equal(new[] { valid }, Select(current,
            new NativeExtractionDirectory(current, Now.AddHours(-1), false),
            new NativeExtractionDirectory(parent, Now.AddHours(-1), false),
            new NativeExtractionDirectory(Path.Combine(parent, "old", "grandchild"), Now.AddHours(-1), false),
            new NativeExtractionDirectory(Path.Combine(parent + "-backup", "outside"), Now.AddHours(-1), false),
            new NativeExtractionDirectory(VirtualPath("another", "outside"), Now.AddHours(-1), false),
            new NativeExtractionDirectory(Path.Combine(parent, "linked"), Now.AddHours(-1), true),
            new NativeExtractionDirectory(valid, Now.AddHours(-1), false)));
    }

    [Fact]
    public void Selectは正規化した直接の兄弟を返す()
    {
        string parent = VirtualPath("HWEncVppPreview");
        string current = Path.Combine(parent, "unused", "..", "current");
        string candidate = Path.Combine(parent, "unused", "..", "old");
        Assert.Equal(new[] { Path.GetFullPath(candidate) }, Select(current,
            new NativeExtractionDirectory(Path.GetFullPath(current), Now.AddHours(-1), false),
            new NativeExtractionDirectory(candidate, Now.AddHours(-1), false)));
    }

    [Fact]
    public void Resolveは同じnative候補の重複を一つとして扱う()
    {
        string native = VirtualPath("HWEncVppPreview", "native");
        string same = Path.Combine(native, "..", "native");
        Assert.Equal(native, NativeExtractionCleanup.ResolveCurrentDirectory(true,
            JoinDirectories(native, native, same)));
    }

    [Fact]
    public void Resolveはsinglefileの唯一のRuntime候補だけを返す()
    {
        string current = VirtualPath("HWEncVppPreview", "current");
        string search = JoinDirectories("", VirtualPath("other", "invalid"), current, "");
        Assert.Equal(current, NativeExtractionCleanup.ResolveCurrentDirectory(true, search));
        Assert.Null(NativeExtractionCleanup.ResolveCurrentDirectory(false, search));
        Assert.Null(NativeExtractionCleanup.ResolveCurrentDirectory(true, null));
        Assert.Null(NativeExtractionCleanup.ResolveCurrentDirectory(true, ""));
        Assert.Null(NativeExtractionCleanup.ResolveCurrentDirectory(true,
            JoinDirectories(current, VirtualPath("HWEncVppPreview", "another"))));
    }

    [Fact]
    public void Cleanは古い兄弟だけ削除し現行と最近と未来を残す()
    {
        using var tree = new TestTree();
        string current = tree.Entry("current", Now.AddHours(-1));
        string old = tree.Entry("old", Now.AddMinutes(-11));
        string recent = tree.Entry("recent", Now.AddMinutes(-9));
        string boundary = tree.Entry("boundary", Now.AddMinutes(-10));
        string future = tree.Entry("future", Now.AddHours(1));
        NativeExtractionCleanup.Clean(current, new FixedClock());
        Assert.False(Directory.Exists(old));
        foreach (string path in new[] { current, recent, boundary, future }) AssertPayload(path);
    }

    [Fact]
    public void Cleanは古い兄弟にある通常の木を再帰削除する()
    {
        using var tree = new TestTree();
        string current = tree.Entry("current", Now);
        string old = tree.Entry("old", Now.AddHours(-1));
        string nested = Path.Combine(old, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "payload.txt"), "nested");
        Directory.SetLastWriteTimeUtc(old, Now.AddHours(-1).UtcDateTime);
        NativeExtractionCleanup.Clean(current, new FixedClock());
        Assert.False(Directory.Exists(old));
        AssertPayload(current);
    }

    [Fact]
    public void Cleanは違う親名なら古い兄弟も触らない()
    {
        using var tree = new TestTree("HWEncVppPreview-extra");
        string current = tree.Entry("current", Now);
        string old = tree.Entry("old", Now.AddHours(-1));
        NativeExtractionCleanup.Clean(current, new FixedClock());
        AssertPayload(current);
        AssertPayload(old);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Cleanは現行が不明なら何もしない(string? current)
    {
        using var tree = new TestTree();
        string old = tree.Entry("old", Now.AddHours(-1));
        NativeExtractionCleanup.Clean(current, new FixedClock());
        AssertPayload(old);
    }

    [Fact]
    public void Cleanは現行または親が存在しない場合も例外を出さない()
    {
        using var tree = new TestTree();
        string old = tree.Entry("old", Now.AddHours(-1));
        NativeExtractionCleanup.Clean(Path.Combine(tree.Parent, "missing"), new FixedClock());
        NativeExtractionCleanup.Clean(Path.Combine(tree.Root, "missing", "HWEncVppPreview", "missing"), new FixedClock());
        AssertPayload(old);
    }

    [Fact]
    public void Cleanは直接のリンクをたどらず他の古い兄弟を処理する()
    {
        using var tree = new TestTree();
        string current = tree.Entry("current", Now);
        string target = tree.OutsideEntry("target");
        string link = Path.Combine(tree.Parent, "link");
        tree.Link(link, target);
        string old = tree.Entry("old", Now.AddHours(-1));
        NativeExtractionCleanup.Clean(current, new FixedClock());
        AssertPayload(target);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void Cleanは配下のリンク先を残して古い木を削除する()
    {
        using var tree = new TestTree();
        string current = tree.Entry("current", Now);
        string linked = tree.Entry("linked", Now.AddHours(-1));
        string target = tree.OutsideEntry("target");
        Directory.CreateDirectory(Path.Combine(linked, "nested"));
        tree.Link(Path.Combine(linked, "nested", "nested-link"), target);
        Directory.SetLastWriteTimeUtc(linked, Now.AddHours(-1).UtcDateTime);
        string old = tree.Entry("old", Now.AddHours(-1));
        NativeExtractionCleanup.Clean(current, new FixedClock());
        Assert.False(Directory.Exists(linked));
        AssertPayload(target);
        Assert.False(Directory.Exists(old));
    }

    [WindowsFact]
    public void CleanはWindowsで排他利用中の候補が失敗しても別の古い兄弟を削除する()
    {
        using var tree = new TestTree();
        string current = tree.Entry("current", Now);
        string locked = tree.Entry("00-locked", Now.AddHours(-1));
        string old = tree.Entry("99-old", Now.AddHours(-1));
        using var held = new FileStream(Path.Combine(locked, "payload.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        NativeExtractionCleanup.Clean(current, new FixedClock());
        AssertPayload(current);
        Assert.True(Directory.Exists(locked));
        Assert.True(File.Exists(Path.Combine(locked, "payload.txt")));
        Assert.False(Directory.Exists(old));
    }

    private static IReadOnlyList<string> Select(string? current, params NativeExtractionDirectory[] directories) =>
        NativeExtractionCleanup.SelectDirectories(current, directories, Now);

    private static string VirtualPath(params string[] parts) =>
        Path.GetFullPath(Path.Combine(new[] { "native-cleanup-tests" }.Concat(parts).ToArray()));

    private static string JoinDirectories(params string[] paths) => string.Join(Path.PathSeparator, paths);

    private static void AssertPayload(string directory) => Assert.Equal("sentinel", File.ReadAllText(Path.Combine(directory, "payload.txt")));

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windowsの削除共有モードを検証するため、この検査だけWindowsで実行する。";
        }
    }

    private sealed class TestTree : IDisposable
    {
        private readonly List<string> _links = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "viewer-native-cleanup-" + Guid.NewGuid().ToString("N"));
        public string Parent { get; }

        public TestTree(string parentName = "HWEncVppPreview")
        {
            Parent = Path.Combine(Root, parentName);
            Directory.CreateDirectory(Parent);
        }

        public string Entry(string name, DateTimeOffset timestamp) => MakeEntry(Path.Combine(Parent, name), timestamp);

        public string OutsideEntry(string name) => MakeEntry(Path.Combine(Root, "outside", name), Now.AddHours(-1));

        internal static string MakeEntry(string path, DateTimeOffset timestamp)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "payload.txt"), "sentinel");
            Directory.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
            return path;
        }

        public void Link(string link, string target)
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception exception) when (OperatingSystem.IsWindows()
                && exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // Windowsでシンボリックリンクの権限がなくても、同じ一時領域内のジャンクションで保護を検査する。
                var start = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("ジャンクション作成を開始できません。");
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                    throw new IOException("ジャンクション作成が期限内に完了しません。");
                }
                Assert.True(process.ExitCode == 0, "ジャンクション作成失敗: "
                    + stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
            }
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            _links.Add(link);
        }

        public void Dispose()
        {
            // 後片付けでもリンク先をたどらないよう、登録したリンクを先に非再帰で外す。
            foreach (string link in _links.OrderByDescending(path => path.Length))
            {
                try { Directory.Delete(link); }
                catch (DirectoryNotFoundException) { }
            }
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

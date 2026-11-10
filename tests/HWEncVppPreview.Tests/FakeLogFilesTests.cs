using System.Diagnostics;
using System.Text.Json;
using HWEncVppPreview.Core;
using HWEncVppPreview.Testing;

namespace HWEncVppPreview.Tests;

public sealed class FakeLogFilesTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [Fact]
    public void 旧baseとPID別記録を集約して各PIDの受信順を守る()
    {
        using var files = new Files();
        File.WriteAllText(files.Log, "{\"cmd\":\"legacy\"}\n");
        File.WriteAllText(files.ForPid(files.Log, 101), Line("second", 101, 2, 100) + Line("first", 101, 1, 300));
        File.WriteAllText(files.ForPid(files.Log, 102), Line("other", 102, 1, 200));
        string[] commands = FakeLogFiles.ReadAllLines(files.Log).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("cmd").GetString()!;
        }).ToArray();
        Assert.Equal(new[] { "legacy", "other", "first", "second" }, commands);
    }

    [Fact]
    public void 未完行は次回へ回して完了したら一度だけ読む()
    {
        using var files = new Files();
        File.WriteAllText(files.Log, "{\"cmd\":\"first\"}\n{\"cmd\":");
        Assert.Single(FakeLogFiles.ReadAllLines(files.Log));
        File.AppendAllText(files.Log, "\"second\"}\n");
        Assert.Equal(2, FakeLogFiles.ReadAllLines(files.Log).Length);
        Assert.Equal(2, FakeLogFiles.ReadAllLines(files.Log).Length);
    }

    [Fact]
    public void 完了した不正JSONは取りこぼしとして隠さない()
    {
        using var files = new Files();
        File.WriteAllText(files.Log, "{\n");
        Assert.ThrowsAny<JsonException>(() => FakeLogFiles.ReadAllLines(files.Log));
    }

    [Fact]
    public void 旧PID一覧も読み数値PID以外の別ファイルを削除しない()
    {
        using var files = new Files();
        File.WriteAllText(files.Pids, "99\n");
        File.WriteAllText(files.ForPid(files.Pids, 101), "101\n");
        File.WriteAllText(files.ForPid(files.Pids, 102), "102\n");
        string unrelated = Path.Combine(files.DirectoryPath, "pids.latest.txt");
        File.WriteAllText(unrelated, "残す");
        Assert.True(FakeLogFiles.Exists(files.Pids));
        Assert.Equal(new[] { 99, 101, 102 }, FakeLogFiles.ReadAllLines(files.Pids).Select(int.Parse).Order().ToArray());
        FakeLogFiles.Delete(files.Pids);
        Assert.False(FakeLogFiles.Exists(files.Pids));
        Assert.Equal("残す", File.ReadAllText(unrelated));
    }

    [Fact]
    public async Task 並列の実Fake起動も各暖機runとquitをPID別に全件残す()
    {
        using var files = new Files();
        var backends = Enumerable.Range(0, 4).Select(_ => new ServerBackend(configureStartInfo: info =>
        {
            info.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = files.Log;
            info.Environment["FAKE_ENCODER_PID_FILE"] = files.Pids;
        })).ToArray();
        int[] pids = Array.Empty<int>();
        try
        {
            await Task.WhenAll(backends.Select(backend => backend.InitializeEncoderAsync(BackendTests.FakeEncoderPath())))
                .WaitAsync(Safety);
            pids = FakeLogFiles.ReadAllLines(files.Pids).Select(int.Parse).Order().ToArray();
            Assert.Equal(backends.Length, pids.Length);
            Assert.Equal(backends.Length, pids.Distinct().Count());
            Assert.False(File.Exists(files.Pids));
            Assert.False(File.Exists(files.Log));
            Assert.Equal(backends.Length, FakeLogFiles.ReadAllLines(files.Log).Length);
            var sinks = backends.Select(_ => new Sink()).ToArray();
            var request = new PreviewRequest
            {
                EncoderPath = BackendTests.FakeEncoderPath(), InputFile = "log-test.ts", Fps = 30,
                TargetSeconds = 0, PreMarginSeconds = 0, PostMarginSeconds = 0,
            };
            PreviewRunResult[] results = await Task.WhenAll(backends.Select((backend, index) => backend.RunAsync(request, sinks[index])))
                .WaitAsync(Safety);
            Assert.All(results, result => Assert.Equal(0, result.ExitCode));
            Assert.All(sinks, sink => Assert.Equal(1, sink.Frames));
        }
        finally
        {
            await Task.WhenAll(backends.Select(backend => backend.DisposeAsync().AsTask())).WaitAsync(Safety);
        }
        string[] lines = FakeLogFiles.ReadAllLines(files.Log);
        Assert.Equal(backends.Length * 3, lines.Length);
        var grouped = lines.Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return (Pid: root.GetProperty("_fakeLogPid").GetInt32(), Sequence: root.GetProperty("_fakeLogSequence").GetInt64(),
                Command: root.GetProperty("cmd").GetString());
        }).GroupBy(entry => entry.Pid).OrderBy(group => group.Key).ToArray();
        Assert.Equal(pids, grouped.Select(group => group.Key).ToArray());
        foreach (var group in grouped)
        {
            Assert.Equal(new long[] { 1, 2, 3 }, group.Select(entry => entry.Sequence).ToArray());
            Assert.Equal(new[] { "run", "run", "quit" }, group.Select(entry => entry.Command).ToArray());
            Assert.True(File.Exists(files.ForPid(files.Log, group.Key)));
            Assert.True(File.Exists(files.ForPid(files.Pids, group.Key)));
            AssertStopped(group.Key);
        }
    }

    private static string Line(string command, int pid, long sequence, long timestamp)
        => JsonSerializer.Serialize(new { cmd = command, _fakeLogPid = pid, _fakeLogSequence = sequence, _fakeLogTimestamp = timestamp }) + "\n";

    private static void AssertStopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }

    private sealed class Sink : IPreviewSink
    {
        public int Frames;
        public void OnOutput() { }
        public void OnHeader(Y4mHeader header) { }
        public void OnFrame(Y4mFrame frame) => ++Frames;
    }

    private sealed class Files : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "fake-log-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(DirectoryPath);
        public string Log => Path.Combine(DirectoryPath, "commands.jsonl");
        public string Pids => Path.Combine(DirectoryPath, "pids.txt");
        public string ForPid(string path, int pid) => Path.Combine(DirectoryPath,
            Path.GetFileNameWithoutExtension(path) + "." + pid + Path.GetExtension(path));
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}

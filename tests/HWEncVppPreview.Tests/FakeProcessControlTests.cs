using HWEncVppPreview.Testing;
using System.Diagnostics;
using System.Text.Json;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class FakeProcessControlTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task IgnoredQuitIsLoggedAndNextRequestStillRuns()
    {
        string directory = CreateDirectory();
        string log = Path.Combine(directory, "commands.jsonl");
        using var process = Start(new Dictionary<string, string>
        {
            ["FAKE_ENCODER_IGNORE_QUIT"] = "1",
            ["FAKE_ENCODER_COMMAND_LOG_FILE"] = log,
        });
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var reader = new PreviewProtocolReader(process.StandardOutput.BaseStream);
            Assert.IsType<PreviewReady>(await NextAsync(reader));
            await SendAsync(process, new { cmd = "quit" });
            await SendAsync(process, new { cmd = "run", id = 7, args = new[] { "--frames", "1" } });
            while (await NextAsync(reader) is not PreviewEnd end || end.Id != 7) { }
            Assert.False(process.HasExited);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(SafetyTimeout);
            Assert.Equal(0, process.ExitCode);
            string[] commands = FakeLogFiles.ReadAllLines(log);
            Assert.Equal(2, commands.Length);
            using var quit = JsonDocument.Parse(commands[0]);
            using var run = JsonDocument.Parse(commands[1]);
            Assert.Equal("quit", quit.RootElement.GetProperty("cmd").GetString());
            Assert.Equal(7, run.RootElement.GetProperty("id").GetInt64());
        }
        finally
        {
            await StopAsync(process);
            await stderr.WaitAsync(SafetyTimeout);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InheritedPipesStayOpenUntilOwnedChildIsReleased()
    {
        string directory = CreateDirectory();
        string ready = Path.Combine(directory, "ready");
        string release = Path.Combine(directory, "release");
        string exited = Path.Combine(directory, "exited");
        using var parent = Start(new Dictionary<string, string>
        {
            ["FAKE_ENCODER_HOLD_OUTPUT_READY_FILE"] = ready,
            ["FAKE_ENCODER_HOLD_OUTPUT_RELEASE_FILE"] = release,
            ["FAKE_ENCODER_HOLD_OUTPUT_EXIT_FILE"] = exited,
        });
        Process? child = null;
        var stderr = parent.StandardError.ReadToEndAsync();
        try
        {
            var reader = new PreviewProtocolReader(parent.StandardOutput.BaseStream);
            Assert.IsType<PreviewReady>(await NextAsync(reader));
            await SendAsync(parent, new { cmd = "quit" });
            await WaitForFileAsync(ready);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready), System.Globalization.CultureInfo.InvariantCulture));
            await parent.WaitForExitAsync().WaitAsync(SafetyTimeout);
            Assert.Equal(0, parent.ExitCode);
            var eof = parent.StandardOutput.ReadToEndAsync();
            Assert.False(eof.IsCompleted);
            Assert.False(stderr.IsCompleted);
            await File.WriteAllTextAsync(release, "");
            await child.WaitForExitAsync().WaitAsync(SafetyTimeout);
            Assert.Equal("", await eof.WaitAsync(SafetyTimeout));
            Assert.Equal("0", await File.ReadAllTextAsync(exited));
        }
        finally
        {
            // 途中のassert失敗でも自分で起動した子を解放し、最後まで回収する。
            await File.WriteAllTextAsync(release, "");
            await StopAsync(parent);
            if (child == null && File.Exists(ready))
            {
                int pid = int.Parse(await File.ReadAllTextAsync(ready), System.Globalization.CultureInfo.InvariantCulture);
                try { child = Process.GetProcessById(pid); }
                catch (ArgumentException) { }
            }
            if (child != null)
            {
                try { await child.WaitForExitAsync().WaitAsync(SafetyTimeout); }
                catch (TimeoutException) { await StopAsync(child); }
                child.Dispose();
            }
            await stderr.WaitAsync(SafetyTimeout);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fake-process-" + Guid.NewGuid().ToString("N"))).FullName;

    private static Process Start(Dictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(BackendTests.FakeEncoderPath())
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string key in new[] { "FAKE_ENCODER_IGNORE_QUIT", "FAKE_ENCODER_HOLD_OUTPUT_READY_FILE",
            "FAKE_ENCODER_HOLD_OUTPUT_RELEASE_FILE", "FAKE_ENCODER_HOLD_OUTPUT_EXIT_FILE", "FAKE_ENCODER_COMMAND_LOG_FILE" })
            start.Environment.Remove(key);
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        start.ArgumentList.Add("--preview-server");
        return Process.Start(start) ?? throw new InvalidOperationException("偽エンコーダを起動できませんでした");
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(SafetyTimeout);
    }

    private static async Task<PreviewEvent> NextAsync(PreviewProtocolReader reader)
        => await Task.Run(() => reader.ReadEvent() ?? throw new EndOfStreamException()).WaitAsync(SafetyTimeout);

    private static async Task SendAsync(Process process, object command)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync();
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => ready.TrySetResult();
        watcher.Renamed += (_, _) => { if (File.Exists(path)) ready.TrySetResult(); };
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path)) return;
        await ready.Task.WaitAsync(SafetyTimeout);
    }
}

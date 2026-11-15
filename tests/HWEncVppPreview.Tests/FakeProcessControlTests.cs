using HWEncVppPreview.Testing;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class FakeProcessControlTests
{
    private static readonly TimeSpan SafetyTimeout = TestTimeouts.Hang;

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
            await process.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
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
            await stderr.WaitWithDiagnosticsAsync(SafetyTimeout);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PipeWaitReportsEarlyFailureAndItsDiagnostic()
    {
        string name = "early-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task operation = Task.FromException(new InputProbeException("失敗", 23, "子の診断"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PipeWait.UntilConnectedAsync(pipe, name, operation, SafetyTimeout));
        Assert.Contains(name, error.Message);
        Assert.Contains("子の診断", error.Message);
    }

    [Fact]
    public async Task InheritedPipesStayOpenUntilOwnedChildIsReleased()
    {
        string pipeName = "fake-output-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var parent = Start(new Dictionary<string, string> { ["FAKE_ENCODER_HOLD_OUTPUT_PIPE"] = pipeName });
        Process? child = null;
        var stderr = parent.StandardError.ReadToEndAsync();
        try
        {
            var reader = new PreviewProtocolReader(parent.StandardOutput.BaseStream);
            Assert.IsType<PreviewReady>(await NextAsync(reader));
            await SendAsync(parent, new { cmd = "quit" });
            await PipeWait.UntilConnectedAsync(pipe, pipeName, SafetyTimeout);
            using var pipeReader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            string pid = await pipeReader.ReadLineAsync().WaitWithDiagnosticsAsync(SafetyTimeout) ?? throw new EndOfStreamException();
            child = Process.GetProcessById(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture));
            await parent.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.Equal(0, parent.ExitCode);
            var eof = parent.StandardOutput.ReadToEndAsync();
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("release");
            await child.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.Equal("0", await pipeReader.ReadLineAsync().WaitWithDiagnosticsAsync(SafetyTimeout));
            Assert.Equal("", await eof.WaitWithDiagnosticsAsync(SafetyTimeout));
            Assert.Equal("", await stderr.WaitWithDiagnosticsAsync(SafetyTimeout));
        }
        finally
        {
            // 途中で失敗しても、この試験が起動したプロセスを終了させる。
            await StopAsync(parent);
            if (child != null)
            {
                await StopAsync(child);
                child.Dispose();
            }
            await stderr.WaitWithDiagnosticsAsync(SafetyTimeout);
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
        foreach (string key in new[] { "FAKE_ENCODER_IGNORE_QUIT", "FAKE_ENCODER_HOLD_OUTPUT_PIPE", "FAKE_ENCODER_COMMAND_LOG_FILE" })
            start.Environment.Remove(key);
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        start.ArgumentList.Add("--preview-server");
        return Process.Start(start) ?? throw new InvalidOperationException("偽エンコーダを起動できませんでした");
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
    }

    private static async Task<PreviewEvent> NextAsync(PreviewProtocolReader reader)
        => await Task.Run(() => reader.ReadEvent() ?? throw new EndOfStreamException()).WaitWithDiagnosticsAsync(SafetyTimeout);

    private static async Task SendAsync(Process process, object command)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync();
    }

}

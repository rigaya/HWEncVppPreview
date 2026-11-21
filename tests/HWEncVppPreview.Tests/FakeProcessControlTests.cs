using HWEncVppPreview.Testing;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class FakeProcessControlTests
{
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
            await process.WaitForExitAsync();
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
            await stderr;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PipeWaitReportsEarlyFailureAndItsDiagnostic()
    {
        string name = "early-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task operation = Task.FromException(new InputProbeException("失敗", 23, "子の診断"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PipeWait.UntilConnectedAsync(pipe, name, operation));
        Assert.Contains(name, error.Message);
        Assert.Contains("子の診断", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InheritedPipesStayOpenUntilOwnedChildIsReleased(bool sendRelease)
    {
        string pipeName = "fake-output-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var parent = Start(new Dictionary<string, string> { ["FAKE_ENCODER_HOLD_OUTPUT_PIPE"] = pipeName });
        Task<string> stderrEof = parent.StandardError.ReadToEndAsync();
        string stage = "親のREADY";
        try
        {
            var reader = new PreviewProtocolReader(parent.StandardOutput.BaseStream);
            Assert.IsType<PreviewReady>(await NextAsync(reader));
            stage = "親へのquit送信";
            await SendAsync(parent, new { cmd = "quit" });
            Task parentExit = parent.WaitForExitAsync();
            stage = "子の制御パイプへの接続";
            await PipeWait.UntilConnectedAsync(pipe, pipeName, parentExit);
            using var pipeReader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            stage = "子のPIDの受信";
            string? pid = await pipeReader.ReadLineAsync();
            Assert.True(int.TryParse(pid, out int childPid) && childPid > 0);
            stage = "親の終了";
            await parentExit;
            Assert.Equal(0, parent.ExitCode);

            // 親が終了してから確認行を要求し、子が継承した標準出力に書けることを確かめる。
            stage = "継承した標準出力の確認";
            await pipe.WriteAsync("check\n"u8.ToArray()).AsTask();
            Assert.Equal("held", await parent.StandardOutput.ReadLineAsync());

            if (sendRelease)
            {
                stage = "子へのrelease送信";
                await pipe.WriteAsync("release\n"u8.ToArray()).AsTask();
                // 子は応答を書き終えてから終了するため、継承したパイプのEOFを待つ前に応答を読む。
                stage = "子の終了コードの受信";
                Assert.Equal("0", await pipeReader.ReadLineAsync());
            }
            else
            {
                // 試験が途中で失敗して制御パイプを閉じた場合も、子は継承したパイプを手放す。
                pipe.Dispose();
            }

            stage = "継承した標準出力と標準エラーのEOF";
            Assert.Equal("", await parent.StandardOutput.ReadToEndAsync());
            Assert.Equal("", await stderrEof);
        }
        catch (Exception error)
        {
            // 制御パイプを先に閉じ、継承パイプの診断を読んでから元の失敗と一緒に報告する。
            pipe.Dispose();
            string stderr;
            try
            {
                await StopAsync(parent);
                stderr = await stderrEof;
            }
            catch (Exception cleanup)
            {
                throw new AggregateException($"{stage}で失敗しました (制御パイプ: {pipeName})。", error, cleanup);
            }
            throw new InvalidOperationException(
                $"{stage}で失敗しました (制御パイプ: {pipeName})。標準エラー: {stderr}", error);
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
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
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
        await process.WaitForExitAsync();
    }

    private static async Task<PreviewEvent> NextAsync(PreviewProtocolReader reader)
        => await Task.Run(() => reader.ReadEvent() ?? throw new EndOfStreamException());

    private static async Task SendAsync(Process process, object command)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync();
    }

}

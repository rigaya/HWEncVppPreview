using System.Diagnostics;
using System.Text.Json;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class FakeStreamingTests
{
    private static readonly TimeSpan SafetyTimeout = TestTimeouts.Hang;

    [Fact]
    public async Task MissingFrameLimitStreamsUntilInputEndInOneShotMode()
    {
        var run = await ReadOneShotAsync("--seek", "0:00:00.100", "--timestamp-passthrough",
            "--fake-origin-seconds", "1000", "--fake-input-end", "0.300");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(6, run.Frames.Count);
        Assert.Equal(1000.1, run.Frames[0].Xts!.Value, 7);
        Assert.True(run.Frames.All(frame => frame.Xts < 1000.3));
    }

    [Fact]
    public async Task PassthroughUsesTheSamePixelsAndClockAcrossSeeks()
    {
        var first = await ReadOneShotAsync("--frames", "6", "--seek", "0:00:01.000",
            "--timestamp-passthrough", "--fake-origin-seconds", "250");
        var next = await ReadOneShotAsync("--frames", "6", "--seek", "0:00:01.033333333",
            "--timestamp-passthrough", "--fake-origin-seconds", "250");
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, next.ExitCode);
        Assert.Equal(251, first.Frames[0].Xts!.Value, 7);
        for (int i = 0; i < 5; ++i)
        {
            Assert.Equal(first.Frames[i + 1].Data, next.Frames[i].Data);
            Assert.Equal(first.Frames[i + 1].Xts, next.Frames[i].Xts);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("double")]
    [InlineData("film")]
    [InlineData("vfr")]
    public async Task IgnoringPassthroughChangesOnlyTheClock(string? rate)
    {
        var arguments = new List<string>
        {
            "--frames", "12", "--seek", "0:00:00.700", "--timestamp-passthrough",
            "--fake-origin-seconds", "1000", "--fake-no-xdur",
        };
        if (rate != null) arguments.AddRange(["--fake-output-rate", rate]);
        var absolute = await ReadOneShotAsync(arguments.ToArray());
        arguments.Add("--fake-zero-origin");
        var relative = await ReadOneShotAsync(arguments.ToArray());
        Assert.Equal(0, absolute.ExitCode);
        Assert.Equal(0, relative.ExitCode);
        Assert.Equal(absolute.Frames.Count, relative.Frames.Count);
        Assert.True(absolute.Frames.Count > 1);
        Assert.Equal(0, relative.Frames[0].Xts);
        double first = absolute.Frames[0].Xts!.Value;
        for (int i = 0; i < absolute.Frames.Count; ++i)
        {
            Assert.Equal(absolute.Frames[i].Data, relative.Frames[i].Data);
            Assert.Equal(absolute.Frames[i].Xts!.Value - first, relative.Frames[i].Xts!.Value, 7);
            Assert.Null(relative.Frames[i].Xdur);
        }
    }

    [Fact]
    public async Task RelativeLegacyRequestsKeepTheirOriginalPixelNumbering()
    {
        var head = await ReadOneShotAsync("--frames", "3");
        var later = await ReadOneShotAsync("--frames", "3", "--seek", "0:00:10.000");
        Assert.Equal(3, head.Frames.Count);
        Assert.Equal(3, later.Frames.Count);
        Assert.Equal(0, later.Frames[0].Xts);
        for (int i = 0; i < head.Frames.Count; ++i)
            Assert.Equal(head.Frames[i].Data, later.Frames[i].Data);
    }

    [Fact]
    public async Task ContinuousServerCanCancelAfterTheReaderStopsAndRecover()
    {
        await using var child = Start("--preview-server");
        var reader = new PreviewProtocolReader(child.Process.StandardOutput.BaseStream);
        Assert.IsType<PreviewReady>(await NextAsync(reader));
        await SendAsync(child.Process, new { cmd = "run", id = 1,
            args = new[] { "--timestamp-passthrough", "--fake-origin-seconds", "1000", "--output-res", "1024x1024" } });
        Y4mFrame? first = null;
        while (first == null)
        {
            var item = await NextAsync(reader);
            Assert.False(item is PreviewEnd);
            if (item is PreviewFrame frame) first = frame.Frame;
        }
        Assert.Equal(1000, first.Xts);
        // 読み手を停止したまま中断を送り、その後にだけ排出を再開する。
        await SendAsync(child.Process, new { cmd = "cancel", id = 1 });
        PreviewEnd end = await ReadEndAsync(reader);
        Assert.Equal(-12, end.Rc);

        await SendAsync(child.Process, new { cmd = "run", id = 2,
            args = new[] { "--frames", "2", "--timestamp-passthrough", "--fake-origin-seconds", "1000", "--seek", "0:00:01.000" } });
        var recovered = new List<Y4mFrame>();
        while (true)
        {
            var item = await NextAsync(reader);
            if (item is PreviewFrame frame) recovered.Add(frame.Frame);
            if (item is PreviewEnd completion)
            {
                Assert.Equal(2, completion.Id);
                Assert.Equal(0, completion.Rc);
                break;
            }
        }
        Assert.Equal(2, recovered.Count);
        Assert.Equal(1001, recovered[0].Xts);
        Assert.Equal(first.Header.ExtTags["XPID"], recovered[0].Header.ExtTags["XPID"]);
        await SendAsync(child.Process, new { cmd = "quit" });
        await child.Process.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(0, child.Process.ExitCode);
    }

    [Fact]
    public async Task CapabilitiesAreAdvertisedOnlyWhenExplicitlyEnabled()
    {
        await using var legacy = Start(["--help"], capabilities: false);
        string oldHelp = await legacy.Process.StandardOutput.ReadToEndAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        await legacy.Process.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.DoesNotContain("--timestamp-passthrough", oldHelp);
        await using var modern = Start(["--help"], capabilities: true);
        string help = await modern.Process.StandardOutput.ReadToEndAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        await modern.Process.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Contains("--timestamp-passthrough", help);
        Assert.Contains("--opencl-task-threads", help);
    }

    private static async Task<PreviewEnd> ReadEndAsync(PreviewProtocolReader reader)
    {
        while (true)
            if (await NextAsync(reader) is PreviewEnd end) return end;
    }

    private static async Task<PreviewEvent> NextAsync(PreviewProtocolReader reader)
        => await Task.Run(() => reader.ReadEvent() ?? throw new EndOfStreamException()).WaitWithDiagnosticsAsync(SafetyTimeout);

    private static async Task SendAsync(Process process, object command)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<(int ExitCode, IReadOnlyList<Y4mFrame> Frames)> ReadOneShotAsync(params string[] arguments)
    {
        await using var child = Start(arguments);
        var read = Task.Run<IReadOnlyList<Y4mFrame>>(() =>
        {
            using var stream = new BufferedStream(child.Process.StandardOutput.BaseStream, 1 << 16);
            var frames = new List<Y4mFrame>();
            var reader = new Y4mReader(stream);
            while (reader.ReadFrame() is { } frame) frames.Add(frame);
            return frames;
        });
        await Task.WhenAll(read, child.Process.WaitForExitAsync()).WaitWithDiagnosticsAsync(SafetyTimeout);
        return (child.Process.ExitCode, await read);
    }

    private static ChildProcess Start(params string[] arguments) => Start(arguments, false);

    private static ChildProcess Start(string[] arguments, bool capabilities)
    {
        var start = new ProcessStartInfo(BackendTests.FakeEncoderPath())
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment.Remove("FAKE_ENCODER_HELP_FILE");
        start.Environment["FAKE_ENCODER_PREVIEW_CAPABILITIES"] = capabilities ? "1" : "0";
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return new ChildProcess(Process.Start(start) ?? throw new InvalidOperationException("偽エンコーダを起動できませんでした"));
    }

    private sealed class ChildProcess(Process process) : IAsyncDisposable
    {
        private readonly Task<string> _stderr = process.StandardError.ReadToEndAsync();
        public Process Process { get; } = process;

        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            await _stderr.WaitWithDiagnosticsAsync(SafetyTimeout);
            Process.Dispose();
        }
    }
}

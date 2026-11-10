using System.Diagnostics;
using System.Text.Json;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class FakeClockDiscontinuityTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false, "--fake-clock-backstep-at")]
    [InlineData(true, "--fake-clock-backstep-at")]
    [InlineData(false, "--fake-clock-jump-at")]
    [InlineData(true, "--fake-clock-jump-at")]
    public async Task ClockFaultAffectsOnlyPassthroughAndRecoveryKeepsPixels(bool server, string fault)
    {
        string[] common = ["--frames", "6", "--seek", "0:00:01.000", "--fake-origin-seconds", "1000"];
        string[][] requests =
        [
            [.. common, "--timestamp-passthrough"],
            [.. common, "--timestamp-passthrough", fault, "4"],
            [.. common, fault, "4"],
        ];
        var runs = server ? await ReadServerAsync(requests) : await ReadOneShotsAsync(requests);
        Assert.All(runs, frames => Assert.Equal(6, frames.Count));
        for (int i = 0; i < 6; ++i)
        {
            var baseline = runs[0][i];
            var broken = runs[1][i];
            var recovered = runs[2][i];
            Assert.Equal(baseline.Data, broken.Data);
            Assert.Equal(baseline.Data, recovered.Data);
            Assert.Equal(i / 30.0, recovered.Xts!.Value, 7);
            double expected = baseline.Xts!.Value;
            if (fault == "--fake-clock-jump-at" && i >= 3) expected += 10;
            if (fault == "--fake-clock-backstep-at" && i == 3)
                expected = runs[0][2].Xts!.Value - 2 * baseline.Xdur!.Value;
            Assert.Equal(expected, broken.Xts!.Value, 7);
        }
    }

    [Fact]
    public async Task ExistingOriginFlagCanRepresentNearWrapWithoutClockFaults()
    {
        var runs = await ReadOneShotsAsync(
            [["--frames", "6", "--timestamp-passthrough", "--fake-origin-seconds", "95443.7"]]);
        Assert.Equal(95443.7, runs[0][0].Xts!.Value, 7);
        for (int i = 1; i < runs[0].Count; ++i)
            Assert.True(runs[0][i].Xts > runs[0][i - 1].Xts);
    }

    private static async Task<List<List<Y4mFrame>>> ReadOneShotsAsync(string[][] requests)
    {
        var result = new List<List<Y4mFrame>>();
        foreach (string[] arguments in requests)
        {
            await using var child = Start(arguments);
            var reading = Task.Run(() =>
            {
                var reader = new Y4mReader(child.Process.StandardOutput.BaseStream);
                var frames = new List<Y4mFrame>();
                while (reader.ReadFrame() is { } frame) frames.Add(frame);
                return frames;
            });
            await Task.WhenAll(reading, child.Process.WaitForExitAsync()).WaitAsync(SafetyTimeout);
            Assert.Equal(0, child.Process.ExitCode);
            result.Add(await reading);
        }
        return result;
    }

    private static async Task<List<List<Y4mFrame>>> ReadServerAsync(string[][] requests)
    {
        await using var child = Start(["--preview-server"]);
        var reader = new PreviewProtocolReader(child.Process.StandardOutput.BaseStream);
        Assert.IsType<PreviewReady>(await NextAsync(reader));
        var result = new List<List<Y4mFrame>>();
        for (int i = 0; i < requests.Length; ++i)
        {
            long id = i + 1;
            await SendAsync(child.Process, new { cmd = "run", id, args = requests[i] });
            var frames = new List<Y4mFrame>();
            while (true)
            {
                var item = await NextAsync(reader);
                if (item is PreviewFrame frame) frames.Add(frame.Frame);
                if (item is PreviewEnd end)
                {
                    Assert.Equal(id, end.Id);
                    Assert.Equal(0, end.Rc);
                    break;
                }
            }
            result.Add(frames);
        }
        await SendAsync(child.Process, new { cmd = "quit" });
        await child.Process.WaitForExitAsync().WaitAsync(SafetyTimeout);
        Assert.Equal(0, child.Process.ExitCode);
        return result;
    }

    private static async Task<PreviewEvent> NextAsync(PreviewProtocolReader reader)
        => await Task.Run(() => reader.ReadEvent() ?? throw new EndOfStreamException()).WaitAsync(SafetyTimeout);

    private static async Task SendAsync(Process process, object command)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync();
    }

    private static ChildProcess Start(string[] arguments)
    {
        var start = new ProcessStartInfo(BackendTests.FakeEncoderPath())
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
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
            await Process.WaitForExitAsync().WaitAsync(SafetyTimeout);
            await _stderr.WaitAsync(SafetyTimeout);
            Process.Dispose();
        }
    }
}

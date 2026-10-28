using System.Diagnostics;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.Tests;

public sealed class InputProbeTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ReadsInputFormatWithFrameLimitAndCachesByArgumentValues()
    {
        var commands = new List<string[]>();
        await using var probe = new InputProbe(configureStartInfo: info => commands.Add(info.ArgumentList.ToArray()));
        InputInfo first = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts", new[] { "--avhw" }).WaitAsync(SafetyTimeout);
        InputInfo cached = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts", new List<string> { "--avhw" }).WaitAsync(SafetyTimeout);
        Assert.Same(first, cached);
        Assert.Equal(64, first.Width);
        Assert.Equal(36, first.Height);
        Assert.Equal(30, first.Fps);
        Assert.Equal(30, first.FpsNum);
        Assert.Equal(1, first.FpsDen);
        Assert.Equal(1, first.SarNum);
        Assert.Equal(1, first.SarDen);
        Assert.Equal(Y4mChroma.C420, first.Chroma);
        Assert.Equal(8, first.BitDepth);
        Assert.Equal("420jpeg", first.ColorSpaceTag);
        var command = Assert.Single(commands);
        Assert.Equal(new[] { "--avhw", "-i", "input.ts", "--frames", "300", "-c", "raw", "--y4m-timestamp", "-o", "-" }, command);
    }

    [Fact]
    public async Task CacheKeyIncludesEncoderInputAndAdditionalArgumentsAndCopiesLists()
    {
        int starts = 0;
        await using var probe = new InputProbe(configureStartInfo: _ => ++starts);
        string encoder = BackendTests.FakeEncoderPath();
        var arguments = new List<string> { "--avhw" };
        await probe.ProbeAsync(encoder, "first.ts", arguments).WaitAsync(SafetyTimeout);
        arguments[0] = "--avsw";
        await probe.ProbeAsync(encoder, "first.ts", new[] { "--avhw" }).WaitAsync(SafetyTimeout);
        Assert.Equal(1, starts);
        await probe.ProbeAsync(encoder, "first.ts", arguments).WaitAsync(SafetyTimeout);
        await probe.ProbeAsync(encoder, "other.ts", arguments).WaitAsync(SafetyTimeout);
        string alternate = Path.GetDirectoryName(encoder) + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar + Path.GetFileName(encoder);
        await probe.ProbeAsync(alternate, "other.ts", arguments).WaitAsync(SafetyTimeout);
        Assert.Equal(4, starts);
    }

    [Fact]
    public async Task ConcurrentSameRequestsShareSuccessfulCacheEntry()
    {
        int starts = 0;
        await using var probe = new InputProbe(configureStartInfo: _ => ++starts);
        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts"))).WaitAsync(SafetyTimeout);
        Assert.Equal(1, starts);
        Assert.All(results, item => Assert.Same(results[0], item));
    }

    [Fact]
    public async Task EncoderErrorContainsExitCodeAndDiagnosticsAndIsNotCached()
    {
        int starts = 0;
        await using var probe = new InputProbe(configureStartInfo: info =>
        {
            ++starts;
            // フレームを出す前の失敗を再現する。最初のフレーム取得後は自己中断が成功扱いになるため。
            int index = info.ArgumentList.IndexOf("--frames");
            info.ArgumentList[index + 1] = "0";
        });
        for (int i = 0; i < 2; ++i)
        {
            var error = await Assert.ThrowsAsync<InputProbeException>(() =>
                probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts", new[] { "--fake-rc", "23" }).WaitAsync(SafetyTimeout));
            Assert.Equal(23, error.ExitCode);
            Assert.Contains("FAKE requested rc=23", error.StandardError);
        }
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task FirstFrameStopsAndReapsProcessWithoutWaitingForTheLimit()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"probe-first-frame-{Guid.NewGuid():N}.txt");
        await using var probe = new InputProbe(configureStartInfo: info => info.Environment["FAKE_ENCODER_PID_FILE"] = pidFile);
        try
        {
            // 2枚目以降は無応答でも、最初の1枚だけで検査を完了してプロセスを回収する。
            InputInfo input = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts",
                new[] { "--fake-hang-after", "1" }).WaitAsync(SafetyTimeout);
            Assert.Equal(30, input.Fps);
            int pid = await ReadPidAsync(pidFile);
            AssertStopped(pid);
        }
        finally { File.Delete(pidFile); }
    }

    [Fact]
    public async Task FirstFrameMakesLaterEncoderExitCodeIrrelevant()
    {
        await using var probe = new InputProbe();
        InputInfo input = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts",
            new[] { "--fake-rc", "23" }).WaitAsync(SafetyTimeout);
        Assert.Equal(30, input.Fps);
    }

    [Fact]
    public async Task NoFrameCannotPopulateTheCacheEvenWithSuccessfulExit()
    {
        int starts = 0;
        await using var probe = new InputProbe(configureStartInfo: info =>
        {
            ++starts;
            int index = info.ArgumentList.IndexOf("--frames");
            info.ArgumentList[index + 1] = "0";
        });
        for (int i = 0; i < 2; ++i)
        {
            var error = await Assert.ThrowsAsync<InputProbeException>(() =>
                probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts").WaitAsync(SafetyTimeout));
            Assert.Equal(-1, error.ExitCode);
        }
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task TimeoutUsesInjectedClockAndReapsProcess()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"probe-timeout-{Guid.NewGuid():N}.txt");
        string readyFile = Path.Combine(Path.GetTempPath(), $"probe-log-ready-{Guid.NewGuid():N}.txt");
        var clock = new FakeTimeProvider();
        await using var probe = new InputProbe(clock, configureStartInfo: info =>
        {
            info.Environment["FAKE_ENCODER_PID_FILE"] = pidFile;
            info.Environment["FAKE_ENCODER_LOG_READY_FILE"] = readyFile;
        });
        try
        {
            Task<InputInfo> run = probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts", new[] { "--fake-hang-after", "0" });
            int pid = await ReadPidAsync(pidFile);
            await WaitForFileAsync(readyFile);
            clock.Advance(TimeSpan.FromSeconds(30));
            var error = await Assert.ThrowsAsync<InputProbeTimeoutException>(() => run.WaitAsync(SafetyTimeout));
            Assert.Contains($"FAKEPID {pid}", error.StandardError);
            AssertStopped(pid);
            InputInfo recovered = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts").WaitAsync(SafetyTimeout);
            Assert.Equal(30, recovered.Fps);
        }
        finally { File.Delete(pidFile); File.Delete(readyFile); }
    }

    [Fact]
    public async Task CancellationOfWaitingRequestDoesNotKillOtherProbe()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"probe-cancel-{Guid.NewGuid():N}.txt");
        await using var probe = new InputProbe(configureStartInfo: info => info.Environment["FAKE_ENCODER_PID_FILE"] = pidFile);
        using var activeCancellation = new CancellationTokenSource();
        using var waitingCancellation = new CancellationTokenSource();
        try
        {
            Task<InputInfo> active = probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts", new[] { "--fake-hang-after", "0" }, activeCancellation.Token);
            int pid = await ReadPidAsync(pidFile);
            Task<InputInfo> waiting = probe.ProbeAsync(BackendTests.FakeEncoderPath(), "next.ts", cancellationToken: waitingCancellation.Token);
            waitingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(SafetyTimeout));
            Assert.False(active.IsCompleted);
            activeCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(SafetyTimeout));
            AssertStopped(pid);
        }
        finally { File.Delete(pidFile); }
    }

    [Fact]
    public async Task DisposeCancelsActiveProbeReapsProcessAndRejectsNewRequests()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"probe-dispose-{Guid.NewGuid():N}.txt");
        var probe = new InputProbe(configureStartInfo: info => info.Environment["FAKE_ENCODER_PID_FILE"] = pidFile);
        try
        {
            Task<InputInfo> run = probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts", new[] { "--fake-hang-after", "0" });
            int pid = await ReadPidAsync(pidFile);
            await probe.DisposeAsync().AsTask().WaitAsync(SafetyTimeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(SafetyTimeout));
            AssertStopped(pid);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => probe.ProbeAsync(BackendTests.FakeEncoderPath(), "in.ts"));
            await probe.DisposeAsync();
        }
        finally
        {
            await probe.DisposeAsync();
            File.Delete(pidFile);
        }
    }

    [Fact]
    public void RejectsNonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputProbe(timeout: TimeSpan.Zero));
    }

    private static async Task WaitForFileAsync(string path)
    {
        async Task WaitAsync()
        {
            while (!File.Exists(path)) await Task.Yield();
        }
        await WaitAsync().WaitAsync(SafetyTimeout);
    }

    private static async Task<int> ReadPidAsync(string path)
    {
        async Task<int> ReadAsync()
        {
            while (true)
            {
                if (File.Exists(path) && int.TryParse(File.ReadLines(path).FirstOrDefault(), out int pid)) return pid;
                await Task.Yield();
            }
        }
        return await ReadAsync().WaitAsync(SafetyTimeout);
    }

    private static void AssertStopped(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, $"検査プロセス{pid}が残っています");
        }
        catch (ArgumentException) { /* 回収済み。 */ }
    }
}

using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class StreamingCommandProbeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AdditionalFlagsAreOnlyAddedWhenHelpAdvertisesThem(bool timestamps, bool threads)
    {
        var help = new HelpIndex((timestamps ? "  --timestamp-passthrough  pass\n" : "")
            + (threads ? "  --opencl-task-threads <int>  threads\n" : ""));
        var request = Request() with { Capabilities = CommandBuilder.CapabilitiesFromHelp(help) };
        foreach (var args in new[] { CommandBuilder.BuildOneShot(request), CommandBuilder.BuildServer(request) })
        {
            Assert.Equal(timestamps, args.Contains("--timestamp-passthrough"));
            Assert.Equal(threads, args.Contains("--opencl-task-threads"));
            if (threads) Assert.Equal("0", args[Array.IndexOf(args, "--opencl-task-threads") + 1]);
            Assert.Contains("--frames", args);
        }
    }

    [Fact]
    public void ContinuousOmitsFrameLimitAndPhysicalSeekCanBeExplicitlyZero()
    {
        var request = Request() with { Continuous = true, SeekSecondsOverride = 0 };
        Assert.DoesNotContain("--frames", CommandBuilder.BuildOneShot(request));
        Assert.DoesNotContain("--frames", CommandBuilder.BuildServer(request));
        Assert.DoesNotContain("--seek", CommandBuilder.BuildOneShot(request));
        Assert.Null(CommandBuilder.Plan(request).SeekSeconds);
        Assert.Equal(10, CommandBuilder.Plan(request).ActualPreMargin);
        Assert.Equal(8, CommandBuilder.Plan(Request()).SeekSeconds);
    }

    [Fact]
    public async Task ProbeObtainsUnfilteredOriginAtInputStartAndSeparatesLegacyCache()
    {
        var commands = new List<string[]>();
        await using var probe = new InputProbe(configureStartInfo: info => commands.Add(info.ArgumentList.ToArray()));
        string[] arguments = ["--fake-origin-seconds", "1234.5"];
        var capabilities = new PreviewCapabilities(true, true);
        var absolute = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts", arguments,
            capabilities: capabilities).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1234.5, absolute.TimestampOriginSeconds);
        Assert.DoesNotContain("--seek", commands[0]);
        Assert.Contains("--timestamp-passthrough", commands[0]);
        Assert.Contains("--opencl-task-threads", commands[0]);
        Assert.DoesNotContain("--vpp-nlmeans", commands[0]);
        Assert.Same(absolute, await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts", arguments,
            capabilities: capabilities));
        var legacy = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "input.ts", arguments).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(legacy.TimestampOriginSeconds);
        Assert.Equal(2, commands.Count);
        Assert.DoesNotContain("--timestamp-passthrough", commands[1]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1000, true)]
    [InlineData(95443.5, false)]
    [InlineData(95383.3, true)]
    public async Task ProbeKeepsNumericOriginButSeparatesWrapConfidence(double origin, bool trusted)
    {
        await using var probe = new InputProbe();
        var info = await probe.ProbeAsync(BackendTests.FakeEncoderPath(), "origin.ts",
            ["--fake-origin-seconds", origin.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            capabilities: new(true, true)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(origin, info.TimestampOriginSeconds!.Value, 6);
        Assert.Equal(trusted, info.TimestampOriginTrusted);
    }

    [Fact]
    public async Task ProbeDisposeBoundsUnresponsiveBackendAndPendingPreparationGate()
    {
        var clock = new ProbeShutdownClock();
        var backend = new UnresponsiveProbeBackend();
        var probe = new InputProbe(backend, clock);
        Task<InputInfo> preparation = probe.ProbeAsync("cpu-stub", "input.ts");
        try
        {
            await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Task dispose = probe.DisposeAsync().AsTask();
            await backend.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await backend.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await clock.TwoDeadlinesArmed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            clock.Advance(TimeSpan.FromSeconds(5));
            await dispose.WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await preparation.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.False(backend.Running.IsCompleted);
        }
        finally { backend.Release(); await probe.DisposeAsync(); }
    }

    private sealed class ProbeShutdownClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        private int _deadlines;
        public TaskCompletionSource TwoDeadlinesArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public void Advance(TimeSpan delta) => _clock.Advance(delta);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(5) && Interlocked.Increment(ref _deadlines) >= 2) TwoDeadlinesArmed.TrySetResult();
            return timer;
        }
    }

    private sealed class UnresponsiveProbeBackend : IPreviewBackend
    {
        private readonly TaskCompletionSource<PreviewRunResult> _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewRunResult> Running => _running.Task;
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        { Started.TrySetResult(); return _running.Task; }
        public ValueTask AbortAsync() { Aborted.TrySetResult(); return new(_cleanup.Task); }
        public ValueTask DisposeAsync() { Disposed.TrySetResult(); return new(_cleanup.Task); }
        public void Release() { _running.TrySetResult(new(-12, "", true)); _cleanup.TrySetResult(); }
    }

    private static PreviewRequest Request() => new()
    {
        EncoderPath = "encoder", InputFile = "input.ts", TargetSeconds = 10, Fps = 30,
    };
}

using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class BeforeEarlySelectionTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 2, FpsDen = 1 };
    private static PreviewRequest Request => new()
    {
        EncoderPath = "fake", InputFile = "input", Fps = 2, TargetSeconds = 1,
        TimestampOriginSeconds = 1000, Capabilities = new(TimestampPassthrough: true), DependsOnHistory = false,
    };
    private static Y4mFrame Frame(double? seconds, double? duration = .5)
        => new(Header, seconds is double time ? 1000 + time : null, duration, new byte[6]);

    [Fact]
    public async Task 単調な同一durationはEND前に適格な一枚を通知する()
    {
        var backend = new GatedBackend(new[] { Frame(0), Frame(.5), Frame(1), Frame(1.5) });
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        var selected = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<PreviewCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.FrameReady += value => selected.TrySetResult(value);
        controller.Completed += value => completed.TrySetResult(value);
        try
        {
            controller.Submit(Request, background: true, bypassDebounce: true);
            var early = await selected.Task.WaitAsync(Safety);
            Assert.True(early.CanDisplayBeforeEnd);
            Assert.False(early.TargetNotReached);
            Assert.Equal(1001, early.Frame.Xts);
            Assert.True(early.Segment!.Clock.TrustedAbsolute);
            Assert.Single(early.Segment.Frames);
            Assert.False(completed.Task.IsCompleted);
            backend.Release.TrySetResult();
            await controller.WaitForIdleAsync().WaitAsync(Safety);
            Assert.Null((await completed.Task.WaitAsync(Safety)).EarlyDisplayFallbackReason);
        }
        finally { backend.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("cadence")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task 受信条件が崩れたprefixは早期表示へ昇格しない(string variation)
    {
        Y4mFrame[] frames = variation switch
        {
            "duration" => new[] { Frame(0), Frame(.5, .25), Frame(1), Frame(1.5) },
            "cadence" => new[] { Frame(0), Frame(.4), Frame(1), Frame(1.6) },
            "missing" => new[] { Frame(null), Frame(null), Frame(null), Frame(null) },
            _ => new[] { Frame(0), Frame(.5), Frame(.5), Frame(1.5) },
        };
        var backend = new GatedBackend(frames);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        var images = new System.Collections.Concurrent.ConcurrentQueue<PreviewFrameResult>();
        controller.FrameReady += images.Enqueue;
        try
        {
            controller.Submit(Request, background: true, bypassDebounce: true);
            await backend.Reached.Task.WaitAsync(Safety);
            Assert.DoesNotContain(images, image => image.CanDisplayBeforeEnd);
            backend.Release.TrySetResult();
            await controller.WaitForIdleAsync().WaitAsync(Safety);
            Assert.DoesNotContain(images, image => image.CanDisplayBeforeEnd);
        }
        finally { backend.Release.TrySetResult(); }
    }

    [Fact]
    public async Task 早期通知後の長durationは最終列と条件崩れの理由を渡す()
    {
        var tail = Frame(2, 3);
        var backend = new GatedBackend(new[] { Frame(0), Frame(.5), Frame(1), Frame(1.5) }, tail);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        var selected = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        PreviewCompletion? completed = null;
        controller.FrameReady += value => selected.TrySetResult(value);
        controller.Completed += value => completed = value;
        try
        {
            controller.Submit(Request, background: true, bypassDebounce: true);
            Assert.True((await selected.Task.WaitAsync(Safety)).CanDisplayBeforeEnd);
            backend.Release.TrySetResult();
            await controller.WaitForIdleAsync().WaitAsync(Safety);
            Assert.NotNull(completed);
            Assert.Equal("受信した表示時間が一致しない", completed.EarlyDisplayFallbackReason);
            Assert.Same(tail, completed.Frames[FrameSelector.Select(completed.Segment!, 1, 2)]);
        }
        finally { backend.Release.TrySetResult(); }
    }

    [Fact]
    public async Task 実Fakeのgateを解放する前に早期表示適格性が届く()
    {
        const int GateAfterFrame = 32;
        string gate = Path.Combine(Path.GetTempPath(), "before-early-" + Guid.NewGuid().ToString("N"));
        await using var backend = new ServerBackend();
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        var selected = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<PreviewCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.FrameReady += value => selected.TrySetResult(value);
        controller.Completed += value => completed.TrySetResult(value);
        try
        {
            controller.Submit(Request with { EncoderPath = BackendTests.FakeEncoderPath(), Fps = 30,
                InputArgs = new[] { "--fake-gate", gate, "--fake-gate-after", GateAfterFrame.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
                background: true, bypassDebounce: true);
            var frame = await selected.Task.WaitAsync(Safety);
            Assert.True(frame.CanDisplayBeforeEnd);
            Assert.Equal(1001, frame.Frame.Xts!.Value, 6);
            Assert.True(int.TryParse(frame.Frame.Header.ExtTags["XPID"], out _));
            Assert.False(completed.Task.IsCompleted);
            await File.WriteAllTextAsync(gate, "");
            await controller.WaitForIdleAsync().WaitAsync(Safety);
            Assert.Equal(0, (await completed.Task.WaitAsync(Safety)).Result.ExitCode);
        }
        finally
        {
            // 失敗時もgateを開いてから子を回収し、次の実行へ同期ファイルを残さない。
            await File.WriteAllTextAsync(gate, "");
            await controller.DisposeAsync();
            File.Delete(gate);
        }
    }

    [Theory]
    [InlineData(.0099, true, false)]
    [InlineData(.0099, true, true)]
    [InlineData(.0101, false, false)]
    [InlineData(.0101, false, true)]
    public async Task 間隔の一パーセント境界はXdur有無で同じ(double ratio, bool eligible, bool noDuration)
    {
        const double BaseDuration = .5;
        double? duration = noDuration ? null : BaseDuration;
        double offset = BaseDuration * ratio;
        var frames = new[] { Frame(0, duration), Frame(.5 + offset, duration), Frame(1, duration), Frame(1.5 + offset, duration) };
        await AssertEligibilityAsync(frames, Request, eligible, frames[2]);
    }

    [Theory]
    [InlineData(.0099, true)]
    [InlineData(.0101, false)]
    [InlineData(.5, false)]
    public async Task durationの幅も先頭値の一パーセントで固定する(double ratio, bool eligible)
    {
        const double BaseDuration = .5;
        double changedDuration = BaseDuration * (1 + ratio);
        // 時刻間隔は当該durationに一致させ、duration比較そのものの境界を分離する。
        var frames = new[] { Frame(0), Frame(changedDuration, changedDuration),
            Frame(changedDuration + BaseDuration), Frame(changedDuration + 2 * BaseDuration) };
        await AssertEligibilityAsync(frames, Request, eligible, frames[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 実TSの五十九点九四fpsの時刻揺れはEND前に許可する(bool noDuration)
    {
        const int ExpectedIndex = 12;
        const double MicrosecondsPerSecond = 1_000_000;
        var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 60000, FpsDen = 1001 };
        // 実機traceの先頭20枚を原点からのµsで固定し、旧12µs条件で落ちた箇所も通す。
        int[] observed = { 0, 16677, 33366, 50044, 66733, 83411, 100100, 116789, 133477, 150144,
            166844, 183511, 200200, 216889, 233566, 250255, 266944, 283611, 300311, 316977 };
        var frames = observed.Select(time => new Y4mFrame(header, 1000 + time / MicrosecondsPerSecond,
            noDuration ? null : 1 / header.Fps, new byte[6])).ToArray();
        await AssertEligibilityAsync(frames, Request with { Fps = header.Fps, TargetSeconds = .2 }, true, frames[ExpectedIndex]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 早期通知後の一パーセント超過も最終列に理由を残す(bool durationChanges)
    {
        const double OverBoundaryRatio = .0101;
        const double BaseDuration = .5;
        var tail = durationChanges ? Frame(2.25, .75) : Frame(2 + BaseDuration * OverBoundaryRatio);
        var backend = new GatedBackend(new[] { Frame(0), Frame(.5), Frame(1), Frame(1.5) }, tail);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        var selected = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<PreviewCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.FrameReady += value => selected.TrySetResult(value);
        controller.Completed += value => completed.TrySetResult(value);
        try
        {
            controller.Submit(Request, background: true, bypassDebounce: true);
            Assert.True((await selected.Task.WaitAsync(Safety)).CanDisplayBeforeEnd);
            Assert.False(completed.Task.IsCompleted);
            backend.Release.TrySetResult();
            await controller.WaitForIdleAsync().WaitAsync(Safety);
            var final = await completed.Task.WaitAsync(Safety);
            Assert.Equal(durationChanges ? "受信した表示時間が一致しない" : "受信時刻の間隔が一定でない", final.EarlyDisplayFallbackReason);
            Assert.Contains(tail, final.Frames);
            Assert.Equal(1001, final.Frames[FrameSelector.Select(final.Segment!, 1, 2)].Xts);
        }
        finally { backend.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(.0099, true, false)]
    [InlineData(.0099, true, true)]
    [InlineData(.0101, false, false)]
    [InlineData(.0101, false, true)]
    public async Task 実Fakeの間隔境界をENDgate前に判定する(double ratio, bool eligible, bool noDuration)
    {
        const int GateAfterFrame = 64;
        string gate = Path.Combine(Path.GetTempPath(), "before-jitter-" + Guid.NewGuid().ToString("N"));
        await using var backend = new ServerBackend();
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        var selected = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<PreviewCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.FrameReady += value => selected.TrySetResult(value);
        controller.Completed += value => completed.TrySetResult(value);
        var args = new List<string> { "--fake-gate", gate, "--fake-gate-after", GateAfterFrame.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--fake-output-rate", "double", "--fake-timestamp-jitter-ratio", ratio.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (noDuration) args.Add("--fake-no-xdur");
        try
        {
            controller.Submit(Request with { EncoderPath = BackendTests.FakeEncoderPath(), Fps = 30, InputArgs = args },
                background: true, bypassDebounce: true);
            var frame = await selected.Task.WaitAsync(Safety);
            Assert.Equal(eligible, frame.CanDisplayBeforeEnd);
            Assert.Equal(60, frame.Frame.Header.Fps);
            Assert.Equal(!noDuration, frame.Frame.Xdur.HasValue);
            Assert.Equal(1001, frame.Frame.Xts!.Value, 6);
            Assert.False(completed.Task.IsCompleted);
            await File.WriteAllTextAsync(gate, "");
            await controller.WaitForIdleAsync().WaitAsync(Safety);
            Assert.Equal(0, (await completed.Task.WaitAsync(Safety)).Result.ExitCode);
        }
        finally
        {
            await File.WriteAllTextAsync(gate, "");
            await controller.DisposeAsync();
            File.Delete(gate);
        }
    }

    private static async Task AssertEligibilityAsync(Y4mFrame[] frames, PreviewRequest request, bool eligible, Y4mFrame expected)
    {
        var backend = new GatedBackend(frames);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        var selected = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<PreviewCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.FrameReady += value => selected.TrySetResult(value);
        controller.Completed += value => completed.TrySetResult(value);
        try
        {
            controller.Submit(request, background: true, bypassDebounce: true);
            var frame = await selected.Task.WaitAsync(Safety);
            Assert.Equal(eligible, frame.CanDisplayBeforeEnd);
            Assert.Same(expected, frame.Frame);
            Assert.False(frame.TargetNotReached);
            Assert.False(completed.Task.IsCompleted);
            backend.Release.TrySetResult();
            await controller.WaitForIdleAsync().WaitAsync(Safety);
            var final = await completed.Task.WaitAsync(Safety);
            Assert.Equal(0, final.Result.ExitCode);
            Assert.Same(expected, final.Frames[FrameSelector.Select(final.Segment!, request.TargetSeconds, request.Fps)]);
        }
        finally { backend.Release.TrySetResult(); }
    }

    private sealed class GatedBackend(Y4mFrame[] frames, Y4mFrame? tail = null) : IPreviewBackend
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            sink.OnHeader(frames.FirstOrDefault()?.Header ?? Header);
            foreach (var frame in frames) sink.OnFrame(frame);
            Reached.TrySetResult();
            await Release.Task.WaitAsync(token);
            if (tail != null) sink.OnFrame(tail);
            return new(0, "");
        }
        public ValueTask AbortAsync() { Release.TrySetResult(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Release.TrySetResult(); return ValueTask.CompletedTask; }
    }
}

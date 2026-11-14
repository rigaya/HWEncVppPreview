using System.Threading.Channels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class StreamingControllerTests
{
    private static PreviewRequest Request(double target = 10) => new()
    {
        EncoderPath = "encoder", InputFile = "input", Fps = 2, TargetSeconds = target,
        // 従来の2秒暖機を検証するfixtureなので、時間方向の処理を明示する。
        VppArgs = new[] { "--vpp-kfm" },
        PreMarginSeconds = 2, PostMarginSeconds = 0.5, Continuous = true,
        Capabilities = new(true, true), TimestampOriginSeconds = 1000,
    };

    [Fact]
    public async Task ConfirmedDemandPublishesFollowingFrameBeforeReadPause()
    {
        var backend = new StreamingBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var updates = new List<PreviewSegmentUpdate>();
        var frames = new List<PreviewFrameResult>();
        controller.SegmentUpdated += updates.Add;
        controller.FrameReady += frames.Add;
        controller.Submit(Request());
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        // 中心だけの確定では隣を選べないため、次の1枚もpause前の更新に含まれる必要がある。
        Assert.Contains(updates, update => !update.IsReadPaused && update.DemandSatisfied
            && update.Segment.Frames.Any(frame => frame.Xts == 1010.5));
        // 区間更新を増やしても、先読みの画像をFrameReadyとして余分に表示しない。
        Assert.Equal(1010, Assert.Single(frames).Frame.Xts);
    }

    [Fact]
    public async Task ForwardDemandSharesPhysicalStreamAndDoesNotPublishPrefetch()
    {
        var backend = new StreamingBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var frames = new List<PreviewFrameResult>();
        controller.FrameReady += frames.Add;
        controller.Submit(Request());
        var first = await backend.Pauses.Reader.ReadAsync();
        await controller.WaitForCurrentDemandAsync();
        Assert.Equal(1010, Assert.Single(frames).Frame.Xts);
        Assert.Equal(1012, first.LastTimestamp);
        // 順方向は従来どおり暖機込み3秒。逆方向の表示用窓拡張を適用しない。
        Assert.Equal(7, CommandBuilder.Plan(Assert.Single(backend.Requests)).SeekSeconds);
        Assert.Equal(2, backend.Requests[0].PreMarginSeconds);
        controller.Submit(Request(11), navigation: true);
        var second = await backend.Pauses.Reader.ReadAsync();
        await controller.WaitForCurrentDemandAsync();
        Assert.Equal(1, backend.Calls);
        Assert.Equal(1011, frames[^1].Frame.Xts);
        Assert.Equal(1013, second.LastTimestamp);
        int readBeforeBackward = backend.FramesRead;
        controller.Submit(Request(10), navigation: true);
        await controller.WaitForCurrentDemandAsync();
        Assert.Equal(1, backend.Calls);
        Assert.Equal(1010, frames[^1].Frame.Xts);
        Assert.Equal(3, frames.Count);
        await controller.WaitForIdleAsync();
        Assert.Equal(readBeforeBackward, backend.FramesRead);
        Assert.False(second.Gate.IsCompleted);
    }

    [Fact]
    public async Task PausedReadSuspendsWatchdogAndEditCancelsStream()
    {
        var clock = new FakeTimeProvider();
        var backend = new StreamingBackend();
        await using var controller = new PreviewController(backend, timeProvider: clock, debounce: TimeSpan.Zero);
        int failures = 0;
        controller.Failed += _ => ++failures;
        controller.Submit(Request());
        await backend.Pauses.Reader.ReadAsync();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, failures);
        controller.Submit(Request() with { VppArgs = new[] { "edited" } });
        await backend.Pauses.Reader.ReadAsync();
        Assert.Equal(2, backend.Calls);
        Assert.Equal(1, backend.Cancellations);
    }

    [Fact]
    public async Task LargeJumpStartsAnotherPhysicalStream()
    {
        var backend = new StreamingBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        controller.Submit(Request());
        await backend.Pauses.Reader.ReadAsync();
        controller.Submit(Request(20), navigation: true);
        await backend.Pauses.Reader.ReadAsync();
        Assert.Equal(2, backend.Calls);
        Assert.Equal(1, backend.Cancellations);
    }

    [Fact]
    public async Task TightBudgetPausesEarlyWithoutLosingCurrentFrame()
    {
        var backend = new StreamingBackend();
        var cache = new FrameCache(18);
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        PreviewSegmentUpdate? update = null;
        controller.SegmentUpdated += value => update = value;
        controller.Submit(Request(0));
        var paused = await backend.Pauses.Reader.ReadAsync();
        await controller.WaitForCurrentDemandAsync();
        Assert.Equal(1001, paused.LastTimestamp);
        Assert.NotNull(update);
        Assert.True(update.DemandSatisfied);
        Assert.True(update.IsReadPaused);
        Assert.Equal(18, cache.SizeBytes);
    }

    [Fact]
    public async Task BackgroundUsesIndependentBackendOnlyAfterForegroundPauses()
    {
        var foreground = new StreamingBackend();
        var background = new StreamingBackend();
        await using var controller = new PreviewController(foreground, debounce: TimeSpan.Zero,
            backgroundBackend: background);
        var ready = new TaskCompletionSource<PreviewCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Completed += result => ready.TrySetResult(result);
        controller.Submit(Request());
        await foreground.Pauses.Reader.ReadAsync();
        controller.Submit(Request() with { Continuous = false, VppArgs = new[] { "before" } }, background: true);
        var complete = await ready.Task;
        await controller.WaitForIdleAsync();
        Assert.Equal(1, foreground.Calls);
        Assert.Equal(1, background.Calls);
        Assert.NotNull(complete.Segment);
        Assert.Equal(8, complete.Segment.SafeStartSeconds);
        Assert.False(complete.TargetNotReached);
    }

    [Fact]
    public async Task NaturalEndExposesFinalFrameAndCompletesLifetime()
    {
        var backend = new StreamingBackend { InputEndSeconds = 10.5 };
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewCompletion? completed = null;
        controller.Completed += result => completed = result;
        controller.Submit(Request());
        await controller.WaitForIdleAsync();
        Assert.NotNull(completed);
        Assert.True(completed.Segment!.ReachedInputEnd);
        Assert.Equal(10.5, completed.Segment.SafeEndSeconds);
        Assert.Equal(0, backend.Cancellations);
    }

    [Fact]
    public async Task ZeroBasedClockFallsBackToFiniteWithoutRangeStitching()
    {
        var backend = new StreamingBackend { IgnorePassthrough = true };
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        var first = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.FrameReady += result => first.TrySetResult(result);
        controller.Submit(Request());
        var frame = await first.Task;
        Assert.False(frame.Segment!.Clock.TrustedAbsolute);
        await controller.WaitForIdleAsync();
        controller.Submit(Request(11), navigation: true);
        await controller.WaitForIdleAsync();
        Assert.Equal(3, backend.Calls);
        Assert.False(backend.Requests[^1].Continuous);
        Assert.False(backend.Requests[^1].Capabilities.TimestampPassthrough);
    }

    [Fact]
    public async Task ReverseFiniteWindowExcludesWarmupAndTailGuard()
    {
        var backend = new StreamingBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewCompletion? completed = null;
        controller.Completed += value => completed = value;
        controller.Submit(Request() with { Continuous = false, PreMarginSeconds = 5 });
        await controller.WaitForIdleAsync();
        Assert.NotNull(completed);
        var segment = completed.Segment!;
        Assert.Equal(5, segment.Clock.PhysicalSeekSeconds);
        Assert.Equal(7, segment.SafeStartSeconds);
        Assert.True(segment.SafeEndSeconds >= 10);
        Assert.True(segment.SafeEndSeconds < 10.51);
        int selected = FrameSelector.Select(segment, 10, 2);
        Assert.Equal(1010, segment.Frames[selected].Xts);
        Assert.Equal(-1, FrameSelector.Select(segment, 6, 2));
        Assert.Equal(1007, segment.Frames[FrameSelector.Select(segment, 7, 2)].Xts);
        Assert.Equal(3, 10 - segment.SafeStartSeconds);
        Assert.Equal(5, CommandBuilder.Plan(Assert.Single(backend.Requests)).SeekSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ReverseFinitePre5ClampsToInputStartWithoutWarmupExclusion(double target)
    {
        var backend = new StreamingBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewCompletion? completed = null;
        controller.Completed += value => completed = value;
        controller.Submit(Request(target) with { Continuous = false, PreMarginSeconds = 5 });
        await controller.WaitForIdleAsync();
        Assert.NotNull(completed);
        var segment = completed.Segment!;
        Assert.Null(CommandBuilder.Plan(Assert.Single(backend.Requests)).SeekSeconds);
        Assert.Equal(0, segment.Clock.PhysicalSeekSeconds);
        Assert.Equal(0, segment.SafeStartSeconds);
        Assert.Equal(0, FrameSelector.Select(segment, 0, 2));
        Assert.Equal(1000 + target, segment.Frames[FrameSelector.Select(segment, target, 2)].Xts);
    }

    [Fact]
    public async Task BackgroundWaitsUntilForegroundActuallyPauses()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreground = new StreamingBackend
        {
            BeforeFrameAsync = async index => { if (index == 0) { blocked.TrySetResult(); await release.Task; } },
        };
        var background = new StreamingBackend();
        await using var controller = new PreviewController(foreground, debounce: TimeSpan.Zero, backgroundBackend: background);
        controller.Submit(Request());
        await blocked.Task;
        controller.Submit(Request() with { Continuous = false, VppArgs = new[] { "before" } }, background: true);
        Assert.Equal(0, background.Calls);
        release.TrySetResult();
        await foreground.Pauses.Reader.ReadAsync();
        await controller.WaitForIdleAsync();
        Assert.Equal(1, background.Calls);
    }

    [Fact]
    public async Task ResumedForegroundKeepsNewBackgroundQueuedUntilNextPause()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreground = new StreamingBackend
        {
            BeforeFrameAsync = async index => { if (index == 11) { blocked.TrySetResult(); await release.Task; } },
        };
        var background = new StreamingBackend();
        await using var controller = new PreviewController(foreground, debounce: TimeSpan.Zero, backgroundBackend: background);
        controller.Submit(Request());
        await foreground.Pauses.Reader.ReadAsync();
        controller.Submit(Request(11), navigation: true);
        await blocked.Task;
        controller.Submit(Request(11) with { Continuous = false, VppArgs = new[] { "before" } }, background: true);
        Assert.Equal(0, background.Calls);
        release.TrySetResult();
        await foreground.Pauses.Reader.ReadAsync();
        await controller.WaitForIdleAsync();
        Assert.Equal(1, foreground.Calls);
        Assert.Equal(1, background.Calls);
    }

    [Fact]
    public async Task KnownDurationReleasesFiniteTailGuardAtNaturalEnd()
    {
        var backend = new StreamingBackend { InputEndSeconds = 10.2, StepSeconds = 0.1 };
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFrameResult? image = null;
        PreviewCompletion? completed = null;
        controller.FrameReady += value => image = value;
        controller.Completed += value => completed = value;
        controller.Submit(Request() with { Continuous = false, Fps = 10, InputDurationSeconds = 10.2 });
        await controller.WaitForIdleAsync();
        Assert.NotNull(image);
        Assert.False(image.TargetNotReached);
        Assert.Equal(1010, image.Frame.Xts!.Value, 6);
        Assert.True(completed!.Segment!.ReachedInputEnd);
        Assert.True(completed.Segment.SafeEndSeconds >= 10.19);
    }

    [Fact]
    public async Task UnknownDurationFiniteEndConfirmsEofAndReleasesTheGuard()
    {
        var backend = new StreamingBackend { InputEndSeconds = 10.2, StepSeconds = 0.1 };
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        int images = 0;
        controller.Failed += value => failure = value;
        controller.FrameReady += _ => ++images;
        controller.Submit(Request() with { Continuous = false, Fps = 10 });
        await controller.WaitForIdleAsync();
        Assert.Null(failure);
        Assert.Equal(1, images);
        Assert.Equal(2, backend.Calls);
    }

    [Fact]
    public async Task UnsafeWarmupAtNaturalEndBacktracksBeforeDisplaying()
    {
        var backend = new StreamingBackend { InputEndSeconds = 7.5, StepSeconds = 0.1 };
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        int images = 0;
        int completed = 0;
        controller.Failed += value => failure = value;
        controller.FrameReady += _ => ++images;
        controller.Completed += _ => ++completed;
        // 実際の入力が目標より大幅に短い場合、計画seek+2の暖機境界まで画像が届かない。
        controller.Submit(Request(10.2) with { Fps = 10 });
        await controller.WaitForIdleAsync();
        Assert.Null(failure);
        Assert.Equal(1, images);
        Assert.Equal(1, completed);
        Assert.InRange(backend.Calls, 2, 3);
    }

    [Fact]
    public async Task DirectKnownEndUsesThreeSecondWarmupAndDisplaysFinalSafeFrame()
    {
        var backend = new StreamingBackend { InputEndSeconds = 10.2, StepSeconds = 0.3 };
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFrameResult? image = null;
        PreviewCompletion? complete = null;
        controller.FrameReady += value => image = value;
        controller.Completed += value => complete = value;
        controller.Submit(Request(10.2) with { InputDurationSeconds = 10.2 });
        await controller.WaitForIdleAsync();
        Assert.Equal(7.2, CommandBuilder.Plan(Assert.Single(backend.Requests)).SeekSeconds!.Value, 6);
        Assert.NotNull(image);
        Assert.Equal(1009.9, image.Frame.Xts!.Value, 6);
        Assert.True(image.TargetNotReached);
        Assert.True(complete!.TargetNotReached);
        Assert.True(complete.Segment!.ReachedInputEnd);
        Assert.True(image.Segment!.Clock.ToInputSeconds(image.Frame, 0, 2) >= image.Segment.SafeStartSeconds);
    }

    [Theory]
    [InlineData(true, 0.003, true)]
    [InlineData(false, 0.003, true)]
    [InlineData(true, 0.005, true)]
    [InlineData(false, 0.005, true)]
    [InlineData(true, 0.006, true)]
    [InlineData(false, 0.006, true)]
    public async Task NaturalEndUsesLastFrameWithoutRequiringContainerDurationAgreement(bool continuous, double gap, bool accepted)
    {
        var backend = new StreamingBackend
        {
            InputEndSeconds = 10.19, StepSeconds = 0.1, FrameDurationSeconds = 0.1 - gap,
        };
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFrameResult? image = null;
        PreviewFailure? failure = null;
        controller.FrameReady += value => image = value;
        controller.Failed += value => failure = value;
        controller.Submit(Request(10.2) with { Continuous = continuous, Fps = 10, InputDurationSeconds = 10.2 });
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(accepted, image != null);
        Assert.Equal(!accepted, failure != null);
        Assert.Equal(continuous ? 1 : 2, backend.Requests.Count);
        if (image != null)
        {
            Assert.Equal(1010.1, image.Frame.Xts!.Value, 6);
            Assert.True(image.Segment!.ReachedInputEnd);
            // 生の表示終了時刻を延ばさず、EOF以降の要求に最終画像を使う。
            Assert.Equal(10.2 - gap, image.Segment.SafeEndSeconds, 6);
            var segment = image.Segment;
            Assert.True(PreviewSafeRange.IsKnownInputEnd(segment.PhysicalRequest, segment, 10.21));
            Assert.False(PreviewSafeRange.IsKnownInputEnd(segment.PhysicalRequest, segment with { ReachedInputEnd = false }, 10.2));
        }
    }

    [Fact]
    public async Task HorizonPauseDoesNotEvictCacheForAnUnreadFrame()
    {
        var cache = new FrameCache(36);
        var other = Request(0) with { InputFile = "cached-other" };
        cache.Store(other, new[] { new Y4mFrame(new Y4mHeader { Width = 2, Height = 2 }, 0, 0.5, new byte[6]) });
        var backend = new StreamingBackend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request(0));
        await backend.Pauses.Reader.ReadAsync();
        Assert.Equal(36, cache.SizeBytes);
        Assert.True(cache.TryGet(other, out _));
    }

    [Fact]
    public async Task VfrGapWithoutSafeCandidateFailsInsteadOfPausingForever()
    {
        var backend = new StreamingBackend { StepSeconds = 4, FrameDurationSeconds = 1 };
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        controller.Failed += value => failure = value;
        controller.Submit(Request());
        await controller.WaitForCurrentDemandAsync();
        await controller.WaitForIdleAsync();
        Assert.NotNull(failure);
        Assert.Contains("目標時刻", failure.Reason);
        Assert.False(backend.Pauses.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ThreeSecondPhysicalWarmupKeepsVfrSelectionBeforeOrAtTarget()
    {
        var backend = new StreamingBackend { StepSeconds = 0.6 };
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFrameResult? image = null;
        PreviewFailure? failure = null;
        controller.FrameReady += value => image = value;
        controller.Failed += value => failure = value;
        controller.Submit(Request());
        await backend.Pauses.Reader.ReadAsync();
        await controller.WaitForCurrentDemandAsync();
        Assert.Null(failure);
        Assert.NotNull(image);
        Assert.Equal(7, CommandBuilder.Plan(Assert.Single(backend.Requests)).SeekSeconds);
        Assert.Equal(1010, image.Frame.Xts!.Value, 6);
        Assert.False(image.TargetNotReached);
    }

    [Fact]
    public async Task ModernFiniteAfterUsesPhysicalPre3AndKeepsLogicalRequest()
    {
        var backend = new StreamingBackend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        PreviewFrameResult? image = null;
        PreviewCompletion? completed = null;
        controller.FrameReady += value => image = value;
        controller.Completed += value => completed = value;
        var logical = Request() with { Continuous = false };
        controller.Submit(logical);
        await controller.WaitForIdleAsync();
        Assert.NotNull(image);
        Assert.Equal(2, image.Request.PreMarginSeconds);
        Assert.Equal(7, image.Segment!.Clock.PhysicalSeekSeconds);
        Assert.Equal(7, CommandBuilder.Plan(completed!.Segment!.PhysicalRequest).SeekSeconds);
        Assert.Equal(7, CommandBuilder.Plan(Assert.Single(backend.Requests)).SeekSeconds);
        Assert.Equal(1010, image.Frame.Xts);
    }

    [Fact]
    public async Task ShiftedClockMismatchKeepsAbsoluteClockAndCurrentStreamNavigation()
    {
        var backend = new StreamingBackend
        {
            StepSeconds = 1.0 / 24, InputPixels = true, TimestampShifts = new[] { 0d, 1.0 / 30 },
        };
        var cache = new FrameCache();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        var images = new List<PreviewFrameResult>();
        controller.FrameReady += images.Add;
        var request = Request() with { Fps = 24 };
        controller.Submit(request);
        await backend.Pauses.Reader.ReadAsync();
        long old = cache.GetNavigationFrames(request)[0].SegmentId;
        controller.Submit(request with { TargetSeconds = 11 }, bypassCache: true);
        await backend.Pauses.Reader.ReadAsync();
        await controller.WaitForCurrentDemandAsync();
        Assert.Null(cache.GetSegment(old));
        Assert.False(cache.CanStitch(request));
        Assert.False(images[^1].Segment!.CanStitch);
        Assert.True(images[^1].Segment!.Clock.TrustedAbsolute);
        double time = images[^1].Segment!.Clock.ToInputSeconds(images[^1].Frame, 0, 24);
        Assert.InRange(time, 10.9, 11.1);
        controller.Submit(request with { TargetSeconds = 12 }, navigation: true);
        await backend.Pauses.Reader.ReadAsync();
        await controller.WaitForCurrentDemandAsync();
        Assert.Equal(2, backend.Calls);
        controller.Submit(request with { TargetSeconds = 30 }, navigation: true);
        await controller.WaitForIdleAsync();
        // 読み取り停止まで待っても、その通知は残る。前の要求の通知を次の入力の開始と取り違えないよう、ここで受け取る。
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(3, backend.Calls);
        // 区間間の照合が失敗しても、新しい1本のstream内では連続取得を維持する。
        Assert.True(backend.Requests[^1].Continuous);
        Assert.False(cache.CanStitch(request));
        Assert.True(images[^1].Segment!.Clock.TrustedAbsolute);
        Assert.InRange(images[^1].Segment!.Clock.ToInputSeconds(images[^1].Frame, 0, 24), 29.9, 30.1);
        controller.Submit(request with { InputFile = "fresh" });
        await backend.Pauses.Reader.ReadAsync();
        controller.Submit(request);
        await backend.Pauses.Reader.ReadAsync();
        Assert.Equal(5, backend.Calls);
        Assert.True(backend.Requests[^1].Continuous);
        Assert.True(cache.CanStitch(request));
    }

    [Fact]
    public async Task HalfFrameBoundaryWithSingleFrameBudgetFailsInsteadOfLeavingDemandPending()
    {
        var backend = new StreamingBackend();
        var cache = new FrameCache(6);
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        controller.Failed += value => failure = value;
        controller.Submit(Request(0.25));
        await controller.WaitForIdleAsync();
        Assert.NotNull(failure);
        Assert.False(cache.TryGetAt(Request(0.25), 0.25, out _));
    }

    [Fact]
    public async Task LongVfrDurationUsesLargerNeighborAndDoesNotTriggerLegacyReplay()
    {
        var backend = new StreamingBackend { FrameDurationForIndex = index => index == 0 ? 0.01 : 0.5 };
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        controller.Submit(Request());
        await controller.WaitForIdleAsync();
        Assert.Equal(1, backend.Calls);
        Assert.False(controller.IsLegacySource(Request()));
    }

    [Fact]
    public async Task EarlyFiniteFrameBlocksOldCacheUntilValidationFinishes()
    {
        var cache = new FrameCache();
        var request = Request() with { Continuous = false, CorrelationId = 2 };
        var frames = Enumerable.Range(0, 8).Select(i => new Y4mFrame(new Y4mHeader { Width = 2, Height = 2 },
            1007 + i * 0.5, 0.5, new byte[6])).ToArray();
        cache.StoreSegment(request, frames, new(1000, 7, true), 9, 10.5);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new StreamingBackend { BeforeFinishAsync = () => release.Task };
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        var early = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new List<PreviewCompletion>();
        controller.FrameReady += value => { if (value.Request.CorrelationId == 2) early.TrySetResult(value); };
        controller.Completed += completions.Add;
        controller.Submit(request, bypassCache: true);
        Assert.False((await early.Task).Segment!.CanStitch);
        controller.Submit(request with { TargetSeconds = 10.5, Continuous = true, CorrelationId = 3 }, navigation: true);
        Assert.DoesNotContain(completions, value => value.Request.CorrelationId == 3);
        Assert.Equal(1, backend.Calls);
        release.TrySetResult();
        await backend.Pauses.Reader.ReadAsync();
        await controller.WaitForIdleAsync();
        Assert.Equal(2, backend.Calls);
    }

    private sealed record Pause(double LastTimestamp, Task Gate);
    private sealed class StreamingBackend : IPreviewBackend
    {
        public Channel<Pause> Pauses { get; } = Channel.CreateUnbounded<Pause>();
        public bool InputPixels { get; init; }
        public IReadOnlyList<double> TimestampShifts { get; init; } = Array.Empty<double>();
        public double StepSeconds { get; init; } = 0.5;
        public double? FrameDurationSeconds { get; init; }
        public Func<int, double>? FrameDurationForIndex { get; init; }
        public Func<int, Task>? BeforeFrameAsync { get; init; }
        public Func<Task>? BeforeFinishAsync { get; init; }
        public double? InputEndSeconds { get; init; }
        public bool IgnorePassthrough { get; init; }
        public List<PreviewRequest> Requests { get; } = new();
        public int Calls { get; private set; }
        public int FramesRead { get; private set; }
        public int Cancellations { get; private set; }
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            ++Calls;
            Requests.Add(request);
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            double shift = TimestampShifts.Count == 0 ? 0 : TimestampShifts[Math.Min(Calls - 1, TimestampShifts.Count - 1)];
            var header = new Y4mHeader { Width = 2, Height = 2 };
            double last = double.NaN;
            try
            {
                for (int i = 0; ; ++i)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((!request.Continuous && i >= CommandBuilder.Plan(request).FrameCount)
                        || (InputEndSeconds is double end && seek + i * StepSeconds >= end))
                    {
                        if (BeforeFinishAsync != null) await BeforeFinishAsync().WaitAsync(cancellationToken);
                        return new(0, "");
                    }
                    var permission = sink.BeforeReadAsync(cancellationToken);
                    if (!permission.IsCompleted)
                    {
                        sink.OnReadStateChanged(false);
                        Pauses.Writer.TryWrite(new(last, permission.AsTask()));
                    }
                    await permission;
                    sink.OnReadStateChanged(true);
                    if (BeforeFrameAsync != null) await BeforeFrameAsync(i).WaitAsync(cancellationToken);
                    sink.OnOutput();
                    last = IgnorePassthrough || !request.Capabilities.TimestampPassthrough ? i * StepSeconds : 1000 + seek + i * StepSeconds + shift;
                    ++FramesRead;
                    byte pixel = InputPixels ? (byte)((int)Math.Round((seek + i * StepSeconds) / StepSeconds) % 251) : (byte)0;
                    sink.OnFrame(new(header, last, FrameDurationForIndex?.Invoke(i) ?? FrameDurationSeconds ?? StepSeconds, Enumerable.Repeat(pixel, 6).ToArray()));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ++Cancellations;
                return new(-12, "", true);
            }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

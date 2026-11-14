using HWEncVppPreview.Core;
namespace HWEncVppPreview.Tests;

public sealed class WindowRecoveryTests
{
    [Fact]
    public async Task LateKeyAtThreeSecondsRecoversBeforeFromTheInputStart()
    {
        var backend=new WindowBackend(true);
        await using var controller=new PreviewController(backend,backend,cache:new FrameCache(0),debounce:TimeSpan.Zero);
        PreviewFrameResult? image=null;PreviewFailure? failure=null;
        controller.FrameReady+=value=>image=value;controller.Failed+=value=>failure=value;
        controller.Submit(Request(3,24000.0/1001),background:true);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);Assert.NotNull(image);
        Assert.Equal(2,backend.Requests.Count);
        Assert.Null(CommandBuilder.Plan(backend.Requests[1]).SeekSeconds);
        Assert.Equal(1003.003,image.Frame.Xts!.Value,6);
    }
    [Fact]
    public async Task LeadingFramesConfirmTheTailWithoutChangingSeek()
    {
        var backend=new WindowBackend(false);
        await using var controller=new PreviewController(backend,backend,cache:new FrameCache(0),debounce:TimeSpan.Zero);
        PreviewFrameResult? image=null;PreviewFailure? failure=null;
        controller.FrameReady+=value=>image=value;controller.Failed+=value=>failure=value;
        controller.Submit(Request(472.583,60000.0/1001) with {PreMarginSeconds=5});
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);Assert.NotNull(image);
        Assert.Equal(2,backend.Requests.Count);
        Assert.Equal(CommandBuilder.Plan(backend.Requests[0]).SeekSeconds,CommandBuilder.Plan(backend.Requests[1]).SeekSeconds);
        Assert.True(backend.Requests[1].Continuous);
        Assert.Equal(Math.Round(1467.667189 + 295 * 1001.0 / 60000, 6), image.Frame.Xts!.Value);
    }
    [Fact]
    public async Task StreamLandingAfterTargetRecoversWithoutChangingItsTimestampMode()
    {
        var backend = new SyntheticBackend((request, call) => call == 1 ? request.TargetSeconds + 0.5 : CommandBuilder.Plan(request).SeekSeconds ?? 0);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        PreviewFrameResult? image = null;
        PreviewFailure? failure = null;
        controller.FrameReady += value => image = value;
        controller.Failed += value => failure = value;
        controller.Submit(Request(10, 10) with { Continuous = true });
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.NotNull(image);
        Assert.Equal(2, backend.Requests.Count);
        Assert.True(backend.Requests[0].Continuous);
        Assert.False(backend.Requests[1].Continuous);
        Assert.Null(image.Request.RecoveryPhysicalWindow);
        Assert.All(backend.Requests, request => Assert.True(request.Capabilities.TimestampPassthrough));
        Assert.Equal(1010, image.Frame.Xts!.Value, 6);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoProgressStopsAndPreservesStructuredShortfall(bool background)
    {
        var backend = new SyntheticBackend((request, call) => 10.5);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        controller.Failed += value => failure = value;
        controller.Submit(Request(10, 10), background: background);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.NotNull(failure);
        Assert.True(failure.WindowShortfall!.MissingHead);
        Assert.False(failure.WindowShortfall.MissingTail);
        Assert.Equal(1010.5, failure.WindowShortfall.FirstXts);
        Assert.Equal(1 + PreviewWindowRecovery.MaximumRetries, backend.Requests.Count);
        Assert.Same(backend.Requests[^1], failure.WindowShortfall.RequestedWindow);
    }
    [Fact]
    public async Task NoEarlierDataAtInputStartFailsWithoutRepeatingTheSameWindow()
    {
        var backend = new SyntheticBackend((request, call) => 0.5);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        controller.Failed += value => failure = value;
        controller.Submit(Request(0.1, 10));
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.NotNull(failure);
        Assert.True(failure.WindowShortfall!.MissingHead);
        Assert.Single(backend.Requests);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExpansionChangesOnlyTheMissingSideAndHasAFiniteLimit(bool head)
    {
        var physical = Request(10, 10) with { SeekSecondsOverride = 7, PreMarginSeconds = 3, PostMarginSeconds = 0.5 };
        var shortfall = new PreviewWindowShortfall(head, !head, 1010.5, 1012, physical, 10,
            head ? 10.5 : 7, 12, head ? 10.5 : 9, head ? 12 : 9.9, false);
        Assert.True(PreviewWindowRecovery.TryExpand(physical, shortfall, 0, out var expanded));
        var next = expanded.RecoveryPhysicalWindow!;
        if (head) Assert.Equal(physical.PostMarginSeconds, next.PostMarginSeconds);
        else Assert.Equal(CommandBuilder.Plan(physical).SeekSeconds, CommandBuilder.Plan(next).SeekSeconds);
        Assert.False(PreviewWindowRecovery.TryExpand(physical, shortfall, PreviewWindowRecovery.MaximumRetries, out _));
    }
    [Fact]
    public async Task TheSameLateKeyCanBeCrossedByTheSecondBoundedHeadExpansion()
    {
        var backend = new SyntheticBackend((request, call) => Math.Ceiling((CommandBuilder.Plan(request).SeekSeconds ?? 0) / 10) * 10);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        PreviewFrameResult? image = null;
        PreviewFailure? failure = null;
        controller.FrameReady += value => image = value;
        controller.Failed += value => failure = value;
        controller.Submit(Request(9, 10));
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.NotNull(image);
        Assert.Equal(1009, image.Frame.Xts);
        Assert.Equal(3, backend.Requests.Count);
        Assert.Null(CommandBuilder.Plan(backend.Requests[^1]).SeekSeconds);
    }

    [Fact]
    public async Task ANewEditCancelsTheOldRecoveryAndNeverPublishesItsFrame()
    {
        var backend = new BlockingRecoveryBackend();
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        var selected = new List<double>();
        controller.FrameReady += value => selected.Add(value.Request.TargetSeconds);
        controller.Submit(Request(10, 10) with { PreMarginSeconds = 5 });
        await backend.RecoveryStarted.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        controller.Submit(Request(20, 10));
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.True(backend.Cancelled);
        Assert.Equal(new[] { 20d }, selected);
    }

    [Fact]
    public async Task AContinuouslyImprovingButStillMissingHeadStopsAtTheRetryLimit()
    {
        var backend = new SyntheticBackend((request, call) => request.TargetSeconds + 0.5 - call * 0.1);
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        PreviewFailure? failure = null;
        controller.Failed += value => failure = value;
        controller.Submit(Request(100, 10));
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(1 + PreviewWindowRecovery.MaximumRetries, backend.Requests.Count);
        Assert.NotNull(failure);
        Assert.True(failure.WindowShortfall!.MissingHead);
    }

    [Fact]
    public void AConfirmedInputEndCannotBeExtendedIntoASuccess()
    {
        var request = Request(10, 10);
        var shortfall = new PreviewWindowShortfall(false, true, 1008, 1009.8, request, 10,
            8, 9.8, 8, 9.9, true);
        Assert.False(PreviewWindowRecovery.TryExpand(request, shortfall, 0, out _));
    }

    private sealed class BlockingRecoveryBackend : IPreviewBackend
    {
        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        private int _oldRuns;
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            if (request.TargetSeconds == 10 && ++_oldRuns == 2)
            {
                RecoveryStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F10:1 C420");
            int count = CommandBuilder.Plan(request).FrameCount - (request.TargetSeconds == 10 ? 7 : 0);
            for (int i = 0; i < count; ++i)
                sink.OnFrame(new(header, 1000 + seek + i * 0.1, 0.1, new byte[6]));
            return new(0, "");
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SafeCandidateDoesNotPublishBeforeTheTargetItselfBecomesSafe()
    {
        var backend = new NearTailBackend();
        await using var controller = new PreviewController(backend, backend, debounce: TimeSpan.Zero);
        int images = 0;
        PreviewFailure? failure = null;
        controller.FrameReady += _ => ++images;
        controller.Failed += value => failure = value;
        controller.Submit(Request(10, 10));
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.Equal(2, backend.Runs);
        Assert.Equal(1, images);
    }

    private sealed class NearTailBackend : IPreviewBackend
    {
        public int Runs { get; private set; }
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            ++Runs;
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F10:1 C420");
            foreach (double time in Enumerable.Range(70, 30).Select(index => index / 10.0).Concat(new[] { 9.99, Runs == 1 ? 10.395 : 10.6 }))
                sink.OnFrame(new(header, 1000 + time, 0.1, new byte[6]));
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SyntheticBackend(Func<PreviewRequest, int, double> landing) : IPreviewBackend
    {
        public List<PreviewRequest> Requests { get; } = new();
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F10:1 C420");
            double first = landing(request, Requests.Count);
            int count = request.Continuous ? 100 : Math.Max(30, CommandBuilder.Plan(request).FrameCount);
            for (int i = 0; i < count; ++i)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await sink.BeforeReadAsync(cancellationToken);
                sink.OnFrame(new(header, Math.Round(1000 + first + i * 0.1, 6), 0.1, new byte[6]));
            }
            return new(0, "");
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static PreviewRequest Request(double target,double fps)=>new()
    {
        EncoderPath="encoder",InputFile="input.ts",Fps=fps,TargetSeconds=target,
        Capabilities=new(true),TimestampOriginSeconds=1000,InputDurationSeconds=1800,
    };
    private sealed class WindowBackend(bool head) : IPreviewBackend
    {
        public List<PreviewRequest> Requests {get;}=new();
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request,CancellationToken token=default)=>Task.FromResult<bool?>(false);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request,IPreviewSink sink,CancellationToken cancellationToken=default)
        {
            Requests.Add(request);
            var plan=CommandBuilder.Plan(request);double seek=plan.SeekSeconds??0;
            double first=head?(seek>0?3.003:0):467.667189;
            int frames=plan.FrameCount-(head?0:7);
            var header=Y4mHeader.Parse(head?"YUV4MPEG2 W2 H2 F24000:1001 C420":"YUV4MPEG2 W2 H2 F60000:1001 C420");
            for(int i=0;i<frames;i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sink.OnFrame(new(header,Math.Round(1000+first+i/request.Fps,6),null,new byte[6]));
            }
            return Task.FromResult(new PreviewRunResult(0,""));
        }
        public ValueTask AbortAsync()=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}

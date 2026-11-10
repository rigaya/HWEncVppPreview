using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class AfterCompletionEvictionTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentCompletionSurvivesEvictionAndQueuesSharedBefore(bool removeBeforePost)
    {
        await using var fixture = new Fixture();
        var frames = Frames();
        var segment = fixture.Store(frames);
        object context = fixture.Register(segment);
        // 有限の早期画像はcache保存前のID0。完了のUI反映までを明示的に止める。
        Invoke(fixture.Vm, "OnFrameReady", new PreviewFrameResult(fixture.Request, frames[30], false)
            { Segment = segment with { SegmentId = 0, CanStitch = false } });
        if (removeBeforePost) fixture.Cache.RemoveSegment(segment.SegmentId);
        fixture.Complete(segment);
        if (!removeBeforePost) fixture.Cache.RemoveSegment(segment.SegmentId);
        await fixture.Idle();
        Assert.False(fixture.Vm.IsBusy);
        Assert.Null(fixture.Vm.ErrorText);
        Assert.NotNull(fixture.Vm.PreviewImage);
        Assert.NotNull(fixture.Vm.BeforeImage);
        Assert.Same(frames[30], Field(fixture.Vm, "_displayedFrame"));
        Assert.Same(frames[30], Field(fixture.Vm, "_beforeDisplayedFrame"));
        fixture.AssertCompact(context, 1);
        Assert.Equal(1L, Field(fixture.Vm, "_lastCompletedAfter"));
    }

    [AvaloniaTheory]
    [InlineData(-1, 0)]
    [InlineData(1, 2)]
    public async Task VfrNeighborUsesExactAnchorRatherThanHalfDurationCandidate(int direction, int expected)
    {
        await using var fixture = new Fixture();
        double[] times = [0.9, 1, 1.005, 1.04, 1.3];
        var frames = times.Select((time, i) => Frame(i, time, 0.2)).ToArray();
        var segment = fixture.Store(frames, safeStart: 0.9);
        object context = fixture.Register(segment, direction);
        fixture.Complete(segment);
        fixture.Cache.RemoveSegment(segment.SegmentId);
        await fixture.Idle();
        Assert.Null(Field(fixture.Vm, "_pendingNeighbor"));
        Assert.Equal(times[expected], fixture.Vm.CurrentSeconds, 8);
        Assert.Same(frames[expected], Field(fixture.Vm, "_displayedFrame"));
        Assert.False(fixture.Vm.IsBusy);
        fixture.AssertCompact(context, 3);
    }

    [AvaloniaFact]
    public async Task UntimedTrimmedWindowKeepsGlobalFrameNumbersForNeighborAndBefore()
    {
        await using var fixture = new Fixture();
        var frames = Frames(untimed: true);
        var stored = fixture.Store(frames, trusted: false);
        var segment = new PreviewFrameReference(stored, 6).RetainFrameOnly().Segment with { Frames = frames.Skip(6).ToArray() };
        object context = fixture.Register(segment, 1);
        fixture.Complete(segment);
        fixture.Cache.RemoveSegment(segment.SegmentId);
        await fixture.Idle();
        Assert.Equal(31d / 30, fixture.Vm.CurrentSeconds, 8);
        Assert.Same(frames[31], Field(fixture.Vm, "_displayedFrame"));
        Assert.Same(frames[31], Field(fixture.Vm, "_beforeDisplayedFrame"));
        Assert.False(fixture.Vm.IsBusy);
        fixture.AssertCompact(context, 3);
    }

    [AvaloniaTheory]
    [InlineData(-1, 0)]
    [InlineData(1, 1.4)]
    public async Task MissingNeighborEndsAtBoundaryWithoutKeepingPending(int direction, double target)
    {
        await using var fixture = new Fixture(target);
        var segment = fixture.Store(Frames());
        object context = fixture.Register(segment, direction);
        fixture.Complete(segment);
        fixture.Cache.RemoveSegment(segment.SegmentId);
        await fixture.Idle();
        Assert.Null(Field(fixture.Vm, "_pendingNeighbor"));
        Assert.Equal(target, fixture.Vm.CurrentSeconds, 8);
        Assert.False(fixture.Vm.IsBusy);
        Assert.Null(fixture.Vm.ErrorText);
        Assert.True((bool)Property(context, "NeighborResolved")!);
        fixture.AssertCompact(context, 3);
    }

    [AvaloniaFact]
    public async Task UnsafeTargetIsNotReportedAsSuccessfulCompletion()
    {
        await using var fixture = new Fixture(0.1);
        var segment = fixture.Store(Frames(), safeStart: 0.5);
        object context = fixture.Register(segment);
        fixture.Complete(segment);
        fixture.Cache.RemoveSegment(segment.SegmentId);
        await fixture.Idle();
        Assert.False(fixture.Vm.IsBusy);
        Assert.NotNull(fixture.Vm.ErrorText);
        Assert.Null(fixture.Vm.PreviewImage);
        Assert.Null(fixture.Vm.BeforeImage);
        fixture.AssertCompact(context, 0);
    }

    [AvaloniaFact]
    public async Task SupersededCompletionCannotRestoreIntervalOrQueueBefore()
    {
        await using var fixture = new Fixture();
        var segment = fixture.Store(Frames());
        object context = fixture.Register(segment);
        fixture.Complete(segment);
        Invoke(fixture.Vm, "BeginIntent", true);
        fixture.Cache.RemoveSegment(segment.SegmentId);
        await fixture.Idle();
        Assert.Null(Property(context, "Segment"));
        Assert.Empty((IReadOnlyList<Y4mFrame>)Field(fixture.Vm, "_intervalFrames")!);
        Assert.Null(fixture.Vm.PreviewImage);
        Assert.Null(fixture.Vm.BeforeImage);
        Assert.Equal(0L, Field(fixture.Vm, "_lastCompletedAfter"));
    }

    [AvaloniaFact]
    public async Task BeforeCompletionDoesNotOverwriteAfterIntervalOrStatus()
    {
        await using var fixture = new Fixture();
        var frames = Frames();
        var segment = fixture.Store(frames);
        fixture.Register(segment);
        fixture.Complete(segment);
        fixture.Cache.RemoveSegment(segment.SegmentId);
        await fixture.Idle();
        var afterImage = fixture.Vm.PreviewImage;
        string status = fixture.Vm.StatusText;
        var interval = Field(fixture.Vm, "_intervalFrames");
        var before = fixture.Request with { CorrelationId = -1 };
        var beforeFrames = Frames().Select(frame => new Y4mFrame(frame.Header, frame.Xts, frame.Xdur,
            Enumerable.Repeat((byte)120, frame.Data.Length).ToArray())).ToArray();
        var beforeSegment = fixture.Cache.StoreSegment(before, beforeFrames, new(0, 0, true), 0, 1.4)!;
        fixture.Register(beforeSegment, before: true);
        Invoke(fixture.Vm, "OnCompleted", new PreviewCompletion(before, new(0, "before only"), false, false)
            { Segment = beforeSegment, Frames = beforeFrames });
        fixture.Cache.RemoveSegment(beforeSegment.SegmentId);
        await fixture.Idle();
        Assert.Same(afterImage, fixture.Vm.PreviewImage);
        Assert.Same(interval, Field(fixture.Vm, "_intervalFrames"));
        Assert.Equal(status, fixture.Vm.StatusText);
        Assert.Same(frames[30], Field(fixture.Vm, "_displayedFrame"));
        Assert.Same(beforeFrames[30], Field(fixture.Vm, "_beforeDisplayedFrame"));
        Assert.False(fixture.Vm.IsBeforeBusy);
    }

    [AvaloniaFact]
    public async Task PositiveIdImageAlreadyConvertingIsReplacedWithoutStartingANewGeneration()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int conversions = 0;
        await using var fixture = new Fixture(converter: async (frame, token) =>
        {
            if (Interlocked.Increment(ref conversions) == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return new byte[16];
        });
        try
        {
            var frames = Frames();
            var segment = fixture.Store(frames);
            object context = fixture.Register(segment);
            Invoke(fixture.Vm, "OnFrameReady", new PreviewFrameResult(fixture.Request, frames[30], false) { Segment = segment });
            await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitWithDiagnosticsAsync(Safety);
            await started.Task.WaitWithDiagnosticsAsync(Safety);
            fixture.Complete(segment);
            fixture.Cache.RemoveSegment(segment.SegmentId);
            await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitWithDiagnosticsAsync(Safety);
            release.TrySetResult();
            await fixture.Idle();
            Assert.Equal(1L, Field(fixture.Vm, "_generation"));
            Assert.False(fixture.Vm.IsBusy);
            Assert.Same(frames[30], Field(fixture.Vm, "_displayedFrame"));
            Assert.NotNull(fixture.Vm.PreviewImage);
            Assert.NotNull(fixture.Vm.BeforeImage);
            fixture.AssertCompact(context, 1);
        }
        finally { release.TrySetResult(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeighborBeyondSafeRangeDoesNotDisplayUnsafeFrameOrKeepBusy(bool trusted)
    {
        await using var fixture = new Fixture();
        var segment = fixture.Store(Frames(), trusted: trusted) with { SafeEndSeconds = 1 };
        object context = fixture.Register(segment, 1);
        fixture.Complete(segment);
        fixture.Cache.RemoveSegment(segment.SegmentId);
        await fixture.Idle();
        Assert.Equal(1, fixture.Vm.CurrentSeconds, 8);
        Assert.Null(Field(fixture.Vm, "_pendingNeighbor"));
        Assert.False(fixture.Vm.IsBusy);
        Assert.Null(fixture.Vm.ErrorText);
        Assert.True((bool)Property(context, "NeighborResolved")!);
        fixture.AssertCompact(context, 3);
    }

    private static Y4mFrame Frame(int index, double? timestamp, double duration = 1d / 30)
        => new(Header, timestamp, duration, Enumerable.Repeat((byte)index, Header.FrameBytes).ToArray());
    private static Y4mFrame[] Frames(bool untimed = false)
        => Enumerable.Range(0, 43).Select(i => Frame(i, untimed ? null : i / 30d)).ToArray();
    private static object? Field(object value, string name) => value.GetType().GetField(name, Flags)!.GetValue(value);
    private static void SetField(object value, string name, object? field) => value.GetType().GetField(name, Flags)!.SetValue(value, field);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name, Flags)!.GetValue(value);
    private static object? Invoke(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Flags)!.Invoke(value, arguments);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Services _services = new();
        public FrameCache Cache { get; } = new();
        public MainWindowViewModel Vm { get; }
        public PreviewRequest Request { get; }
        public Fixture(double target = 1, Func<Y4mFrame, CancellationToken, Task<byte[]>>? converter = null)
        {
            Request = new() { CorrelationId = 1, TargetSeconds = target, Fps = 30, EncoderPath = "unused",
                InputFile = "after.ts", Capabilities = new(true, true), TimestampOriginSeconds = 0 };
            Vm = new(_services, null, new UnusedBackend(), null, null, Cache, converter);
            SetField(Vm, "_currentSeconds", target);
            Vm.IsBusy = true;
        }
        public PreviewSegmentSnapshot Store(Y4mFrame[] frames, bool trusted = true, double safeStart = 0)
            => Cache.StoreSegment(Request, frames, new(0, 0, trusted), safeStart, 1.5)!;
        public object Register(PreviewSegmentSnapshot segment, int direction = 0, bool before = false)
        {
            var type = typeof(MainWindowViewModel).GetNestedType("RequestContext", BindingFlags.NonPublic)!;
            var kind = typeof(MainWindowViewModel).GetNestedType("RequestKind", BindingFlags.NonPublic)!;
            var constructor = type.GetConstructors(Flags).Single();
            object context = constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.Name switch
            {
                "generation" => (object)1L, "request" => segment.PhysicalRequest, "started" => 0L,
                "kind" => Enum.Parse(kind, before ? "Before" : "After"), "neighborDirection" => direction,
                _ => parameter.DefaultValue,
            }).ToArray());
            context.GetType().GetProperty("Segment")!.SetValue(context, segment);
            SetField(Vm, "_generation", 1L);
            if (before) SetField(Vm, "_beforeExecutionContext", context);
            else
            {
                Invoke(Vm, "RegisterContext", context);
                if (direction != 0)
                {
                    var intent = typeof(MainWindowViewModel).GetNestedType("NeighborIntent", BindingFlags.NonPublic)!;
                    SetField(Vm, "_pendingNeighbor", Activator.CreateInstance(intent, 1L, direction));
                }
            }
            return context;
        }
        public void Complete(PreviewSegmentSnapshot segment) => Invoke(Vm, "OnCompleted",
            new PreviewCompletion(Request, new(0, "after only"), false, false) { Segment = segment, Frames = segment.Frames });
        public async Task Idle()
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitWithDiagnosticsAsync(Safety);
            await Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        }
        public void AssertCompact(object context, int maximum)
        {
            var retained = Assert.IsType<PreviewSegmentSnapshot>(Property(context, "Segment"));
            Assert.InRange(retained.Frames.Count, 0, maximum);
            Assert.Equal(0, retained.SegmentId);
            Assert.False(retained.CanStitch);
            Assert.InRange(((IReadOnlyList<Y4mFrame>)Field(Vm, "_intervalFrames")!).Count, 0, maximum);
        }
        public async ValueTask DisposeAsync() { await Vm.DisposeAsync(); _services.Dispose(); }
    }
    private sealed class UnusedBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("完了通知試験ではbackendを起動しない。");
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "after-eviction-" + Guid.NewGuid().ToString("N"));
        public Services() => Directory.CreateDirectory(_directory);
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose() => Directory.Delete(_directory, true);
    }
}

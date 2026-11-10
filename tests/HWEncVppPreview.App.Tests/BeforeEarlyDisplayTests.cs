using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class BeforeEarlyDisplayTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 2, FpsDen = 1 };

    [AvaloniaFact]
    public async Task END前の一枚を表示し最終同frameでは正segment保護だけを更新する()
    {
        const int ParentFrames = 5;
        const int SelectedIndex = 2;
        await using var state = new State();
        var producers = Enumerable.Range(0, ParentFrames).Select(i => state.Frame(i * .5, (byte)(32 + i))).ToArray();
        var descriptors = producers.Select(x => x.Frame).ToArray();
        var selected = descriptors[SelectedIndex];
        state.Ready(selected, state.Segment(descriptors));
        foreach (var producer in producers) producer.Dispose();
        await state.Pump();
        Assert.NotNull(state.Vm.BeforeImage);
        Assert.Same(selected, Field(state.Vm, "_beforeDisplayedFrame"));
        Assert.True(state.Vm.IsBeforeBusy);
        Assert.Null(state.Vm.PreviewImage);
        Assert.Equal(1, state.Scope.Capture().LiveOwners);
        for (int i = 0; i < ParentFrames; ++i)
            if (i != SelectedIndex) Assert.Throws<ObjectDisposedException>(() => descriptors[i].RawPixels);
        long version = (long)Field(state.Vm, "_beforeImageGeneration")!;
        var image = state.Vm.BeforeImage;
        var cache = (FrameCache)Field(state.Vm, "_cache")!;
        var final = cache.StoreSegment(state.Request, new[] { selected }, new(1000, 0, true), 1, 1.5)!;
        state.Complete(new[] { selected }, final);
        await state.Pump();
        Assert.Same(image, state.Vm.BeforeImage);
        Assert.True((long)Field(state.Vm, "_beforeImageGeneration")! > version);
        Assert.Equal(final.SegmentId, Field(state.Vm, "_beforePresentationSegmentId"));
        Assert.False(state.Vm.IsBeforeBusy);
        Assert.Equal(1, state.Vm.CurrentSeconds);
        Assert.Null(Field(state.Vm, "_navigationAnchor"));
    }

    [AvaloniaFact]
    public async Task 後続longDurationの最終選択が早期画像を置き換える()
    {
        await using var state = new State();
        using var early = state.Frame(1, 32);
        using var final = state.Frame(1.2, 64, 1);
        state.Ready(early.Frame, state.Segment(new[] { early.Frame }));
        await state.Pump();
        Assert.Same(early.Frame, Field(state.Vm, "_beforeDisplayedFrame"));
        var initialImage = state.Vm.BeforeImage;
        state.Complete(new[] { early.Frame, final.Frame }, reason: "受信した表示時間が一致しない");
        await state.Pump();
        Assert.Same(final.Frame, Field(state.Vm, "_beforeDisplayedFrame"));
        Assert.NotSame(initialImage, state.Vm.BeforeImage);
        Assert.False(state.Vm.IsBeforeBusy);
        Assert.Null(Field(state.Context, "BeforeEarlyFrame"));
        Assert.Equal(1, state.Vm.CurrentSeconds);
        Assert.Null(state.Vm.PreviewImage);
    }

    [AvaloniaFact]
    public async Task ENDがUIより先着した場合は古い早期Postを表示しない()
    {
        await using var state = new State();
        using var early = state.Frame(1, 32);
        using var final = state.Frame(1.2, 64, 1);
        state.Ready(early.Frame, state.Segment(new[] { early.Frame }));
        state.Complete(new[] { early.Frame, final.Frame }, reason: "受信した表示時間が一致しない");
        await state.Pump();
        Assert.Same(final.Frame, Field(state.Vm, "_beforeDisplayedFrame"));
        Assert.Null(Field(state.Context, "BeforeEarlyFrame"));
        Assert.False(state.Vm.IsBeforeBusy);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UI前の目標移動と取消は早期画像をcommitしない(bool cancel)
    {
        await using var state = new State();
        using var early = state.Frame(1, 32);
        state.Ready(early.Frame, state.Segment(new[] { early.Frame }));
        if (cancel) state.Call("CancelBefore", false);
        else state.Call("SetTimeOnly", 2d);
        await state.Pump();
        Assert.Null(state.Vm.BeforeImage);
        Assert.Null(Field(state.Context, "BeforeEarlyFrame"));
        Assert.Null(state.Vm.PreviewImage);
        Assert.Null(Field(state.Vm, "_navigationAnchor"));
        Assert.Equal(cancel ? 1 : 2, state.Vm.CurrentSeconds);
    }

    [AvaloniaFact]
    public async Task 再取得へ切り替わる前の早期Postは表示しない()
    {
        await using var state = new State();
        using var early = state.Frame(1, 32);
        state.Ready(early.Frame, state.Segment(new[] { early.Frame }));
        typeof(MainWindowViewModel).GetMethod("BeginReplay", Flags | BindingFlags.Static)!
            .Invoke(null, new[] { state.Context });
        await state.Pump();
        Assert.Null(state.Vm.BeforeImage);
        Assert.Null(Field(state.Context, "BeforeEarlyFrame"));
    }

    [AvaloniaFact]
    public async Task 不適格な候補はENDで従来どおり表示する()
    {
        await using var state = new State();
        using var frame = state.Frame(1, 32);
        state.Ready(frame.Frame, state.Segment(new[] { frame.Frame }), false);
        await state.Pump();
        Assert.Null(state.Vm.BeforeImage);
        state.Complete(new[] { frame.Frame });
        await state.Pump();
        Assert.Same(frame.Frame, Field(state.Vm, "_beforeDisplayedFrame"));
        Assert.False(state.Vm.IsBeforeBusy);
    }

    [AvaloniaFact]
    public async Task 早期候補のTry取得失敗は表示世代を更新せずrawを復活させない()
    {
        await using var state = new State();
        var producer = state.Frame(1, 32);
        var frame = producer.Frame;
        var segment = state.Segment(new[] { frame });
        producer.Dispose();
        long version = (long)Field(state.Vm, "_beforeImageGeneration")!;
        // 通知所有がない入口でも、失敗した記述子を通常Captureへ渡さないことを確かめる。
        state.Call("QueueEarlyBeforeFrame", state.Context,
            new PreviewFrameResult(state.Request, frame, false) { Segment = segment, CanDisplayBeforeEnd = true }, segment);
        await state.Pump();
        Assert.Null(state.Vm.BeforeImage);
        Assert.Equal(version, Field(state.Vm, "_beforeImageGeneration"));
        Assert.Equal(1, state.Scope.Capture().TryRetainFailures);
        Assert.True(state.Scope.Capture().IsEmpty);
        Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
    }

    private static object? Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target);

    private sealed class State : IAsyncDisposable
    {
        private readonly Services _services = new();
        private readonly CancellationTokenSource _before = new();
        internal MainWindowViewModel Vm { get; }
        internal RawOwnershipScope Scope { get; }
        internal object Context { get; }
        internal PreviewRequest Request { get; } = new()
        { CorrelationId = -1, EncoderPath = "fake", InputFile = "input", TargetSeconds = 1, Fps = 2,
            TimestampOriginSeconds = 1000, Capabilities = new(TimestampPassthrough: true) };
        internal State()
        {
            Vm = new MainWindowViewModel(_services);
            Scope = (RawOwnershipScope)Field(Vm, "_rawOwnershipScope")!;
            Vm.CompareMode = PreviewCompareMode.Split;
            Set("_generation", 1L);
            var type = typeof(MainWindowViewModel).GetNestedType("RequestContext", BindingFlags.NonPublic)!;
            var ctor = type.GetConstructors(Flags).Single();
            Context = ctor.Invoke(ctor.GetParameters().Select(parameter => parameter.Name switch
            {
                "generation" => (object)1L, "request" => Request, "started" => 0L,
                "kind" => Enum.Parse(parameter.ParameterType, "Before"), "beforeCancellation" => _before,
                _ => parameter.DefaultValue,
            }).ToArray());
            Set("_beforeExecutionContext", Context);
            Set("_beforeCancellation", _before);
            Set("_beforeSelectionRequest", Request);
            Call("SetTimeOnly", 1d);
            Vm.IsBeforeBusy = true;
        }
        private void Set(string name, object value) => Vm.GetType().GetField(name, Flags)!.SetValue(Vm, value);
        internal object? Call(string name, params object[] args) => Vm.GetType().GetMethod(name, Flags)!.Invoke(Vm, args);
        internal RawFrameLease Frame(double seconds, byte pixel, double duration = .5)
            => Scope.Create(Header, 1000 + seconds, duration, new byte[] { pixel, pixel, pixel, pixel, 128, 128 }, Scope.NewRunId(), "TestReader");
        internal PreviewSegmentSnapshot Segment(IReadOnlyList<Y4mFrame> frames)
            => new(0, Request, new(1000, 0, true), frames, 0, 3, false, false) { CanStitch = false };
        internal void Ready(Y4mFrame frame, PreviewSegmentSnapshot segment, bool allowed = true)
            => Call("OnFrameReady", new PreviewFrameResult(Request, frame, false) { Segment = segment, CanDisplayBeforeEnd = allowed });
        internal void Complete(IReadOnlyList<Y4mFrame> frames, PreviewSegmentSnapshot? segment = null, string? reason = null)
            => Call("OnCompleted", new PreviewCompletion(Request, new(0, ""), false, false)
            { Frames = frames, Segment = segment ?? Segment(frames), EarlyDisplayFallbackReason = reason });
        internal async Task Pump()
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitWithDiagnosticsAsync(Safety);
            await Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        }
        public async ValueTask DisposeAsync()
        {
            await Vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
            Assert.True(Scope.Capture().IsEmpty);
            Assert.Equal(Scope.Capture().TotalRetains, Scope.Capture().TotalTokenReleases);
            _before.Dispose();
            _services.Dispose();
        }
    }
    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "before-early-" + Guid.NewGuid().ToString("N"));
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

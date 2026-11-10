using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class BeforeCompletionEvictionTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(15);

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EvictedBeforeCompletionSelectsLatestTargetWithoutRestoringWholeWindow(bool beforePost, bool untimed)
    {
        using var services = new Services();
        var cache = new FrameCache();
        await using var vm = new MainWindowViewModel(services, preferred: new UnusedBackend(), cache: cache);
        var request = new PreviewRequest { CorrelationId = -4, TargetSeconds = 0.5, Fps = 30,
            EncoderPath = "unused", InputFile = "before.ts", Capabilities = new(true, true), TimestampOriginSeconds = 0 };
        var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
        var frames = Enumerable.Range(0, 43).Select(i => new Y4mFrame(header, untimed ? null : i / 30.0,
            1.0 / 30, Enumerable.Repeat((byte)i, 6).ToArray())).ToArray();
        var stored = cache.StoreSegment(request, frames, new(0, 0, !untimed), 0, 1.4)!;
        // prefixを切ったno-Xts列でも、元番号を時計へ保持した完了通知を作る。
        var segment = untimed ? new PreviewFrameReference(stored, 6).RetainFrameOnly().Segment with
            { Frames = frames.Skip(6).ToArray() } : stored;
        object context = BeforeContext(vm, request, segment);
        SetField(vm, "_currentSeconds", 1d);
        vm.IsBeforeBusy = true;
        if (beforePost) cache.RemoveSegment(stored.SegmentId);
        Invoke(vm, "OnCompleted", new PreviewCompletion(request, new(0, ""), false, false)
            { Segment = segment, Frames = segment.Frames });
        if (!beforePost) cache.RemoveSegment(stored.SegmentId);
        await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.False(vm.IsBeforeBusy);
        Assert.NotNull(vm.BeforeImage);
        Assert.Same(frames[30], Field(vm, "_beforeDisplayedFrame"));
        var held = Assert.IsType<PreviewSegmentSnapshot>(Property(context, "Segment"));
        Assert.Equal(0, held.SegmentId);
        Assert.False(held.CanStitch);
        Assert.Same(frames[30], Assert.Single(held.Frames));
        Assert.Equal(1, held.Clock.ToInputSeconds(held.Frames[0], 0, 30), 8);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(Field(vm, "_beforeIntervalFrames")));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(Field(vm, "_intervalFrames")));
    }

    [AvaloniaTheory]
    [InlineData(2, 0)]
    [InlineData(0.1, 0.5)]
    public async Task MissingOrUnsafeLatestTargetFinishesBusyWithoutClaimingAnImage(double target, double safeStart)
    {
        using var services = new Services();
        var cache = new FrameCache();
        await using var vm = new MainWindowViewModel(services, preferred: new UnusedBackend(), cache: cache);
        var request = new PreviewRequest { CorrelationId = -4, TargetSeconds = 0.5, Fps = 30,
            EncoderPath = "unused", InputFile = "before.ts", Capabilities = new(true, true), TimestampOriginSeconds = 0 };
        var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
        var frames = Enumerable.Range(0, 43).Select(i => new Y4mFrame(header, i / 30.0, 1.0 / 30, new byte[6])).ToArray();
        var segment = cache.StoreSegment(request, frames, new(0, 0, true), safeStart, 1.4)!;
        object context = BeforeContext(vm, request, segment);
        SetField(vm, "_currentSeconds", target);
        // 既に回復済みの最新目標とし、既存のunavailable経路へ必ず着地させる。
        SetField(vm, "_beforeRecoverySelection", request with { TargetSeconds = target });
        vm.IsBeforeBusy = true;
        Invoke(vm, "OnCompleted", new PreviewCompletion(request, new(0, ""), false, false) { Segment = segment });
        cache.RemoveSegment(segment.SegmentId);
        await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.False(vm.IsBeforeBusy);
        Assert.Null(vm.BeforeImage);
        Assert.Null(Field(vm, "_beforeDisplayedFrame"));
        Assert.True(Assert.IsType<bool>(Field(vm, "_beforeFinished")));
        var held = Assert.IsType<PreviewSegmentSnapshot>(Property(context, "Segment"));
        Assert.Empty(held.Frames);
        Assert.Equal(0, held.SegmentId);
        Assert.False(held.CanStitch);
        Assert.Equal(safeStart, held.SafeStartSeconds);
        Assert.Equal(1.4, held.SafeEndSeconds);
    }

    private static object BeforeContext(MainWindowViewModel vm, PreviewRequest request, PreviewSegmentSnapshot segment)
    {
        var type = typeof(MainWindowViewModel).GetNestedType("RequestContext", BindingFlags.NonPublic)!;
        var kind = typeof(MainWindowViewModel).GetNestedType("RequestKind", BindingFlags.NonPublic)!;
        object context = type.GetConstructors(Flags).Single().Invoke(type.GetConstructors(Flags).Single().GetParameters()
            .Select(parameter => parameter.Name switch
            {
                "generation" => (object)1L, "request" => request, "started" => 0L,
                "kind" => Enum.Parse(kind, "Before"), _ => parameter.DefaultValue,
            }).ToArray());
        context.GetType().GetProperty("Segment")!.SetValue(context, segment);
        SetField(vm, "_generation", 1L);
        SetField(vm, "_beforeExecutionContext", context);
        return context;
    }
    private static object? Field(object value, string name) => value.GetType().GetField(name, Flags)!.GetValue(value);
    private static void SetField(object value, string name, object? field) => value.GetType().GetField(name, Flags)!.SetValue(value, field);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
    private static object? Invoke(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Flags)!.Invoke(value, arguments);

    private sealed class UnusedBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("保持寿命の試験では実行しない。");
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-retention-{Guid.NewGuid():N}");
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

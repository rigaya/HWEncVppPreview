using System.Collections;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class SnapshotRetentionViewModelTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly TimeSpan Safety = TestTimeouts.Hang;

    [AvaloniaFact]
    public async Task QueuedFrameReadyCannotRestoreAnObsoleteWholeSnapshot()
    {
        using var services = new Services();
        await using var vm = new MainWindowViewModel(services, preferred: new UnusedBackend());
        var request = Request();
        var frames = Enumerable.Range(0, 86).Select(Frame).ToArray();
        var segment = new PreviewSegmentSnapshot(0, request, new(0, 0, true), frames, 0, 3, false, false);
        object context = Register(vm, request, segment);
        // Postを保留したまま次の移動へ進む。旧画像1枚は許すが、区間86枚は復活させない。
        Invoke(vm, "OnFrameReady", new PreviewFrameResult(request, frames[30], false) { Segment = segment });
        Invoke(vm, "BeginIntent", true);
        Assert.Null(Property(context, "Segment"));
        await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitWithDiagnosticsAsync(Safety);
        var held = Assert.IsType<PreviewSegmentSnapshot>(Property(context, "Segment"));
        Assert.Same(frames[30], Assert.Single(held.Frames));
        Assert.Equal(segment.SafeStartSeconds, held.SafeStartSeconds);
        Assert.Equal(segment.SafeEndSeconds, held.SafeEndSeconds);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededUpdatesCannotRestoreTheWholeInterval(bool completion)
    {
        using var services = new Services();
        await using var vm = new MainWindowViewModel(services, preferred: new UnusedBackend());
        var request = Request();
        var frames = Enumerable.Range(0, 86).Select(Frame).ToArray();
        var segment = new PreviewSegmentSnapshot(0, request, new(0, 0, true), frames, 0, 3, false, false);
        object context = Register(vm, request, segment);
        Invoke(vm, "BeginIntent", true);
        if (completion)
            Invoke(vm, "OnCompleted", new PreviewCompletion(request, new(0, ""), false, false) { Segment = segment, Frames = frames });
        else Invoke(vm, "OnSegmentUpdated", new PreviewSegmentUpdate(request, segment, false, false));
        await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitWithDiagnosticsAsync(Safety);
        Assert.Null(Property(context, "Segment"));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(Field(vm, "_intervalFrames")));
    }

    [AvaloniaFact]
    public async Task RollingUpdateRefreshesAfterBeforeAndContextToTheCurrentCachePrefix()
    {
        using var services = new Services();
        var request = Request() with { TargetSeconds = 10 };
        var cache = new FrameCache(Frame(0).Data.Length * 4L);
        long id = cache.BeginSegment(request, new(0, 0, true), 0);
        cache.PinCurrent(id, 10);
        for (int i = 0; i < 4; i++) Assert.True(cache.Append(id, Frame(i)));
        var previous = cache.GetSegment(id)!;
        await using var vm = new MainWindowViewModel(services, preferred: new UnusedBackend(), cache: cache);
        object context = Register(vm, request, previous);
        SetField(vm, "_intervalRequest", request);
        SetField(vm, "_intervalSegment", previous);
        SetField(vm, "_intervalFrames", previous.Frames);
        SetField(vm, "_beforeIntervalRequest", request);
        SetField(vm, "_beforeIntervalSegment", previous);
        SetField(vm, "_beforeIntervalFrames", previous.Frames);
        for (int i = 4; i < 8; i++) Assert.True(cache.Append(id, Frame(i)));
        // UI待ちの間にrollingで解放したprefixを、古い通知から取り戻さない。
        Invoke(vm, "OnSegmentUpdated", new PreviewSegmentUpdate(request, previous, false, false));
        await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitWithDiagnosticsAsync(Safety);
        var current = cache.GetSegment(id)!;
        foreach (string name in new[] { "_intervalFrames", "_beforeIntervalFrames" })
            Assert.Equal(current.Frames, Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(Field(vm, name)));
        Assert.Equal(current.Frames, Assert.IsType<PreviewSegmentSnapshot>(Property(context, "Segment")).Frames);
        Assert.DoesNotContain(previous.Frames[0], current.Frames);
    }

    [AvaloniaFact]
    public async Task CacheEvictionReleasesHeldIntervalsWithoutLosingTheLastDisplayedFrame()
    {
        using var services = new Services();
        var request = Request();
        var cache = new FrameCache();
        var frames = Enumerable.Range(0, 8).Select(Frame).ToArray();
        var segment = cache.StoreSegment(request, frames, new(0, 0, true), 0, 3)!;
        await using var vm = new MainWindowViewModel(services, preferred: new UnusedBackend(), cache: cache);
        object context = Register(vm, request, segment);
        SetField(vm, "_intervalRequest", request);
        SetField(vm, "_intervalSegment", segment);
        SetField(vm, "_intervalFrames", segment.Frames);
        SetField(vm, "_beforeIntervalRequest", request);
        SetField(vm, "_beforeIntervalSegment", segment);
        SetField(vm, "_beforeIntervalFrames", segment.Frames);
        SetField(vm, "_displayedFrame", frames[3]);
        cache.RemoveSegment(segment.SegmentId);
        Invoke(vm, "ValidateNavigationState");
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(Field(vm, "_intervalFrames")));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(Field(vm, "_beforeIntervalFrames")));
        Assert.Empty(Assert.IsType<PreviewSegmentSnapshot>(Property(context, "Segment")).Frames);
        Assert.Same(frames[3], Field(vm, "_displayedFrame"));
    }

    [AvaloniaFact]
    public async Task DisposeReleasesTheCompletedContextEvenWhileTheViewModelIsStillReferenced()
    {
        using var services = new Services();
        var request = Request();
        var cache = new FrameCache();
        var frames = Enumerable.Range(0, 86).Select(Frame).ToArray();
        var segment = cache.StoreSegment(request, frames, new(0, 0, true), 0, 3)!;
        var vm = new MainWindowViewModel(services, preferred: new UnusedBackend(), cache: cache);
        object context = Register(vm, request, segment);
        SetField(vm, "_intervalSegment", segment);
        SetField(vm, "_intervalFrames", segment.Frames);
        SetField(vm, "_beforeIntervalSegment", segment);
        SetField(vm, "_beforeIntervalFrames", segment.Frames);
        await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        Assert.Null(Field(vm, "_context"));
        Assert.Null(Property(context, "Segment"));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable>(Field(vm, "_afterRequests")));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(Field(vm, "_intervalFrames")));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(Field(vm, "_beforeIntervalFrames")));
        Assert.Equal(0, cache.SizeBytes);
        GC.KeepAlive(vm);
    }

    private static PreviewRequest Request() => new()
    {
        CorrelationId = 1, TargetSeconds = 1, EncoderPath = "unused", InputFile = "retention.ts", Fps = 30,
        TimestampOriginSeconds = 0, Capabilities = new(true, true),
    };
    private static Y4mFrame Frame(int tick) => new(new Y4mHeader { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 },
        tick / 30.0, 1.0 / 30, new byte[6]);
    private static object Register(MainWindowViewModel vm, PreviewRequest request, PreviewSegmentSnapshot segment)
    {
        var type = typeof(MainWindowViewModel).GetNestedType("RequestContext", BindingFlags.NonPublic)!;
        var constructor = type.GetConstructors(Flags).Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "generation" => (object)request.CorrelationId, "request" => request, "started" => 0L,
            _ => parameter.DefaultValue,
        }).ToArray();
        object context = constructor.Invoke(arguments);
        context.GetType().GetProperty("Segment")!.SetValue(context, segment);
        SetField(vm, "_generation", request.CorrelationId);
        Invoke(vm, "RegisterContext", context);
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

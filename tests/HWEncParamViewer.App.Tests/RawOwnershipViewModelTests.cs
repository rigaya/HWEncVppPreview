using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.Tests;

public sealed class RawOwnershipViewModelTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2 };

    [AvaloniaTheory]
    [InlineData("default", true)]
    [InlineData("clock", true)]
    [InlineData("preferred", false)]
    [InlineData("fallback", false)]
    [InlineData("backgroundPreferred", false)]
    [InlineData("backgroundFallback", false)]
    [InlineData("probe", false)]
    [InlineData("cache", false)]
    [InlineData("converter", false)]
    public async Task raw注入だけがVM全体をlegacyへ固定する(string injected, bool owned)
    {
        using var services = new Services();
        await using var vm = new MainWindowViewModel(services, injected == "clock" ? TimeProvider.System : null,
            injected == "preferred" ? new UnusedBackend() : null,
            injected == "fallback" ? new UnusedBackend() : null,
            injected == "probe" ? new InputProbe() : null,
            injected == "cache" ? new FrameCache() : null,
            injected == "converter" ? (_, _) => Task.FromResult(new byte[16]) : null,
            injected == "backgroundPreferred" ? new UnusedBackend() : null,
            injected == "backgroundFallback" ? new UnusedBackend() : null, beforeEnabled: false);
        await using var ownership = new OwnedVmCheck(vm, requireOwned: owned);
        Assert.Equal(owned, Field(vm, "_usesOwnedRaw"));
        Assert.Equal(owned, Field(vm, "_rawOwnershipScope") != null);
        // legacyで通常配列を所有slotへ渡しても、raw tokenを追加しない。
        using var raw = (IDisposable?)InvokeStatic("RetainRawFrames", new[] { new Y4mFrame(Header, 1, 1, new byte[6]) }, "Test");
        Assert.Null(raw);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UI未実行のraw通知は世代切替とDisposeで失効する(bool dispose, bool detached)
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var context = Context(vm);
        var frame = scope.Create(Header, 1, 1, new byte[6], scope.NewRunId(), "TestReader");
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1 };
        context.GetType().GetProperty("Segment")!.SetValue(context,
            new PreviewSegmentSnapshot(0, request, new(0, 0, true), new[] { frame.Frame }, 1, 2, false, false));
        int calls = 0;
        Invoke(vm, "PostRaw", context, new[] { frame.Frame }, (Action)(() => ++calls));
        if (detached)
        {
            // Completedが通知対応表から外した直後に、次世代で現contextも置換された状態。
            var requests = Field(vm, "_afterRequests")!;
            requests.GetType().GetMethod("Clear")!.Invoke(requests, null);
            typeof(MainWindowViewModel).GetField("_context", Flags)!.SetValue(vm, null);
        }
        frame.Dispose();
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(1, scope.Capture().OwnerKinds["VmUi"]);
        if (dispose) await vm.DisposeAsync();
        else Invoke(vm, "BeginIntent", false);
        await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitAsync(Safety);
        Assert.Equal(0, calls);
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaFact]
    public async Task 引取済みUI処理は失効後も実完了までrawを保持する()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var context = Context(vm);
        var frame = scope.Create(Header, 1, 1, new byte[] { 17, 17, 17, 17, 128, 128 }, scope.NewRunId(), "TestReader");
        var descriptor = frame.Frame;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object post = Invoke(vm, "CaptureRawPost", context, new[] { descriptor }, (Action)(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            Assert.Equal(17, descriptor.RawPixels[0]);
        }))!;
        frame.Dispose();
        Task executing = Task.Run(() => Invoke(post, "Execute"));
        try
        {
            await entered.Task.WaitAsync(Safety);
            Invoke(vm, "BeginIntent", false);
            Assert.Equal(1, scope.Capture().LiveOwners);
        }
        finally
        {
            release.TrySetResult();
            await executing.WaitAsync(Safety);
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaFact]
    public async Task 変換からUIコピー実完了までrawを保持する()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var frame = scope.Create(Header, 1, 1, new byte[] { 17, 17, 17, 17, 128, 128 }, scope.NewRunId(), "TestReader");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task converting = vm.WithConvertedBgraAsync(frame.Frame, new(), CancellationToken.None, async pixels =>
        {
            Assert.Equal(16, pixels.Length);
            entered.TrySetResult();
            await release.Task;
        });
        try
        {
            await entered.Task.WaitAsync(Safety);
            frame.Dispose();
            Assert.Equal(1, scope.Capture().OwnerKinds["VmConversion"]);
            Assert.Equal(1, scope.Capture().LiveOwners);
        }
        finally
        {
            release.TrySetResult();
            await converting.WaitAsync(Safety);
            frame.Dispose();
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 画像workの完了と旧世代棄却でrawが回収される(bool supersede)
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        Context(vm);
        var frame = scope.Create(Header, 1, 1, new byte[] { 17, 17, 17, 17, 128, 128 }, scope.NewRunId(), "TestReader");
        object image = Image(frame.Frame);
        Invoke(vm, "EnqueueImage", image);
        frame.Dispose();
        if (supersede) Invoke(vm, "BeginIntent", false);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(supersede, vm.PreviewImage == null);
        if (supersede) Assert.True(scope.Capture().IsEmpty);
        else Assert.Equal(1, scope.Capture().OwnerKinds["VmDisplayed"]);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaFact]
    public async Task cache消去後も両表示のHDR再変換を保持しDisposeで回収する()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        Context(vm);
        var header = new Y4mHeader
        {
            Width = 2, Height = 2,
            ExtTags = new Dictionary<string, string> { ["XTRANSFER"] = "smpte2084", ["XCOLORMATRIX"] = "bt2020nc", ["XCOLORPRIM"] = "bt2020" },
        };
        var frame = scope.Create(header, 1, 1, new byte[] { 180, 180, 180, 180, 128, 128 }, scope.NewRunId(), "TestReader");
        byte[] pixels = new byte[16];
        YuvToBgra.ConvertInto(frame, pixels);
        using (var after = (IDisposable)Image(frame.Frame)) Invoke(vm, "ApplyImage", after, pixels);
        using (var before = (IDisposable)Image(frame.Frame, before: true)) Invoke(vm, "ApplyImage", before, pixels);
        var oldAfter = vm.PreviewImage;
        var oldBefore = vm.BeforeImage;
        Assert.NotNull(oldAfter);
        Assert.NotNull(oldBefore);
        frame.Dispose();
        ((FrameCache)Field(vm, "_cache")!).Clear();
        Assert.Equal(1, scope.Capture().OwnerKinds["VmDisplayed"]);
        Assert.Equal(1, scope.Capture().OwnerKinds["VmBeforeDisplayed"]);
        vm.HdrReferenceWhiteNits = 400;
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.NotSame(oldAfter, vm.PreviewImage);
        Assert.NotSame(oldBefore, vm.BeforeImage);
        byte[] expected = YuvToBgra.Convert(new Y4mFrame(header, 1, 1,
            new byte[] { 180, 180, 180, 180, 128, 128 }), new() { ReferenceWhiteNits = 400 });
        Assert.Equal(expected, Pixels(vm.PreviewImage!));
        Assert.Equal(expected, Pixels(vm.BeforeImage!));
        Assert.Equal(1, scope.Capture().LiveOwners);
        vm.BeforeImage = null;
        Assert.False(scope.Capture().OwnerKinds.ContainsKey("VmBeforeDisplayed"));
        vm.PreviewImage = null;
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaFact]
    public async Task contextとanchorの保持はVMを生かしたDispose後にも残さない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var context = Context(vm);
        var frames = Enumerable.Range(0, 4).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            new byte[6], scope.NewRunId(), "TestReader")).ToArray();
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1 };
        var segment = new PreviewSegmentSnapshot(0, request, new(0, 0, true), frames.Select(frame => frame.Frame).ToArray(),
            1, 1.2, false, false);
        context.GetType().GetProperty("Segment")!.SetValue(context, segment);
        Invoke(vm, "RememberNavigationAnchor", frames[0].Frame, context);
        foreach (var frame in frames) frame.Dispose();
        Assert.Equal(4, scope.Capture().LiveOwners);
        Assert.Equal(4, scope.Capture().OwnerKinds["VmId0Window"]);
        Assert.Equal(1, scope.Capture().OwnerKinds["VmAnchor"]);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
        GC.KeepAlive(vm);
    }

    [AvaloniaFact]
    public async Task 最新segment通知への置換は旧pendingのrawを即時解放する()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        Context(vm);
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1 };
        for (int index = 0; index < 2; ++index)
        {
            using var frame = scope.Create(Header, 1, 1, new byte[6], scope.NewRunId(), "TestReader");
            var segment = new PreviewSegmentSnapshot(0, request, new(0, 0, true), new[] { frame.Frame }, 1, 2, false, false);
            using var notification = new OwnedPreviewEnvelope(update: new(request, segment, false, false));
            Invoke(vm, "OnOwnedSegmentUpdated", notification);
        }
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(1, scope.Capture().FinalReleases);
        await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitAsync(Safety);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaFact]
    public async Task Replayは旧UI通知を実行前に失効させる()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var context = Context(vm);
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1 };
        var frame = scope.Create(Header, 1, 1, new byte[6], scope.NewRunId(), "TestReader");
        int calls = 0;
        Invoke(vm, "PostRaw", context, new[] { frame.Frame }, (Action)(() => ++calls));
        frame.Dispose();
        Invoke(vm, "OnReplayStarted", request);
        Assert.True(scope.Capture().IsEmpty);
        await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitAsync(Safety);
        Assert.Equal(0, calls);
    }

    [AvaloniaFact]
    public async Task Before選択はnavigation読取を閉じた後のcache消去にも耐える()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = 1, TimestampOriginSeconds = 0,
            Capabilities = new(TimestampPassthrough: true),
        };
        var frame = scope.Create(Header, 1, 1, new byte[] { 17, 17, 17, 17, 128, 128 }, scope.NewRunId(), "TestReader");
        var segment = cache.StoreSegment(request, new[] { frame.Frame }, new(0, 0, true), 1, 1.1)!;
        frame.Dispose();
        typeof(MainWindowViewModel).GetField("_beforeIntervalSegment", Flags)!.SetValue(vm, segment);
        object?[] arguments = { request, segment.Frames, null };
        var selected = (Y4mFrame)typeof(MainWindowViewModel).GetMethod("SelectBeforeFrame", Flags)!.Invoke(vm, arguments)!;
        using var selection = (IDisposable)arguments[2]!;
        cache.Clear();
        Assert.Equal(1, scope.Capture().OwnerKinds["VmBeforeSelection"]);
        Assert.Equal(1, scope.Capture().LiveOwners);
        await vm.WithConvertedBgraAsync(selected, new(), CancellationToken.None, pixels =>
        {
            Assert.Equal(YuvToBgra.Convert(new Y4mFrame(Header, 1, 1,
                new byte[] { 17, 17, 17, 17, 128, 128 })), pixels);
            return Task.CompletedTask;
        }).WaitAsync(Safety);
        selection.Dispose();
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaFact]
    public async Task cacheの存在確認は反復してもraw参照と取得試行を増やさない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = 1, TimestampOriginSeconds = 0,
            Capabilities = new(TimestampPassthrough: true),
        };
        using var producer = scope.Create(Header, 1, 1, new byte[6], scope.NewRunId(), "TestReader");
        var segment = cache.StoreSegment(request, new[] { producer.Frame }, new(0, 0, true), 1, 1.1)!;
        Assert.True(segment.SegmentId > 0);
        producer.Dispose();
        var before = scope.Capture();
        Assert.True(before.LiveTokens > 0);
        long useSequence = (long)Field(cache, "_useSequence")!;
        for (int i = 0; i < 512; ++i)
        {
            Assert.True((bool)Invoke(vm, "CacheHasSegment", segment.SegmentId)!);
            Assert.False((bool)Invoke(vm, "CacheHasSegment", long.MaxValue)!);
        }
        var after = scope.Capture();
        Assert.Equal(before.TotalRetains, after.TotalRetains);
        Assert.Equal(before.TryRetainAttempts, after.TryRetainAttempts);
        Assert.Equal(before.LiveTokens, after.LiveTokens);
        Assert.Equal(useSequence, (long)Field(cache, "_useSequence")!);
        cache.Clear();
        Assert.False((bool)Invoke(vm, "CacheHasSegment", segment.SegmentId)!);
        Assert.Equal(after.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(after.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        var final = scope.Capture();
        Assert.True(final.IsEmpty);
        Assert.Equal(0, final.LiveBytes);
        Assert.Equal(final.TotalRetains, final.TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task 正IDcontextの設定とrefreshはmetadataだけを保持する()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var context = Context(vm);
        var property = context.GetType().GetProperty("Segment")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = 1, Fps = 30, TimestampOriginSeconds = 0,
            Capabilities = new(TimestampPassthrough: true),
        };
        using var producer = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var descriptor = producer.Frame;
        var segment = cache.StoreSegment(request, new[] { descriptor }, new(0, 0, true), 1, 1.1)!;
        Assert.True(segment.SegmentId > 0);
        producer.Dispose();
        var before = scope.Capture();
        for (int i = 0; i < 32; ++i)
        {
            property.SetValue(context, segment with { Frames = segment.Frames.ToArray() });
            Invoke(vm, "RefreshRetainedSnapshots");
        }
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(before.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        var metadata = Assert.IsType<PreviewSegmentSnapshot>(property.GetValue(context));
        Assert.Same(descriptor, Assert.Single(metadata.Frames));
        cache.Clear();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(1d, metadata.Frames[0].Xts!.Value);
        Assert.Equal(1d / 30, metadata.Frames[0].Xdur!.Value);
        Assert.Equal(2, metadata.Frames[0].Header.Width);
        Assert.Equal(descriptor.FrameId, metadata.Frames[0].FrameId);
        Assert.Throws<ObjectDisposedException>(() => descriptor.RawPixels);
        Invoke(vm, "RefreshRetainedSnapshots");
        var invalidated = Assert.IsType<PreviewSegmentSnapshot>(property.GetValue(context));
        Assert.Empty(invalidated.Frames);
        Assert.Equal(segment.SafeStartSeconds, invalidated.SafeStartSeconds);
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaFact]
    public async Task ID0contextの同一窓再設定は共有しsubsetだけを独立保持する()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var context = Context(vm);
        var property = context.GetType().GetProperty("Segment")!;
        var producers = Enumerable.Range(0, 4).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            new byte[6], scope.NewRunId(), "TestReader")).ToArray();
        var descriptors = producers.Select(frame => frame.Frame).ToArray();
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1 };
        var segment = new PreviewSegmentSnapshot(0, request, new(0, 0, true), descriptors, 1, 1.2, false, false);
        long before = scope.Capture().TotalRetains;
        property.SetValue(context, segment);
        Assert.Equal(before + 4, scope.Capture().TotalRetains);
        foreach (var producer in producers) producer.Dispose();
        long retained = scope.Capture().TotalRetains;
        for (int i = 0; i < 64; ++i)
        {
            property.SetValue(context, segment with { Frames = descriptors.ToArray() });
            var metadata = Assert.IsType<PreviewSegmentSnapshot>(property.GetValue(context));
            for (int index = 0; index < descriptors.Length; ++index)
                Assert.Same(descriptors[index], metadata.Frames[index]);
        }
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.Equal(4, scope.Capture().OwnerKinds["VmId0Window"]);
        property.SetValue(context, segment with { Frames = new[] { descriptors[3] } });
        Assert.Equal(retained + 1, scope.Capture().TotalRetains);
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(1, scope.Capture().OwnerKinds["VmId0Window"]);
        Assert.Throws<ObjectDisposedException>(() => descriptors[0].RawPixels);
        Assert.Equal(6, descriptors[3].RawPixels.Length);
        property.SetValue(context, null);
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task ID0contextの設定失敗は旧窓を維持し途中取得を返す()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var context = Context(vm);
        var property = context.GetType().GetProperty("Segment")!;
        using var first = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        using var second = scope.Create(Header, 2, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var firstDescriptor = first.Frame;
        var secondDescriptor = second.Frame;
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1 };
        property.SetValue(context, new PreviewSegmentSnapshot(0, request, new(0, 0, true),
            new[] { firstDescriptor, secondDescriptor }, 1, 3, false, false));
        first.Dispose();
        second.Dispose();
        var saved = property.GetValue(context);
        using var expired = scope.Create(Header, 3, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var expiredDescriptor = expired.Frame;
        expired.Dispose();
        var before = scope.Capture();
        var failure = Assert.Throws<TargetInvocationException>(() => property.SetValue(context,
            new PreviewSegmentSnapshot(0, request, new(0, 0, true), new[] { firstDescriptor, expiredDescriptor },
                1, 4, false, false)));
        Assert.IsType<ObjectDisposedException>(failure.InnerException);
        Assert.Same(saved, property.GetValue(context));
        Assert.Equal(before.LiveOwners, scope.Capture().LiveOwners);
        Assert.Equal(before.LiveTokens, scope.Capture().LiveTokens);
        Assert.Equal(2, scope.Capture().OwnerKinds["VmId0Window"]);
        Assert.Equal(6, firstDescriptor.RawPixels.Length);
        Assert.Equal(6, secondDescriptor.RawPixels.Length);
        property.SetValue(context, null);
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        var final = scope.Capture();
        Assert.True(final.IsEmpty);
        Assert.Equal(final.TotalRetains, final.TotalTokenReleases);
        Assert.Equal(final.NewOwners, final.FinalReleases);
    }

    [AvaloniaFact]
    public async Task cache表示の要求がない場合は処理済みとして取得も再要求もしない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        using var producer = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var descriptor = producer.Frame;
        producer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => descriptor.RawPixels);
        Assert.Null(Field(vm, "_intervalRequest"));
        var context = Field(vm, "_context");
        var generation = Field(vm, "_generation");
        var queued = Field(vm, "_lastQueuedAfter");
        double seconds = vm.CurrentSeconds;
        var before = scope.Capture();
        Assert.True((bool)Invoke(vm, "DisplayCached", descriptor, 2d, 1, null!)!);
        Assert.Equal(before.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Same(context, Field(vm, "_context"));
        Assert.Equal(generation, Field(vm, "_generation"));
        Assert.Equal(queued, Field(vm, "_lastQueuedAfter"));
        Assert.Equal(seconds, vm.CurrentSeconds);
    }

    [AvaloniaFact]
    public async Task 失効したcache表示の取得失敗はnavigation状態をcommitしない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var context = Context(vm);
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1, Fps = 30 };
        using var producer = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var descriptor = producer.Frame;
        producer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => descriptor.RawPixels);
        var segment = new PreviewSegmentSnapshot(7, request, new(0, 0, false), new[] { descriptor }, 1, 2, false, false);
        var anchor = new PreviewFrameReference(segment, 0);
        typeof(MainWindowViewModel).GetField("_intervalRequest", Flags)!.SetValue(vm, request);
        typeof(MainWindowViewModel).GetField("_intervalSegment", Flags)!.SetValue(vm, segment);
        typeof(MainWindowViewModel).GetField("_navigationAnchor", Flags)!.SetValue(vm, anchor);
        Invoke(vm, "SetTimeOnly", 1d);
        string[] fields = { "_generation", "_pendingAfterImage", "_pendingBeforeImage", "_conversionActive",
            "_pendingNeighbor", "_lastIntentTarget", "_lastQueuedAfter", "_beforeImageGeneration" };
        var saved = fields.Select(name => Field(vm, name)).ToArray();
        int Count(string name) => (int)Field(vm, name)!.GetType().GetProperty("Count")!.GetValue(Field(vm, name))!;
        int tasks = Count("_tasks"), posts = Count("_rawPosts");
        var image = vm.PreviewImage;
        var beforeImage = vm.BeforeImage;
        var before = scope.Capture();
        Assert.False((bool)Invoke(vm, "DisplayCached", descriptor, 2d, 1, null!)!);
        for (int i = 0; i < fields.Length; ++i) Assert.Equal(saved[i], Field(vm, fields[i]));
        Assert.Equal(1d, vm.CurrentSeconds);
        Assert.Same(anchor, Field(vm, "_navigationAnchor"));
        Assert.Same(context, Field(vm, "_context"));
        Assert.Same(segment, Field(vm, "_intervalSegment"));
        Assert.Same(image, vm.PreviewImage);
        Assert.Same(beforeImage, vm.BeforeImage);
        Assert.Equal(tasks, Count("_tasks"));
        Assert.Equal(posts, Count("_rawPosts"));
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(before.TryRetainAttempts + 1, scope.Capture().TryRetainAttempts);
        Assert.Equal(before.TryRetainFailures + 1, scope.Capture().TryRetainFailures);
        Assert.Equal(1, scope.Capture().TryRetainsByCaller["VmStepSelection"].Failures);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task 失効した隣frameの取得失敗は保留とcontextを確定しない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var context = Context(vm, neighborDirection: 1);
        using var first = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        using var second = scope.Create(Header, 1 + 1d / 30, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var frames = new[] { first.Frame, second.Frame };
        first.Dispose();
        second.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frames[0].RawPixels);
        Assert.Throws<ObjectDisposedException>(() => frames[1].RawPixels);
        var request = (PreviewRequest)context.GetType().GetProperty("Request")!.GetValue(context)!;
        var segment = new PreviewSegmentSnapshot(8, request, new(0, 0, false), frames, 1, 1.1, false, false);
        context.GetType().GetProperty("Segment")!.SetValue(context, segment);
        var pendingType = typeof(MainWindowViewModel).GetNestedType("NeighborIntent", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(pendingType, Flags, null, new object[] { 1L, 1 }, null)!;
        typeof(MainWindowViewModel).GetField("_pendingNeighbor", Flags)!.SetValue(vm, pending);
        var anchor = new PreviewFrameReference(segment, 0);
        typeof(MainWindowViewModel).GetField("_navigationAnchor", Flags)!.SetValue(vm, anchor);
        Invoke(vm, "SetTimeOnly", 1d);
        var before = scope.Capture();
        var generation = Field(vm, "_generation");
        var savedSegment = context.GetType().GetProperty("Segment")!.GetValue(context);
        Assert.Null(Invoke(vm, "CompleteNeighbor", context, frames));
        Assert.Same(pending, Field(vm, "_pendingNeighbor"));
        Assert.Same(context, Field(vm, "_context"));
        Assert.Same(savedSegment, context.GetType().GetProperty("Segment")!.GetValue(context));
        Assert.False((bool)context.GetType().GetProperty("NeighborResolved")!.GetValue(context)!);
        Assert.Null(context.GetType().GetProperty("ResolvedTargetSeconds")!.GetValue(context));
        Assert.Equal(1d, vm.CurrentSeconds);
        Assert.Equal(generation, Field(vm, "_generation"));
        Assert.Same(anchor, Field(vm, "_navigationAnchor"));
        Assert.Null(vm.PreviewImage);
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(before.TryRetainAttempts + 1, scope.Capture().TryRetainAttempts);
        Assert.Equal(before.TryRetainFailures + 1, scope.Capture().TryRetainFailures);
        Assert.Equal(1, scope.Capture().TryRetainsByCaller["VmNeighborSelection"].Failures);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task 失効したBefore選択は所有も表示状態も返さない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        Context(vm);
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1, Fps = 30 };
        using var producer = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var descriptor = producer.Frame;
        producer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => descriptor.RawPixels);
        var segment = new PreviewSegmentSnapshot(9, request, new(0, 0, false), new[] { descriptor }, 1, 1.1, false, false);
        typeof(MainWindowViewModel).GetField("_beforeIntervalSegment", Flags)!.SetValue(vm, segment);
        var before = scope.Capture();
        var generation = Field(vm, "_generation");
        var beforeGeneration = Field(vm, "_beforeImageGeneration");
        var image = vm.PreviewImage;
        var beforeImage = vm.BeforeImage;
        object?[] arguments = { request, segment.Frames, null };
        Assert.Null(typeof(MainWindowViewModel).GetMethod("SelectBeforeFrame", Flags)!.Invoke(vm, arguments));
        Assert.Null(arguments[2]);
        Assert.Same(segment, Field(vm, "_beforeIntervalSegment"));
        Assert.Equal(generation, Field(vm, "_generation"));
        Assert.Equal(beforeGeneration, Field(vm, "_beforeImageGeneration"));
        Assert.Same(image, vm.PreviewImage);
        Assert.Same(beforeImage, vm.BeforeImage);
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(before.TryRetainAttempts + 1, scope.Capture().TryRetainAttempts);
        Assert.Equal(before.TryRetainFailures + 1, scope.Capture().TryRetainFailures);
        Assert.Equal(1, scope.Capture().TryRetainsByCaller["VmBeforeSelection"].Failures);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 隣の安全域外かtrusted候補消失なら取得せず保留だけ解除する(bool trusted)
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var context = Context(vm, neighborDirection: 1);
        using var first = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        using var second = scope.Create(Header, 1 + 1d / 30, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var frames = new[] { first.Frame, second.Frame };
        first.Dispose();
        second.Dispose();
        var request = (PreviewRequest)context.GetType().GetProperty("Request")!.GetValue(context)!;
        var segment = new PreviewSegmentSnapshot(10, request, new(0, 0, trusted), frames, 1, 1.01, false, false);
        context.GetType().GetProperty("Segment")!.SetValue(context, segment);
        var pendingType = typeof(MainWindowViewModel).GetNestedType("NeighborIntent", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(pendingType, Flags, null, new object[] { 1L, 1 }, null)!;
        typeof(MainWindowViewModel).GetField("_pendingNeighbor", Flags)!.SetValue(vm, pending);
        Invoke(vm, "SetTimeOnly", 1d);
        var before = scope.Capture();
        var savedSegment = context.GetType().GetProperty("Segment")!.GetValue(context);
        Assert.Null(Invoke(vm, "CompleteNeighbor", context, frames));
        Assert.Null(Field(vm, "_pendingNeighbor"));
        Assert.Same(context, Field(vm, "_context"));
        Assert.Same(savedSegment, context.GetType().GetProperty("Segment")!.GetValue(context));
        Assert.False((bool)context.GetType().GetProperty("NeighborResolved")!.GetValue(context)!);
        Assert.Null(context.GetType().GetProperty("ResolvedTargetSeconds")!.GetValue(context));
        Assert.Equal(1d, vm.CurrentSeconds);
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(before.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        Assert.Equal(before.TryRetainFailures, scope.Capture().TryRetainFailures);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task 正IDの両interval設定とrefreshはraw参照を増やさない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = 1, Fps = 30, TimestampOriginSeconds = 0,
            Capabilities = new(TimestampPassthrough: true),
        };
        using var producer = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var descriptor = producer.Frame;
        var segment = cache.StoreSegment(request, new[] { descriptor }, new(0, 0, true), 1, 1.1)!;
        producer.Dispose();
        var before = scope.Capture();
        for (int i = 0; i < 32; ++i)
        {
            typeof(MainWindowViewModel).GetField("_intervalSegment", Flags)!.SetValue(vm, segment);
            Invoke(vm, "SetIntervalFrames", segment.Frames.ToArray(), null!);
            typeof(MainWindowViewModel).GetField("_beforeIntervalSegment", Flags)!.SetValue(vm, segment);
            Invoke(vm, "SetBeforeIntervalFrames", segment.Frames.ToArray(), null!, null!);
            Invoke(vm, "RefreshRetainedSnapshots");
        }
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(before.LiveTokens, scope.Capture().LiveTokens);
        Assert.Equal(before.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        var afterMetadata = (IReadOnlyList<Y4mFrame>)Field(vm, "_intervalFrames")!;
        var beforeMetadata = (IReadOnlyList<Y4mFrame>)Field(vm, "_beforeIntervalFrames")!;
        Assert.Same(descriptor, Assert.Single(afterMetadata));
        Assert.Same(descriptor, Assert.Single(beforeMetadata));
        cache.Clear();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(1d, afterMetadata[0].Xts!.Value);
        Assert.Equal(1d / 30, beforeMetadata[0].Xdur!.Value);
        Assert.Equal(2, afterMetadata[0].Header.Width);
        Assert.Equal(descriptor.FrameId, beforeMetadata[0].FrameId);
        Assert.Throws<ObjectDisposedException>(() => descriptor.RawPixels);
        Invoke(vm, "RefreshRetainedSnapshots");
        Assert.Empty((IReadOnlyList<Y4mFrame>)Field(vm, "_intervalFrames")!);
        Assert.Empty((IReadOnlyList<Y4mFrame>)Field(vm, "_beforeIntervalFrames")!);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaFact]
    public async Task ID0のAfter窓再設定とBefore共有は一回分だけ保持する()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var producers = Enumerable.Range(0, 4).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            new byte[6], scope.NewRunId(), "TestReader")).ToArray();
        var frames = producers.Select(frame => frame.Frame).ToArray();
        var request = new PreviewRequest { TargetSeconds = 1, Fps = 30 };
        var segment = new PreviewSegmentSnapshot(0, request, new(0, 0, true), frames, 1, 1.2, false, false);
        typeof(MainWindowViewModel).GetField("_intervalSegment", Flags)!.SetValue(vm, segment);
        long initial = scope.Capture().TotalRetains;
        Invoke(vm, "SetIntervalFrames", frames, null!);
        Assert.Equal(initial + 4, scope.Capture().TotalRetains);
        foreach (var producer in producers) producer.Dispose();
        long retained = scope.Capture().TotalRetains;
        for (int i = 0; i < 64; ++i)
        {
            var copied = segment with { Frames = frames.ToArray() };
            typeof(MainWindowViewModel).GetField("_intervalSegment", Flags)!.SetValue(vm, copied);
            Invoke(vm, "SetIntervalFrames", copied.Frames, null!);
        }
        typeof(MainWindowViewModel).GetField("_beforeIntervalSegment", Flags)!.SetValue(vm, segment);
        Invoke(vm, "SetBeforeIntervalFrames", frames.ToArray(), null!, Field(vm, "_intervalRaw")!);
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.Equal(4, scope.Capture().LiveTokens);
        var beforeFrames = (IReadOnlyList<Y4mFrame>)Field(vm, "_beforeIntervalFrames")!;
        for (int i = 0; i < frames.Length; ++i) Assert.Same(frames[i], beforeFrames[i]);
        Invoke(vm, "ClearInterval");
        Assert.Equal(4, scope.Capture().LiveOwners);
        Assert.Equal(4, scope.Capture().LiveTokens);
        Assert.All(beforeFrames, frame => Assert.Equal(6, frame.RawPixels.Length));
        Invoke(vm, "ClearBeforeInterval");
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(retained, scope.Capture().TotalRetains);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task ID0のintervalを一枚subsetへ替えると親全窓を保持しない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var producers = Enumerable.Range(0, 4).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            new byte[6], scope.NewRunId(), "TestReader")).ToArray();
        var frames = producers.Select(frame => frame.Frame).ToArray();
        var request = new PreviewRequest { TargetSeconds = 1, Fps = 30 };
        var segment = new PreviewSegmentSnapshot(0, request, new(0, 0, true), frames, 1, 1.2, false, false);
        typeof(MainWindowViewModel).GetField("_intervalSegment", Flags)!.SetValue(vm, segment);
        Invoke(vm, "SetIntervalFrames", frames, null!);
        foreach (var producer in producers) producer.Dispose();
        long retained = scope.Capture().TotalRetains;
        var subset = segment with { Frames = new[] { frames[3] } };
        typeof(MainWindowViewModel).GetField("_intervalSegment", Flags)!.SetValue(vm, subset);
        Invoke(vm, "SetIntervalFrames", subset.Frames, null!);
        Assert.Equal(retained + 1, scope.Capture().TotalRetains);
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(1, scope.Capture().LiveTokens);
        Assert.Same(frames[3], Assert.Single((IReadOnlyList<Y4mFrame>)Field(vm, "_intervalFrames")!));
        for (int i = 0; i < 3; ++i) Assert.Throws<ObjectDisposedException>(() => frames[i].RawPixels);
        Assert.Equal(6, frames[3].RawPixels.Length);
        Invoke(vm, "ClearInterval");
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task ID0のintervalからcontextへ窓共有するとAfter消去後も保持する()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var context = Context(vm);
        using var producer = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var descriptor = producer.Frame;
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1, Fps = 30 };
        var segment = new PreviewSegmentSnapshot(0, request, new(0, 0, true), new[] { descriptor }, 1, 1.1, false, false);
        typeof(MainWindowViewModel).GetField("_intervalSegment", Flags)!.SetValue(vm, segment);
        Invoke(vm, "SetIntervalFrames", segment.Frames, null!);
        producer.Dispose();
        long retained = scope.Capture().TotalRetains;
        Invoke(context, "SetSegmentSharing", segment with { Frames = segment.Frames.ToArray() }, Field(vm, "_intervalRaw")!);
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Invoke(vm, "ClearInterval");
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(1, scope.Capture().LiveTokens);
        var metadata = Assert.IsType<PreviewSegmentSnapshot>(context.GetType().GetProperty("Segment")!.GetValue(context));
        Assert.Same(descriptor, Assert.Single(metadata.Frames));
        Assert.Equal(6, descriptor.RawPixels.Length);
        context.GetType().GetProperty("Segment")!.SetValue(context, null);
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(retained, scope.Capture().TotalRetains);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task legacyのnull区間も共有と消去で配列identityを変えない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        byte[] pixels = new byte[6];
        var descriptor = new Y4mFrame(Header, 1, 1d / 30, pixels);
        Invoke(vm, "SetIntervalFrames", new[] { descriptor }, null!);
        long retained = scope.Capture().TotalRetains;
        for (int i = 0; i < 8; ++i) Invoke(vm, "SetIntervalFrames", new[] { descriptor }, null!);
        Invoke(vm, "SetBeforeIntervalFrames", new[] { descriptor }, null!, Field(vm, "_intervalRaw")!);
        Invoke(vm, "ClearInterval");
        var before = (IReadOnlyList<Y4mFrame>)Field(vm, "_beforeIntervalFrames")!;
        Assert.Same(descriptor, Assert.Single(before));
        Assert.Same(pixels, before[0].Data);
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.Equal(0, scope.Capture().NewOwners);
        Invoke(vm, "ClearBeforeInterval");
        Assert.Same(pixels, descriptor.Data);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaFact]
    public async Task Before完了用cache読取は二枚の既存所有をnull窓へ一度だけ移す()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = 1, Fps = 30, PreMarginSeconds = 1, PostMarginSeconds = 0.1,
            TimestampOriginSeconds = 0, Capabilities = new(TimestampPassthrough: true),
        };
        var context = Context(vm, request: request);
        Invoke(vm, "SetTimeOnly", 1d);
        byte[] pixels = { 17, 17, 17, 17, 128, 128 };
        using var first = scope.Create(Header, 1, 1d / 30, pixels, scope.NewRunId(), "TestReader");
        using var second = scope.Create(Header, 1 + 1d / 30, 1d / 30,
            new byte[] { 33, 33, 33, 33, 128, 128 }, scope.NewRunId(), "TestReader");
        var frames = new[] { first.Frame, second.Frame };
        cache.StoreSegment(request, frames, new(0, 0, true), 1, 1.1);
        first.Dispose();
        second.Dispose();
        long retained = scope.Capture().TotalRetains;
        object?[] arguments = { request, null, true };
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryGetBeforeInterval", Flags)!.Invoke(vm, arguments)!);
        var heldFrames = Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(arguments[1]);
        Assert.Equal(retained + 2, scope.Capture().TotalRetains);
        Assert.Equal(2, scope.Capture().OwnerKinds["VmId0BeforeWindow"]);
        cache.Clear();
        Assert.Equal(2, scope.Capture().LiveOwners);
        Assert.Equal(2, scope.Capture().LiveTokens);
        for (int i = 0; i < frames.Length; ++i)
        {
            Assert.Same(frames[i], heldFrames[i]);
            Assert.Equal(6, heldFrames[i].RawPixels.Length);
        }
        Assert.Null(context.GetType().GetProperty("Segment")!.GetValue(context));
        long beforeComplete = scope.Capture().TotalRetains;
        Invoke(vm, "CompleteBefore", context, heldFrames);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Null(Field(vm, "_beforeIntervalSegment"));
        var metadata = (IReadOnlyList<Y4mFrame>)Field(vm, "_beforeIntervalFrames")!;
        Assert.Equal(frames.Length, metadata.Count);
        for (int i = 0; i < frames.Length; ++i) Assert.Same(frames[i], metadata[i]);
        Assert.Same(frames[0], Field(vm, "_beforeDisplayedFrame"));
        Assert.Equal(YuvToBgra.Convert(new Y4mFrame(Header, 1, 1d / 30, pixels)), Pixels(vm.BeforeImage!));
        // 選択1、work1、変換保護2、表示1だけ。窓二枚の再Captureは増やさない。
        Assert.Equal(beforeComplete + 5, scope.Capture().TotalRetains);
        Assert.Equal(1, scope.Capture().TryRetainsByCaller["VmBeforeSelection"].Successes);
        Assert.Equal(2, scope.Capture().OwnerKinds["VmId0BeforeWindow"]);
        Invoke(vm, "ClearBeforeInterval");
        Assert.Equal(1, scope.Capture().LiveOwners);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task Before完了窓の再取得は包含失敗でslotを変えず成功時だけ移譲する()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            TargetSeconds = 1, Fps = 30, PreMarginSeconds = 1, PostMarginSeconds = 0.1,
            TimestampOriginSeconds = 0, Capabilities = new(TimestampPassthrough: true),
        };
        using var previous = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var previousFrame = previous.Frame;
        Invoke(vm, "SetBeforeIntervalFrames", new[] { previousFrame }, null!, null!);
        previous.Dispose();
        using var first = scope.Create(Header, 1, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        using var second = scope.Create(Header, 1 + 1d / 30, 1d / 30, new byte[6], scope.NewRunId(), "TestReader");
        var frames = new[] { first.Frame, second.Frame };
        var segment = cache.StoreSegment(request, frames, new(0, 0, true), 1, 1.1)!;
        first.Dispose();
        second.Dispose();
        var oldSlot = Field(vm, "_beforeIntervalRaw");
        var oldFrames = Field(vm, "_beforeIntervalFrames");
        var before = scope.Capture();
        Assert.False((bool)Invoke(vm, "TryAdoptBeforeCompletion", long.MaxValue, request, 1d)!);
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.False((bool)Invoke(vm, "TryAdoptBeforeCompletion", segment.SegmentId, request, 100d)!);
        Assert.Same(oldSlot, Field(vm, "_beforeIntervalRaw"));
        Assert.Same(oldFrames, Field(vm, "_beforeIntervalFrames"));
        Assert.Equal(before.LiveTokens, scope.Capture().LiveTokens);
        Assert.Equal(before.TotalRetains + 2, scope.Capture().TotalRetains);
        long retained = scope.Capture().TotalRetains;
        Assert.True((bool)Invoke(vm, "TryAdoptBeforeCompletion", segment.SegmentId, request, 1d)!);
        Assert.Equal(retained + 2, scope.Capture().TotalRetains);
        Assert.NotSame(oldSlot, Field(vm, "_beforeIntervalRaw"));
        Assert.Throws<ObjectDisposedException>(() => previousFrame.RawPixels);
        cache.Clear();
        Assert.Equal(2, scope.Capture().LiveOwners);
        Assert.Equal(2, scope.Capture().LiveTokens);
        var current = (IReadOnlyList<Y4mFrame>)Field(vm, "_beforeIntervalFrames")!;
        for (int i = 0; i < frames.Length; ++i)
        {
            Assert.Same(frames[i], current[i]);
            Assert.Equal(6, current[i].RawPixels.Length);
        }
        Invoke(vm, "ClearBeforeInterval");
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task 正IDのnavigation候補取得はrawを保持せずcache消去後もmetadataだけ残す()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = 1, Fps = 30, TimestampOriginSeconds = 0,
            Capabilities = new(TimestampPassthrough: true),
        };
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, 3).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            new byte[6], run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = cache.StoreSegment(request, frames, new(0, 0, true), 1, 1.2)!;
        foreach (var producer in producers) producer.Dispose();
        var before = scope.Capture();
        IReadOnlyList<PreviewFrameReference> metadata = Array.Empty<PreviewFrameReference>();
        for (int i = 0; i < 32; ++i)
        {
            metadata = (IReadOnlyList<PreviewFrameReference>)Invoke(vm, "NavigationFrames", request, segment)!;
            Assert.Equal(frames.Length, metadata.Count);
            for (int index = 0; index < frames.Length; ++index) Assert.Same(frames[index], metadata[index].Frame);
        }
        var after = scope.Capture();
        Assert.Equal(before.TotalRetains, after.TotalRetains);
        Assert.Equal(before.TryRetainAttempts, after.TryRetainAttempts);
        Assert.Equal(before.LiveTokens, after.LiveTokens);
        cache.Clear();
        Assert.True(scope.Capture().IsEmpty);
        for (int index = 0; index < metadata.Count; ++index)
        {
            Assert.Equal(1 + index / 30d, metadata[index].InputSeconds, 8);
            Assert.Equal(frames[index].FrameId, metadata[index].Frame.FrameId);
            Assert.Equal(2, metadata[index].Frame.Header.Width);
            Assert.Throws<ObjectDisposedException>(() => metadata[index].Frame.RawPixels);
        }
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task ID0の照合禁止navigation候補は有限窓の所有を取り直さない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var request = new PreviewRequest { TargetSeconds = 1, Fps = 30 };
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, 4).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            new byte[6], run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = new PreviewSegmentSnapshot(0, request, new(0, 0, true), frames, 1, 1.2, false, false)
        { CanStitch = false };
        typeof(MainWindowViewModel).GetField("_intervalSegment", Flags)!.SetValue(vm, segment);
        Invoke(vm, "SetIntervalFrames", frames, null!);
        foreach (var producer in producers) producer.Dispose();
        var before = scope.Capture();
        IReadOnlyList<PreviewFrameReference> metadata = Array.Empty<PreviewFrameReference>();
        for (int i = 0; i < 32; ++i)
        {
            metadata = (IReadOnlyList<PreviewFrameReference>)Invoke(vm, "NavigationFrames", request, segment)!;
            Assert.Equal(frames.Length, metadata.Count);
            for (int index = 0; index < frames.Length; ++index) Assert.Same(frames[index], metadata[index].Frame);
        }
        var after = scope.Capture();
        Assert.Equal(before.TotalRetains, after.TotalRetains);
        Assert.Equal(before.TryRetainAttempts, after.TryRetainAttempts);
        Assert.Equal(before.LiveTokens, after.LiveTokens);
        Assert.Equal(frames.Length, after.LiveOwners);
        Invoke(vm, "ClearInterval");
        Assert.True(scope.Capture().IsEmpty);
        Assert.False(metadata[0].Segment.CanStitch);
        Assert.Equal(0, metadata[0].SegmentId);
        Assert.Equal(1d, metadata[0].Frame.Xts!.Value);
        Assert.Throws<ObjectDisposedException>(() => metadata[0].Frame.RawPixels);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task anchorの索引取得と失効候補の拒否は現在の一枚とnavigation状態を変えない()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = 1, Fps = 30, TimestampOriginSeconds = 0,
            Capabilities = new(TimestampPassthrough: true),
        };
        var context = Context(vm, request: request);
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, 3).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            new byte[6], run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = cache.StoreSegment(request, frames, new(0, 0, true), 1, 1.2)!;
        foreach (var producer in producers) producer.Dispose();
        var anchor = new PreviewFrameReference(segment, 0);
        using var retainedAnchor = RawFrameLease.Capture(frames[0], "VmAnchor");
        typeof(MainWindowViewModel).GetField("_navigationAnchor", Flags)!.SetValue(vm, anchor);
        typeof(MainWindowViewModel).GetField("_anchorRaw", Flags)!.SetValue(vm, retainedAnchor);
        typeof(MainWindowViewModel).GetField("_navigationAnchorTarget", Flags)!.SetValue(vm, 1d);
        typeof(MainWindowViewModel).GetField("_intervalRequest", Flags)!.SetValue(vm, request);
        typeof(MainWindowViewModel).GetField("_intervalSegment", Flags)!.SetValue(vm, segment);
        Invoke(vm, "SetIntervalFrames", segment.Frames, null!);
        Invoke(vm, "SetTimeOnly", 1d);
        var metadata = (IReadOnlyList<PreviewFrameReference>)Invoke(vm, "NavigationFrames", request, segment)!;
        var before = scope.Capture();
        for (int i = 0; i < 32; ++i)
        {
            Assert.Equal(0, (int)Invoke(vm, "NavigationAnchorIndex", metadata, 1d, 30d)!);
            Assert.Same(anchor, Field(vm, "_navigationAnchor"));
            Assert.Same(retainedAnchor, Field(vm, "_anchorRaw"));
        }
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(before.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        Assert.Equal(before.LiveTokens, scope.Capture().LiveTokens);
        cache.Clear();
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(6, frames[0].RawPixels.Length);
        Assert.Throws<ObjectDisposedException>(() => frames[1].RawPixels);
        var generation = Field(vm, "_generation");
        var queued = Field(vm, "_lastQueuedAfter");
        var failedBefore = scope.Capture();
        Assert.False((bool)Invoke(vm, "DisplayCached", frames[1], metadata[1].InputSeconds, 1, metadata[1])!);
        Assert.Equal(failedBefore.TryRetainFailures + 1, scope.Capture().TryRetainFailures);
        Assert.Equal(failedBefore.TotalRetains, scope.Capture().TotalRetains);
        Assert.Same(anchor, Field(vm, "_navigationAnchor"));
        Assert.Same(retainedAnchor, Field(vm, "_anchorRaw"));
        Assert.Same(context, Field(vm, "_context"));
        Assert.Equal(generation, Field(vm, "_generation"));
        Assert.Equal(queued, Field(vm, "_lastQueuedAfter"));
        Assert.Equal(1d, vm.CurrentSeconds);
        Assert.Equal(6, frames[0].RawPixels.Length);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task 正IDのsegment通知はUI待ちでもrawを増やさずcache消失後はmetadataだけ残す()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = 1, Fps = 30, TimestampOriginSeconds = 0,
            Capabilities = new(TimestampPassthrough: true),
        };
        Context(vm, request: request);
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, 3).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            new byte[6], run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = cache.StoreSegment(request, frames, new(0, 0, true), 1, 1.2)!;
        foreach (var producer in producers) producer.Dispose();
        var before = scope.Capture();
        using var notification = new OwnedPreviewEnvelope(update: new(request, segment, false, false));
        Invoke(vm, "OnOwnedSegmentUpdated", notification);
        Assert.NotNull(Field(vm, "_pendingSegmentUpdate"));
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(before.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        Assert.Equal(before.LiveTokens, scope.Capture().LiveTokens);
        // 同じUIターンで先にcacheを消し、未反映通知が画素の寿命を延ばさないことを確認する。
        cache.Clear();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(frames.Length, notification.SegmentUpdated!.Segment.Frames.Count);
        for (int index = 0; index < frames.Length; ++index)
        {
            Assert.Same(frames[index], notification.SegmentUpdated.Segment.Frames[index]);
            Assert.Equal(frames[index].FrameId, notification.SegmentUpdated.Segment.Frames[index].FrameId);
            Assert.Throws<ObjectDisposedException>(() => frames[index].RawPixels);
        }
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Null(Field(vm, "_pendingSegmentUpdate"));
        Assert.Null(Field(vm, "_intervalSegment"));
        Assert.Empty((IReadOnlyList<Y4mFrame>)Field(vm, "_intervalFrames")!);
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task Segmentなし完了の窓は通知からUIとintervalへ画素を再取得せず渡る()
    {
        const int FrameCount = 4;
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        vm.HasDuration = true;
        var scope = Scope(vm);
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1, Fps = 30 };
        var context = Context(vm, request: request);
        Invoke(vm, "SetTimeOnly", 1d);
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, FrameCount).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            Enumerable.Repeat((byte)(16 + index), 6).ToArray(), run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        long before = scope.Capture().TotalRetains;
        using var notification = new OwnedPreviewEnvelope(completed: new(request, new(0, ""), false, false)
        { Frames = frames });
        long retained = scope.Capture().TotalRetains;
        Assert.Equal(before + FrameCount, retained);
        Invoke(vm, "OnOwnedCompleted", notification);
        foreach (var producer in producers) producer.Dispose();
        notification.Dispose();
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.Equal(FrameCount, scope.Capture().LiveOwners);
        Assert.Equal(FrameCount, scope.Capture().LiveTokens);
        Assert.Equal(FrameCount, scope.Capture().OwnerKinds["NotificationCompletionWindow"]);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Null(Field(vm, "_intervalSegment"));
        Assert.Null(context.GetType().GetProperty("Segment")!.GetValue(context));
        var held = (IReadOnlyList<Y4mFrame>)Field(vm, "_intervalFrames")!;
        Assert.Equal(FrameCount, held.Count);
        for (int index = 0; index < FrameCount; ++index)
        {
            Assert.Same(frames[index], held[index]);
            Assert.Equal(16 + index, held[index].RawPixels[0]);
        }
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.Equal(FrameCount, scope.Capture().LiveTokens);
        Assert.Equal(FrameCount, scope.Capture().NewOwners);
        Assert.Equal(1L, Field(vm, "_generation"));
        Assert.Equal(0L, Field(vm, "_lastQueuedAfter"));
        Invoke(vm, "ClearInterval");
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task ID0更新の窓は通知とUIとcontextとintervalで同じ所有を共有する()
    {
        const int FrameCount = 4;
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        vm.HasDuration = true;
        var scope = Scope(vm);
        var request = new PreviewRequest { CorrelationId = 1, TargetSeconds = 1, Fps = 30 };
        var context = Context(vm, request: request);
        Invoke(vm, "SetTimeOnly", 1d);
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, FrameCount).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            new byte[6], run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = new PreviewSegmentSnapshot(0, request, new(0, 0, true), frames, 1, 1.2, true, false)
        { CanStitch = false };
        long before = scope.Capture().TotalRetains;
        using var notification = new OwnedPreviewEnvelope(update: new(request, segment, false, false));
        long retained = scope.Capture().TotalRetains;
        Assert.Equal(before + FrameCount, retained);
        Invoke(vm, "OnOwnedSegmentUpdated", notification);
        foreach (var producer in producers) producer.Dispose();
        notification.Dispose();
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.Equal(FrameCount, scope.Capture().LiveTokens);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Assert.Equal(FrameCount, scope.Capture().LiveOwners);
        Assert.Equal(FrameCount, scope.Capture().LiveTokens);
        Assert.Equal(FrameCount, scope.Capture().OwnerKinds["NotificationId0Window"]);
        var held = (IReadOnlyList<Y4mFrame>)Field(vm, "_intervalFrames")!;
        var contextSegment = Assert.IsType<PreviewSegmentSnapshot>(context.GetType().GetProperty("Segment")!.GetValue(context));
        Assert.Equal(FrameCount, held.Count);
        for (int index = 0; index < FrameCount; ++index)
        {
            Assert.Same(frames[index], held[index]);
            Assert.Same(frames[index], contextSegment.Frames[index]);
        }
        Invoke(vm, "ClearInterval");
        Assert.Equal(FrameCount, scope.Capture().LiveOwners);
        Assert.Equal(FrameCount, scope.Capture().LiveTokens);
        context.GetType().GetProperty("Segment")!.SetValue(context, null);
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task 正ID完了をUI前に追い出すと元の一枚だけを救済し通知の親窓を返す()
    {
        const int FrameCount = 4;
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        vm.HasDuration = true;
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = 1, Fps = 30, TimestampOriginSeconds = 0,
            Capabilities = new(TimestampPassthrough: true),
        };
        Context(vm, request: request);
        Invoke(vm, "SetTimeOnly", 1d);
        byte[] pixels = { 16, 20, 24, 28, 128, 128 };
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, FrameCount).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            index == 0 ? pixels : Enumerable.Repeat((byte)(16 + index), 6).ToArray(), run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = cache.StoreSegment(request, frames, new(0, 0, true), 1, 1.2)!;
        long retained = scope.Capture().TotalRetains;
        using var notification = new OwnedPreviewEnvelope(completed: new(request, new(0, ""), false, false)
        { Frames = frames, Segment = segment });
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Invoke(vm, "OnOwnedCompleted", notification);
        Assert.Equal(retained + 1, scope.Capture().TotalRetains);
        cache.Clear();
        foreach (var producer in producers) producer.Dispose();
        notification.Dispose();
        Assert.Equal(1, scope.Capture().LiveTokens);
        Assert.Equal(1, scope.Capture().OwnerKinds["VmCompletionRescue"]);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        var held = Assert.IsType<PreviewSegmentSnapshot>(Field(vm, "_intervalSegment"));
        Assert.Equal(0, held.SegmentId);
        Assert.False(held.CanStitch);
        Assert.Same(frames[0], Assert.Single(held.Frames));
        Assert.Same(frames[0], Field(vm, "_displayedFrame"));
        Assert.Equal(run, held.Frames[0].RawRunId);
        Assert.Equal(frames[0].FrameId, held.Frames[0].FrameId);
        Assert.Equal(YuvToBgra.Convert(new Y4mFrame(Header, 1, 1d / 30, pixels)), Pixels(vm.PreviewImage!));
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.False(scope.Capture().OwnerKinds.ContainsKey("NotificationCompletionWindow"));
        for (int index = 1; index < FrameCount; ++index)
            Assert.Throws<ObjectDisposedException>(() => frames[index].RawPixels);
        Assert.Equal(FrameCount, scope.Capture().NewOwners);
        Assert.Equal(1L, Field(vm, "_generation"));
        // Contextで設定した世代1を追い出し救済のDisplayFrameへそのまま渡すため、投入・反映とも同じ世代になる。
        Assert.Equal(request.CorrelationId, Field(vm, "_lastQueuedAfter"));
        Assert.Equal(request.CorrelationId, Field(vm, "_lastDisplayedAfter"));
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task 正ID完了のcacheが残れば救済一枚だけを準備してUIは最新metadataを使う()
    {
        const int FrameCount = 4;
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        vm.HasDuration = true;
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = 1, Fps = 30, TimestampOriginSeconds = 0,
            Capabilities = new(TimestampPassthrough: true),
        };
        var context = Context(vm, request: request);
        Invoke(vm, "SetTimeOnly", 1d);
        byte[] pixels = { 16, 20, 24, 28, 128, 128 };
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, FrameCount).Select(index => scope.Create(Header, 1 + index / 30d, 1d / 30,
            index == 0 ? pixels : Enumerable.Repeat((byte)(16 + index), 6).ToArray(), run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = cache.StoreSegment(request, frames, new(0, 0, true), 1, 1.2)!;
        foreach (var producer in producers) producer.Dispose();
        context.GetType().GetProperty("Segment")!.SetValue(context, segment);
        Invoke(vm, "DisplayFrame", frames[0], context);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        var bitmap = vm.PreviewImage;
        long retained = scope.Capture().TotalRetains;
        using var notification = new OwnedPreviewEnvelope(completed: new(request, new(0, ""), false, false)
        { Frames = frames, Segment = segment });
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Invoke(vm, "OnOwnedCompleted", notification);
        notification.Dispose();
        Assert.Equal(retained + 1, scope.Capture().TotalRetains);
        Assert.Equal(1, scope.Capture().OwnerKinds["VmCompletionRescue"]);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(retained + 1, scope.Capture().TotalRetains);
        Assert.False(scope.Capture().OwnerKinds.ContainsKey("VmCompletionRescue"));
        var latest = Assert.IsType<PreviewSegmentSnapshot>(Field(vm, "_intervalSegment"));
        Assert.Equal(segment.SegmentId, latest.SegmentId);
        Assert.Equal(FrameCount, latest.Frames.Count);
        for (int index = 0; index < FrameCount; ++index) Assert.Same(frames[index], latest.Frames[index]);
        Assert.Same(frames[0], Field(vm, "_displayedFrame"));
        Assert.Same(bitmap, vm.PreviewImage);
        Assert.Equal(YuvToBgra.Convert(new Y4mFrame(Header, 1, 1d / 30, pixels)), Pixels(vm.PreviewImage!));
        cache.Clear();
        Assert.Equal(1, scope.Capture().LiveOwners);
        for (int index = 1; index < FrameCount; ++index)
            Assert.Throws<ObjectDisposedException>(() => frames[index].RawPixels);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task 隣未解決の完了救済は元時計と番号を保つ三枚だけをUIへ渡す()
    {
        const int FrameCount = 7;
        const int CenterIndex = 3;
        const int NeighborRadius = 1;
        const int FrameRate = 30;
        const double OriginSeconds = 100;
        const double TargetSeconds = 1;
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        vm.HasDuration = true;
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = TargetSeconds, Fps = FrameRate, TimestampOriginSeconds = OriginSeconds,
            Capabilities = new(TimestampPassthrough: true),
        };
        var context = Context(vm, neighborDirection: 1, request: request);
        Invoke(vm, "SetTimeOnly", TargetSeconds);
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, FrameCount).Select(index => scope.Create(Header,
            OriginSeconds + TargetSeconds + (index - CenterIndex) / (double)FrameRate, 1d / FrameRate,
            new byte[] { (byte)(16 + index), (byte)(16 + index), (byte)(16 + index), (byte)(16 + index), 128, 128 },
            run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = cache.StoreSegment(request, frames, new(OriginSeconds, 0, true),
            TargetSeconds - CenterIndex / (double)FrameRate, TargetSeconds + 0.2)!;
        long retained = scope.Capture().TotalRetains;
        using var notification = new OwnedPreviewEnvelope(completed: new(request, new(0, ""), false, false)
        { Frames = frames, Segment = segment });
        Assert.Equal(retained, scope.Capture().TotalRetains);
        Invoke(vm, "OnOwnedCompleted", notification);
        const int RescueFrameCount = NeighborRadius * 2 + 1;
        Assert.Equal(retained + RescueFrameCount, scope.Capture().TotalRetains);
        cache.Clear();
        foreach (var producer in producers) producer.Dispose();
        notification.Dispose();
        Assert.Equal(RescueFrameCount, scope.Capture().LiveOwners);
        Assert.Equal(RescueFrameCount, scope.Capture().LiveTokens);
        Assert.Equal(RescueFrameCount, scope.Capture().OwnerKinds["VmCompletionRescue"]);
        for (int index = 0; index < FrameCount; ++index)
            if (Math.Abs(index - CenterIndex) > NeighborRadius)
                Assert.Throws<ObjectDisposedException>(() => frames[index].RawPixels);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        var held = Assert.IsType<PreviewSegmentSnapshot>(Field(vm, "_intervalSegment"));
        Assert.Equal(0, held.SegmentId);
        Assert.False(held.CanStitch);
        Assert.Equal(RescueFrameCount, held.Frames.Count);
        Assert.Equal(OriginSeconds, held.Clock.InputOriginSeconds);
        Assert.Equal(segment.Clock.PhysicalSeekSeconds, held.Clock.PhysicalSeekSeconds);
        Assert.Equal(CenterIndex - NeighborRadius, held.Clock.FrameIndexOffset);
        for (int index = 0; index < RescueFrameCount; ++index)
        {
            Assert.Same(frames[CenterIndex - NeighborRadius + index], held.Frames[index]);
            Assert.Equal(TargetSeconds + (index - NeighborRadius) / (double)FrameRate,
                held.Clock.ToInputSeconds(held.Frames[index], index, FrameRate), 8);
        }
        Assert.True((bool)context.GetType().GetProperty("NeighborResolved")!.GetValue(context)!);
        Assert.Equal(TargetSeconds + 1d / FrameRate, vm.CurrentSeconds, 8);
        var selected = frames[CenterIndex + NeighborRadius];
        Assert.Same(selected, Field(vm, "_displayedFrame"));
        Assert.Equal(run, selected.RawRunId);
        Assert.Equal(YuvToBgra.Convert(new Y4mFrame(Header, selected.Xts, selected.Xdur,
            new byte[] { 20, 20, 20, 20, 128, 128 })), Pixels(vm.PreviewImage!));
        Assert.Equal(RescueFrameCount, scope.Capture().LiveOwners);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task 通常Before検索はmetadataだけを保持し選択一枚の取得時だけ所有を増やす()
    {
        const int FrameCount = 5;
        const int TargetIndex = 2;
        const int FrameRate = 30;
        const int LookupIterations = 32;
        const double TargetSeconds = 1;
        const double OriginSeconds = 1000;
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = TargetSeconds, Fps = FrameRate, TimestampOriginSeconds = OriginSeconds,
            Capabilities = new(TimestampPassthrough: true),
        };
        Context(vm, request: request);
        Invoke(vm, "SetTimeOnly", TargetSeconds);
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, FrameCount).Select(index => scope.Create(Header,
            OriginSeconds + TargetSeconds + (index - TargetIndex) / (double)FrameRate, 1d / FrameRate,
            new byte[] { (byte)(16 + index), (byte)(16 + index), (byte)(16 + index), (byte)(16 + index), 128, 128 },
            run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var segment = cache.StoreSegment(request, frames, new(OriginSeconds, 0, true),
            TargetSeconds - TargetIndex / (double)FrameRate, TargetSeconds + 0.2)!;
        foreach (var producer in producers) producer.Dispose();
        var before = scope.Capture();
        IReadOnlyList<Y4mFrame> metadata = Array.Empty<Y4mFrame>();
        for (int i = 0; i < LookupIterations; ++i)
        {
            object?[] arguments = { request, null, false };
            Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryGetBeforeInterval", Flags)!.Invoke(vm, arguments)!);
            metadata = Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(arguments[1]);
            Assert.Equal(FrameCount, metadata.Count);
            var current = Assert.IsType<PreviewSegmentSnapshot>(Field(vm, "_beforeIntervalSegment"));
            Assert.Equal(segment.SegmentId, current.SegmentId);
            Assert.Equal(segment.Clock, current.Clock);
            Assert.Equal(TargetIndex, FrameSelector.Select(current, TargetSeconds, FrameRate));
            for (int index = 0; index < FrameCount; ++index) Assert.Same(frames[index], metadata[index]);
        }
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(before.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        Assert.Equal(before.LiveTokens, scope.Capture().LiveTokens);
        object?[] selectionArguments = { request, metadata, null };
        var selected = Assert.IsType<Y4mFrame>(typeof(MainWindowViewModel).GetMethod("SelectBeforeFrame", Flags)!
            .Invoke(vm, selectionArguments));
        using var selectedOwnership = Assert.IsType<RawFrameLease>(selectionArguments[2]);
        Assert.Same(frames[TargetIndex], selected);
        Assert.Equal(run, selected.RawRunId);
        Assert.Equal(frames[TargetIndex].FrameId, selected.FrameId);
        Assert.Equal(OriginSeconds + TargetSeconds, selected.Xts!.Value, 8);
        Assert.Equal(before.TotalRetains + 1, scope.Capture().TotalRetains);
        Assert.Equal(before.TryRetainAttempts + 1, scope.Capture().TryRetainAttempts);
        Assert.Equal(before.TryRetainSuccesses + 1, scope.Capture().TryRetainSuccesses);
        Assert.Equal(before.TryRetainFailures, scope.Capture().TryRetainFailures);
        Assert.Equal(1, scope.Capture().OwnerKinds["VmBeforeSelection"]);
        cache.Clear();
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(1, scope.Capture().LiveTokens);
        Assert.Equal(new byte[] { 18, 18, 18, 18, 128, 128 }, selectedOwnership.Pixels.ToArray());
        for (int index = 0; index < FrameCount; ++index)
            if (index != TargetIndex) Assert.Throws<ObjectDisposedException>(() => metadata[index].RawPixels);
        selectedOwnership.Dispose();
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    [AvaloniaFact]
    public async Task Before検索後のcache消失は画素を固定せず選択と表示状態をcommitしない()
    {
        const int FrameCount = 5;
        const int TargetIndex = 2;
        const int FrameRate = 30;
        const double TargetSeconds = 1;
        const double OriginSeconds = 1000;
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        var cache = (FrameCache)Field(vm, "_cache")!;
        var request = new PreviewRequest
        {
            CorrelationId = 1, TargetSeconds = TargetSeconds, Fps = FrameRate, TimestampOriginSeconds = OriginSeconds,
            Capabilities = new(TimestampPassthrough: true),
        };
        var context = Context(vm, request: request);
        Invoke(vm, "SetTimeOnly", TargetSeconds);
        long run = scope.NewRunId();
        var producers = Enumerable.Range(0, FrameCount).Select(index => scope.Create(Header,
            OriginSeconds + TargetSeconds + (index - TargetIndex) / (double)FrameRate, 1d / FrameRate,
            new byte[6], run, "TestReader")).ToArray();
        var frames = producers.Select(producer => producer.Frame).ToArray();
        cache.StoreSegment(request, frames, new(OriginSeconds, 0, true),
            TargetSeconds - TargetIndex / (double)FrameRate, TargetSeconds + 0.2);
        foreach (var producer in producers) producer.Dispose();
        var initial = scope.Capture();
        object?[] arguments = { request, null, false };
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryGetBeforeInterval", Flags)!.Invoke(vm, arguments)!);
        var metadata = Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(arguments[1]);
        Assert.Equal(initial.TotalRetains, scope.Capture().TotalRetains);
        var held = Field(vm, "_beforeIntervalSegment");
        var anchor = Field(vm, "_navigationAnchor");
        var generation = Field(vm, "_generation");
        var imageGeneration = Field(vm, "_beforeImageGeneration");
        var queued = Field(vm, "_lastQueuedAfter");
        var image = vm.PreviewImage;
        var beforeImage = vm.BeforeImage;
        cache.Clear();
        Assert.True(scope.Capture().IsEmpty);
        foreach (var frame in metadata) Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
        object?[] selectionArguments = { request, metadata, null };
        Assert.Null(typeof(MainWindowViewModel).GetMethod("SelectBeforeFrame", Flags)!.Invoke(vm, selectionArguments));
        Assert.Null(selectionArguments[2]);
        // 正IDの所属が消えたことをmetadataだけで判定し、解放済み画素へのTry取得までは進まない。
        Assert.Equal(initial.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        Assert.Equal(initial.TotalRetains, scope.Capture().TotalRetains);
        Assert.Same(held, Field(vm, "_beforeIntervalSegment"));
        Assert.Same(anchor, Field(vm, "_navigationAnchor"));
        Assert.Same(context, Field(vm, "_context"));
        Assert.Equal(generation, Field(vm, "_generation"));
        Assert.Equal(imageGeneration, Field(vm, "_beforeImageGeneration"));
        Assert.Equal(queued, Field(vm, "_lastQueuedAfter"));
        Assert.Equal(TargetSeconds, vm.CurrentSeconds);
        Assert.Same(image, vm.PreviewImage);
        Assert.Same(beforeImage, vm.BeforeImage);
        Assert.Equal(frames[TargetIndex].FrameId, metadata[TargetIndex].FrameId);
        Assert.Equal(OriginSeconds + TargetSeconds, metadata[TargetIndex].Xts!.Value, 8);
        object?[] missing = { request, null, false };
        Assert.False((bool)typeof(MainWindowViewModel).GetMethod("TryGetBeforeInterval", Flags)!.Invoke(vm, missing)!);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Y4mFrame>>(missing[1]));
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(initial.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(initial.TryRetainAttempts, scope.Capture().TryRetainAttempts);
        await vm.DisposeAsync();
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        // 回帰用の2x2画像だけを読む。実計測のフレームごとの診断コピーは増やさない。
        byte[] pixels = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), handle.AddrOfPinnedObject(), pixels.Length, bitmap.PixelSize.Width * 4);
        }
        finally
        {
            handle.Free();
        }
        return pixels;
    }

    private static MainWindowViewModel CreateVm(Services services)
        => new(services, null, null, null, null, null, null, beforeEnabled: false);
    private static RawOwnershipScope Scope(MainWindowViewModel vm) => (RawOwnershipScope)Field(vm, "_rawOwnershipScope")!;
    private static object Context(MainWindowViewModel vm, int neighborDirection = 0, PreviewRequest? request = null)
    {
        typeof(MainWindowViewModel).GetField("_generation", Flags)!.SetValue(vm, 1L);
        var type = typeof(MainWindowViewModel).GetNestedType("RequestContext", BindingFlags.NonPublic)!;
        var ctor = type.GetConstructors(Flags).Single();
        object context = ctor.Invoke(ctor.GetParameters().Select(parameter => parameter.Name switch
        {
            "generation" => (object)1L,
            "request" => request ?? new PreviewRequest { CorrelationId = 1, TargetSeconds = 1 },
            "started" => 0L,
            "neighborDirection" => neighborDirection,
            _ => parameter.DefaultValue,
        }).ToArray());
        Invoke(vm, "RegisterContext", context);
        return context;
    }
    private static object Image(Y4mFrame frame, bool before = false)
    {
        var ctor = typeof(MainWindowViewModel).GetNestedType("ImageWork", BindingFlags.NonPublic)!.GetConstructors(Flags).Single();
        return ctor.Invoke(ctor.GetParameters().Select(parameter => parameter.Name switch
        {
            "Frame" => (object)frame, "Generation" => 1L, "EditEpoch" => 0L, "Before" => before, _ => parameter.DefaultValue,
        }).ToArray());
    }
    private static object? Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static object? InvokeStatic(string name, params object[] args) => typeof(MainWindowViewModel)
        .GetMethod(name, Flags | BindingFlags.Static)!.Invoke(null, args);
    private sealed class UnusedBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("この試験では実プロセスを起動しない。");
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "raw-vm-" + Guid.NewGuid().ToString("N"));
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

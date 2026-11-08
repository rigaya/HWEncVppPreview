using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.Tests;

public sealed class OwnedNavigationProcessViewModelTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [AvaloniaFact]
    public async Task 標準ownedの順streamはpause後も同じrunで読み進める()
    {
        await using var session = OwnedProcessSession.Create(filterText: "--vpp-nlmeans d=0");
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        await session.WaitForImageAsync(false, frame => Math.Abs(frame.Xts!.Value - 1010) < 1e-6);
        AssertDisplayed(session);
        var first = session.DisplayedFrame()!;
        var before = Interval(session.Vm);
        long allocations = session.Scope.Capture().NewOwners;
        byte[] pixels = RawPixels(first);

        session.Vm.CurrentSeconds = 11;
        await session.WaitIdleAsync();
        await session.WaitForImageAsync(false, frame => Math.Abs(frame.Xts!.Value - 1011) < 1e-6);
        var second = session.DisplayedFrame()!;
        var after = Interval(session.Vm);
        Assert.Equal(before.SegmentId, after.SegmentId);
        Assert.Equal(first.RawRunId, second.RawRunId);
        Assert.True(after.SafeEndSeconds > before.SafeEndSeconds);
        Assert.True(session.Scope.Capture().NewOwners > allocations);
        Assert.False(pixels.SequenceEqual(RawPixels(second)));
        AssertDisplayed(session);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 標準ownedの逆finiteは照合結果を保ちrawを回収する(bool reject)
    {
        string args = "--avhw" + (reject ? " --fake-seek-pixel-offset" : "");
        await using var session = OwnedProcessSession.Create(inputArgs: args, filterText: "--vpp-nlmeans d=0");
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        await session.WaitForImageAsync(false, frame => Math.Abs(frame.Xts!.Value - 1010) < 1e-6);
        AssertDisplayed(session);
        var controller = (PreviewController)Field(session.Vm, "_controller")!;
        var cache = (FrameCache)Field(session.Vm, "_cache")!;
        for (int i = 0; i < 4; i++)
        {
            session.Vm.StepFrameCommand.Execute(-1);
            await session.WaitIdleAsync();
        }
        var logical = (PreviewRequest)Field(controller, "_lastNavigationRequest")!;
        long streamId = Interval(session.Vm).SegmentId;
        int trimNotifications = 0;
        bool takingTrim = false;
        controller.OwnedSegmentUpdated += value =>
        {
            if (takingTrim && value.SegmentUpdated is { IsReadPaused: true } update && update.Segment.SegmentId == streamId)
                ++trimNotifications;
        };
        // 直前と同じ位置の受理通知で5回目とし、実streamのtrim通知だけを同期的に観測する。
        takingTrim = true;
        controller.NotifyNavigation(logical, -1);
        takingTrim = false;
        Assert.Equal(1, trimNotifications);
        await session.WaitIdleAsync();
        using var finite = ReverseSegment(cache);
        Assert.NotEmpty(finite.Frames);
        Assert.All(finite.Frames, frame => Assert.Same(session.Scope, frame.RawOwner!.Scope));
        Assert.False(finite.Segment!.PhysicalRequest.Continuous);
        Assert.Equal(!reject, finite.Segment.CanStitch);
        Assert.Equal(!reject, cache.CanStitch(logical));
        AssertDisplayed(session);
    }

    [AvaloniaFact]
    public async Task 標準ownedは逆方向取得後の大seekを順streamで表示する()
    {
        await using var session = OwnedProcessSession.Create(filterText: "--vpp-nlmeans d=0");
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        await session.WaitForImageAsync(false, frame => Math.Abs(frame.Xts!.Value - 1010) < 1e-6);
        session.Vm.CurrentSeconds = 8;
        await session.WaitIdleAsync();
        long previousRun = session.DisplayedFrame()!.RawRunId;
        session.Vm.CurrentSeconds = 40;
        await session.WaitIdleAsync();
        await session.WaitForImageAsync(false, frame => Math.Abs(frame.Xts!.Value - 1040) < 1e-6);
        var interval = Interval(session.Vm);
        var physical = interval.PhysicalRequest;
        Assert.True(physical.Continuous);
        Assert.Equal(40, physical.TargetSeconds);
        Assert.Equal(40 - (1 + physical.WarmupSeconds), CommandBuilder.Plan(physical).SeekSeconds!.Value, 6);
        Assert.NotEqual(previousRun, session.DisplayedFrame()!.RawRunId);
        AssertDisplayed(session);
    }

    private static RawFrameSetLease ReverseSegment(FrameCache cache)
    {
        long id;
        lock (Field(cache, "_gate")!)
        {
            // prefetchはUI通知を抑止するため、完了後の実cacheを同じgateで特定する。
            var entries = ((IDictionary)Field(cache, "_segments")!).Values.Cast<object>();
            var reverse = Assert.Single(entries, entry =>
                ((PreviewRequest)entry.GetType().GetProperty("Request")!.GetValue(entry)!).CorrelationId < long.MinValue / 2);
            id = (long)reverse.GetType().GetProperty("Id")!.GetValue(reverse)!;
        }
        return cache.GetSegmentOwned(id, "TestRead")!;
    }
    private static PreviewSegmentSnapshot Interval(MainWindowViewModel vm)
        => Assert.IsType<PreviewSegmentSnapshot>(Field(vm, "_intervalSegment"));
    private static object? Field(object target, string name)
        => target.GetType().GetField(name, Flags)!.GetValue(target);
    private static byte[] RawPixels(Y4mFrame frame)
    {
        using var lease = RawFrameLease.Capture(frame, "TestRead");
        return lease.Pixels.ToArray();
    }
    private static void AssertDisplayed(OwnedProcessSession session)
    {
        Assert.True(session.Scope.Capture().NewOwners > 0);
        var frame = Assert.IsType<Y4mFrame>(session.DisplayedFrame());
        Assert.Same(session.Scope, frame.RawOwner!.Scope);
        Assert.Throws<InvalidOperationException>(() => frame.Data);
        using var lease = RawFrameLease.Capture(frame, "TestRead");
        byte[] expected = new byte[frame.Header.Width * frame.Header.Height * 4];
        YuvToBgra.ConvertInto(lease, expected);
        var bitmap = Assert.IsAssignableFrom<Bitmap>(session.Vm.PreviewImage);
        byte[] actual = new byte[expected.Length];
        var pinned = GCHandle.Alloc(actual, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), pinned.AddrOfPinnedObject(), actual.Length, frame.Header.Width * 4);
        }
        finally
        {
            pinned.Free();
        }
        Assert.Equal(expected, actual);
    }
}

using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

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

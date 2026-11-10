using System.Reflection;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class BeforeProtectionDirectionTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string Filter = "--vpp-tweak brightness=0.01";

    [AvaloniaFact]
    public async Task 受理した前後コマだけで方向を更新し未受理反復と旧変換では戻さない()
    {
        await using var session = OwnedProcessSession.Create(filterText: Filter);
        var vm = session.Vm;
        vm.CompareMode = PreviewCompareMode.Split;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        AssertProtection(session, 0);

        double first = vm.CurrentSeconds;
        vm.StepFrameCommand.Execute(1);
        Assert.True(vm.CurrentSeconds > first);
        Assert.Equal(1, Field(vm, "_beforeProtectionDirection"));
        await session.WaitIdleAsync();
        AssertProtection(session, 1);
        var oldFrame = session.DisplayedFrame(before: true)!;
        long oldGeneration = (long)Field(vm, "_generation")!;
        long oldVersion = (long)Field(vm, "_beforeImageGeneration")!;
        double forwardTarget = vm.CurrentSeconds;

        // 隣待ちで受け付けなかった反復を、逆向きの保護ヒントにしてはいけない。
        var intentType = typeof(MainWindowViewModel).GetNestedType("NeighborIntent", BindingFlags.NonPublic)!;
        var intent = intentType.GetConstructors(Flags).Single(ctor => ctor.GetParameters().Length == 2)
            .Invoke(new object[] { oldGeneration, -1 });
        Set(vm, "_pendingNeighbor", intent);
        try { vm.StepFrameCommand.Execute(-1); Assert.Equal(1, Field(vm, "_beforeProtectionDirection")); }
        finally { Set(vm, "_pendingNeighbor", null); }

        vm.StepFrameCommand.Execute(-1);
        Assert.True(vm.CurrentSeconds < forwardTarget);
        await session.WaitIdleAsync();
        AssertProtection(session, -1);
        var currentImage = vm.BeforeImage;
        var currentFrame = session.DisplayedFrame(before: true)!;
        using var oldWork = Image(oldFrame, oldGeneration, (long)Field(vm, "_editEpoch")!, oldVersion);
        // 非現役の変換結果を直接適用して、最新方向と保護する一枚が両方変わらないことを確かめる。
        Call(vm, "ApplyImage", oldWork, Array.Empty<byte>());
        Assert.Same(currentImage, vm.BeforeImage);
        Assert.Same(currentFrame, session.DisplayedFrame(before: true));
        AssertProtection(session, -1);
    }

    [AvaloniaTheory]
    [InlineData("seek")]
    [InlineData("refresh")]
    [InlineData("input")]
    [InlineData("filter")]
    public async Task シークと再実行と入力編集とフィルタ編集はキャッシュの方向もゼロに戻す(string operation)
    {
        await using var session = OwnedProcessSession.Create(filterText: Filter);
        var vm = session.Vm;
        vm.CompareMode = PreviewCompareMode.Split;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        vm.StepFrameCommand.Execute(1);
        await session.WaitIdleAsync();
        AssertProtection(session, 1);
        switch (operation)
        {
            case "seek": vm.CurrentSeconds += .1; break;
            case "refresh": vm.RefreshCommand.Execute(null); break;
            case "input": vm.InputArgsText = "--avsw"; break;
            case "filter": vm.FilterLines[0].Text = "--vpp-tweak brightness=0.02"; break;
        }
        Assert.Equal(0, Field(vm, "_beforeProtectionDirection"));
        Assert.Equal(0, Field(session.Cache, "_displayedBeforeDirection"));
        session.Clock.Advance(TimeSpan.FromMilliseconds(300));
        await session.WaitIdleAsync();
        AssertProtection(session, 0);
    }

    [AvaloniaFact]
    public async Task 比較の開閉と画像破棄は一枚の識別子と方向を一緒に解除復元する()
    {
        await using var session = OwnedProcessSession.Create(filterText: Filter);
        var vm = session.Vm;
        vm.CompareMode = PreviewCompareMode.Split;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        vm.StepFrameCommand.Execute(1);
        await session.WaitIdleAsync();
        AssertProtection(session, 1);
        long frameId = session.DisplayedFrame(before: true)!.FrameId;
        vm.CompareMode = PreviewCompareMode.Single;
        Assert.Null(Field(session.Cache, "_displayedBeforeSegmentId"));
        Assert.Equal(0L, Field(session.Cache, "_displayedBeforeFrameId"));
        Assert.Equal(0, Field(session.Cache, "_displayedBeforeDirection"));
        vm.IsShowingBefore = true;
        AssertProtection(session, 1);
        Assert.Equal(frameId, Field(session.Cache, "_displayedBeforeFrameId"));
        vm.BeforeImage = null;
        Assert.Null(Field(session.Cache, "_displayedBeforeSegmentId"));
        Assert.Equal(0L, Field(vm, "_beforePresentationFrameId"));
        Assert.Equal(0L, Field(session.Cache, "_displayedBeforeFrameId"));
    }

    [AvaloniaFact]
    public async Task ENDで同じ早期画像の変換を省いても正区間の一枚と最新方向を引き継ぐ()
    {
        await using var session = OwnedProcessSession.Create(filterText: Filter);
        var vm = session.Vm;
        vm.CompareMode = PreviewCompareMode.Split;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        vm.StepFrameCommand.Execute(1);
        await session.WaitIdleAsync();
        var frame = session.DisplayedFrame(before: true)!;
        var segment = Assert.IsType<PreviewSegmentSnapshot>(Field(vm, "_beforeIntervalSegment"));
        var request = Assert.IsType<PreviewRequest>(Field(vm, "_beforeIntervalRequest")) with { TargetSeconds = vm.CurrentSeconds };
        long generation = (long)Field(vm, "_generation")!;
        var type = typeof(MainWindowViewModel).GetNestedType("RequestContext", BindingFlags.NonPublic)!;
        var ctor = type.GetConstructors(Flags).Single();
        object context = ctor.Invoke(ctor.GetParameters().Select(parameter => parameter.Name switch
        {
            "generation" => (object)generation, "request" => request, "started" => 0L,
            "kind" => Enum.Parse(parameter.ParameterType, "Before"), _ => parameter.DefaultValue,
        }).ToArray());
        Set(context, "BeforeEarlyFrame", frame);
        Set(context, "BeforeEarlyTarget", vm.CurrentSeconds);
        var image = vm.BeforeImage;
        // END前にはまだ正区間のFrameIdを登録できていなかった状態だけを作り、変換省略枝を直接通す。
        Call(vm, "SelectBeforePresentationSegment", segment.SegmentId, 0L);
        Assert.Equal(0L, Field(session.Cache, "_displayedBeforeFrameId"));
        Assert.True((bool)Call(vm, "CompleteEarlyBeforeFrame", context, frame)!);
        Assert.Same(image, vm.BeforeImage);
        AssertProtection(session, 1);
    }

    [AvaloniaFact]
    public async Task フィルタなしの共有Beforeは方向を持っても追加保護を作らない()
    {
        await using var session = OwnedProcessSession.Create();
        session.Vm.CompareMode = PreviewCompareMode.Split;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        session.Vm.StepFrameCommand.Execute(1);
        await session.WaitIdleAsync();
        Assert.Equal(1, Field(session.Vm, "_beforeProtectionDirection"));
        // 新要求の準備中にcontextが空でも、比較の再表示で共有Beforeだけを追加保護しない。
        Call(session.Vm, "BeginIntent", true);
        session.Vm.CompareMode = PreviewCompareMode.Single;
        session.Vm.IsShowingBefore = true;
        Assert.Null(Field(session.Cache, "_displayedBeforeSegmentId"));
        Assert.Equal(0L, Field(session.Cache, "_displayedBeforeFrameId"));
        Assert.Equal(0, Field(session.Cache, "_displayedBeforeDirection"));
    }

    private static void AssertProtection(OwnedProcessSession session, int direction)
    {
        var frame = session.DisplayedFrame(before: true)!;
        Assert.True(frame.FrameId > 0);
        Assert.Equal(frame.FrameId, Field(session.Vm, "_beforePresentationFrameId"));
        Assert.Equal(frame.FrameId, Field(session.Cache, "_displayedBeforeFrameId"));
        Assert.Equal(direction, Field(session.Vm, "_beforeProtectionDirection"));
        Assert.Equal(direction, Field(session.Cache, "_displayedBeforeDirection"));
        Assert.Equal(Field(session.Vm, "_beforePresentationSegmentId"), Field(session.Cache, "_displayedBeforeSegmentId"));
    }
    private static IDisposable Image(Y4mFrame frame, long generation, long epoch, long version)
    {
        var ctor = typeof(MainWindowViewModel).GetNestedType("ImageWork", BindingFlags.NonPublic)!.GetConstructors(Flags).Single();
        return (IDisposable)ctor.Invoke(ctor.GetParameters().Select(parameter => parameter.Name switch
        {
            "Frame" => (object)frame, "Generation" => generation, "EditEpoch" => epoch,
            "Before" => true, "BeforeVersion" => version, _ => parameter.DefaultValue,
        }).ToArray());
    }
    private static object? Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
}

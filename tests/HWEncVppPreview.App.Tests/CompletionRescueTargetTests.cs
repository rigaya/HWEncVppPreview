using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class CompletionRescueTargetTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const double InitialTarget = 10;
    private const double LatestTarget = 12;
    private const double TimestampOrigin = 1000;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2 };

    [AvaloniaTheory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task Before完了の少数救済はUI実行時の最新目標へ追従する(bool reachedEnd, bool emptyRescue, bool evict)
    {
        await using var session = OwnedProcessSession.Create(filterText: "--vpp-nlmeans", target: InitialTarget);
        var vm = session.Vm;
        vm.HasDuration = true;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        Assert.NotNull(session.DisplayedFrame(before: true));
        var previousRequest = Assert.IsType<PreviewRequest>(Field(vm, "_beforeIntervalRequest"));
        // 実行済みの旧contextを解放してから、同期完了だけを制御できるBeforeへ置き換える。
        Invoke(vm, "CancelBefore", false);
        Invoke(vm, "ClearBeforeInterval");
        session.Cache.Clear();
        double callbackTarget = emptyRescue ? InitialTarget - 2 : InitialTarget;
        Invoke(vm, "SetTimeOnly", callbackTarget);
        var request = previousRequest with { TargetSeconds = callbackTarget };
        using var cancellation = new CancellationTokenSource();
        object context = BeforeContext(vm, request, cancellation);
        long run = session.Scope.NewRunId();
        var producers = new[] { InitialTarget, LatestTarget }.Select(target => session.Scope.Create(Header,
            TimestampOrigin + target, 1d / 30, new byte[] { 32, 32, 32, 32, 128, 128 }, run, "TestCompletionReader")).ToArray();
        try
        {
            var segment = session.Cache.StoreSegment(request, producers.Select(item => item.Frame).ToArray(),
                new PreviewClockMap(TimestampOrigin, 0, true), InitialTarget, LatestTarget + 1d / 30,
                reachedInputEnd: reachedEnd)!;
            Assert.NotNull(segment);
            context.GetType().GetProperty("Segment")!.SetValue(context, segment);
            int runsBefore = RunCount(session.CommandLogPath);
            using (var notification = new OwnedPreviewEnvelope(completed: new(request, new(0, ""), false, false)
                { Frames = segment.Frames, Segment = segment }))
            {
                Invoke(vm, "OnOwnedCompleted", notification);
                // UIへyieldする前に変更し、callback時の1枚だけでは最新目標を満たせない状態を作る。
                Assert.Equal(emptyRescue ? 0L : 1L, session.Scope.Capture().OwnerKinds.GetValueOrDefault("VmCompletionRescue"));
                if (evict) session.Cache.Clear();
                Invoke(vm, "SetTimeOnly", LatestTarget);
                // 実操作の選択意図も最新へ進め、画像の時計計算を旧要求に残さない。
                SetField(vm, "_beforeSelectionRequest", request with { TargetSeconds = LatestTarget });
            }
            foreach (var producer in producers) producer.Dispose();
            await session.WaitIdleAsync();
            var displayed = Assert.IsType<Y4mFrame>(session.DisplayedFrame(before: true));
            Assert.Equal(TimestampOrigin + LatestTarget, displayed.Xts!.Value, 6);
            Assert.NotNull(vm.BeforeImage);
            if (evict)
            {
                var selection = Assert.IsType<PreviewRequest>(Field(vm, "_beforeSelectionRequest"));
                Assert.Equal(LatestTarget, selection.TargetSeconds);
                Assert.NotSame(context, Field(vm, "_beforeExecutionContext"));
                Assert.Equal(runsBefore + 1, RunCount(session.CommandLogPath));
                Assert.IsType<PreviewRequest>(Field(vm, "_beforeRescueRetrySelection"));
            }
            else
            {
                Assert.Same(context, Field(vm, "_beforeExecutionContext"));
                Assert.Equal(runsBefore, RunCount(session.CommandLogPath));
                Assert.Null(Field(vm, "_beforeRescueRetrySelection"));
            }
        }
        finally
        {
            foreach (var producer in producers) producer.Dispose();
        }
        await session.DisposeAsync();
        Assert.True(session.Scope.Capture().IsEmpty);
    }

    private static object BeforeContext(MainWindowViewModel vm, PreviewRequest request, CancellationTokenSource cancellation)
    {
        var type = typeof(MainWindowViewModel).GetNestedType("RequestContext", BindingFlags.NonPublic)!;
        var ctor = type.GetConstructors(Flags).Single();
        object context = ctor.Invoke(ctor.GetParameters().Select(parameter => parameter.Name switch
        {
            "generation" => Field(vm, "_generation"),
            "request" => request,
            "started" => 0L,
            "kind" => Enum.Parse(parameter.ParameterType, "Before"),
            "beforeCancellation" => cancellation,
            "editEpoch" => Field(vm, "_editEpoch"),
            _ => parameter.DefaultValue,
        }).ToArray());
        SetField(vm, "_beforeExecutionContext", context);
        SetField(vm, "_beforeCancellation", cancellation);
        SetField(vm, "_beforeFinished", false);
        SetField(vm, "_beforeSelectionRequest", request);
        return context;
    }

    private static int RunCount(string path)
    {
        if (!FakeLogFiles.Exists(path)) return 0;
        int count = 0;
        foreach (string line in FakeLogFiles.ReadAllLines(path))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("cmd").GetString() == "run") ++count;
        }
        return count;
    }

    private static object? Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target);
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
}

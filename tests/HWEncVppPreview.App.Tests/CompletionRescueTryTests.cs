using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class CompletionRescueTryTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const double InitialTarget = 10;
    private const double LatestTarget = 12;
    private const double TimestampOrigin = 1000;
    private const double MediaDurationSeconds = 60;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2 };

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 同期完了前に解放済みの救済候補は例外や古画像commitにせず実runで回復する(bool before)
    {
        await using var session = OwnedProcessSession.Create(filterText: "--vpp-nlmeans", target: InitialTarget,
            beforeEnabled: before);
        var vm = session.Vm;
        // 後続の取り直しも最新目標を要求できるよう、長さ既知の入力には目標を含む長さを与える。
        vm.DurationSeconds = MediaDurationSeconds;
        vm.HasDuration = true;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        var originalDisplayed = Assert.IsType<Y4mFrame>(session.DisplayedFrame(before));
        object? previousContext = Field(vm, before ? "_beforeExecutionContext" : "_context");
        Assert.NotNull(previousContext);
        var previousRequest = Assert.IsType<PreviewRequest>(previousContext.GetType().GetProperty("Request")!.GetValue(previousContext));
        if (before)
        {
            // 合成contextを登録する前に、実行済みBeforeの所有と取消源を通常経路で切り離す。
            Invoke(vm, "CancelBefore", false);
            Invoke(vm, "ClearBeforeInterval");
        }
        session.Cache.Clear();
        Invoke(vm, "SetTimeOnly", LatestTarget);
        var request = previousRequest with { TargetSeconds = LatestTarget };
        using var cancellation = new CancellationTokenSource();
        object context = InstallContext(vm, request, before, cancellation);
        if (!before) Invoke(vm, "ReleaseDetachedContext", previousContext);
        using var producer = session.Scope.Create(Header, TimestampOrigin + LatestTarget, 1d / 30,
            new byte[] { 32, 32, 32, 32, 128, 128 }, session.Scope.NewRunId(), "TestDeadCompletionReader");
        var deadFrame = producer.Frame;
        var segment = session.Cache.StoreSegment(request, new[] { deadFrame },
            new PreviewClockMap(TimestampOrigin, 0, true), LatestTarget, LatestTarget + 1d / 30)!;
        Assert.NotNull(segment);
        context.GetType().GetProperty("Segment")!.SetValue(context, segment);
        // 正ID通知はmetadataだけなので、作成しても元frameを生かさないことが前提になる。
        using var notification = new OwnedPreviewEnvelope(completed: new(request, new(0, ""), false, false)
        { Frames = segment.Frames, Segment = segment });
        session.Cache.Clear();
        producer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => deadFrame.RawPixels);
        long attempts = session.Scope.Capture().TryRetainAttempts;
        int runsBefore = RunCount(session.CommandLogPath);
        // ここで例外が出る旧Capture経路も検出する。まだUIを動かさないため古画像採用を直接確認できる。
        Invoke(vm, "OnOwnedCompleted", notification);
        Assert.True(session.Scope.Capture().TryRetainAttempts > attempts);
        Assert.False(session.Scope.Capture().OwnerKinds.ContainsKey("VmCompletionRescue"));
        Assert.Same(originalDisplayed, session.DisplayedFrame(before));
        await session.WaitIdleAsync();
        var displayed = Assert.IsType<Y4mFrame>(session.DisplayedFrame(before));
        Assert.NotSame(deadFrame, displayed);
        Assert.NotSame(originalDisplayed, displayed);
        Assert.Equal(TimestampOrigin + LatestTarget, displayed.Xts!.Value, 6);
        Assert.NotNull(before ? vm.BeforeImage : vm.PreviewImage);
        Assert.Null(vm.ErrorText);
        // Beforeは元画像1run、AfterはBefore無効なので前景1run。どちらも回復要求を重複させない。
        Assert.Equal(runsBefore + 1, RunCount(session.CommandLogPath));
        if (before)
        {
            var selection = Assert.IsType<PreviewRequest>(Field(vm, "_beforeSelectionRequest"));
            Assert.Equal(LatestTarget, selection.TargetSeconds);
            Assert.NotSame(context, Field(vm, "_beforeExecutionContext"));
        }
        else Assert.True((long)Field(vm, "_generation")! > (long)context.GetType().GetProperty("Generation")!.GetValue(context)!);
        await session.DisposeAsync();
        Assert.True(session.Scope.Capture().IsEmpty);
    }

    private static object InstallContext(MainWindowViewModel vm, PreviewRequest request, bool before, CancellationTokenSource cancellation)
    {
        var type = typeof(MainWindowViewModel).GetNestedType("RequestContext", BindingFlags.NonPublic)!;
        var ctor = type.GetConstructors(Flags).Single();
        object context = ctor.Invoke(ctor.GetParameters().Select(parameter => parameter.Name switch
        {
            "generation" => Field(vm, "_generation"), "request" => request, "started" => 0L,
            "kind" => Enum.Parse(parameter.ParameterType, before ? "Before" : "After"),
            "beforeCancellation" => before ? cancellation : null,
            "editEpoch" => Field(vm, "_editEpoch"), _ => parameter.DefaultValue,
        }).ToArray());
        if (!before) Invoke(vm, "RegisterContext", context);
        else
        {
            SetField(vm, "_beforeExecutionContext", context);
            SetField(vm, "_beforeCancellation", cancellation);
            SetField(vm, "_beforeFinished", false);
            SetField(vm, "_beforeSelectionRequest", request);
        }
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

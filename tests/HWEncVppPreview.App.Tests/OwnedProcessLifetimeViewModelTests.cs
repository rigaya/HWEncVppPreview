using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class OwnedProcessLifetimeViewModelTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [AvaloniaTheory]
    [InlineData("after")]
    [InlineData("neighbor")]
    [InlineData("before")]
    public async Task 実完了のcache追出し後も由来を保つ少数frameで救済する(string kind)
    {
        await using var session = OwnedProcessSession.Create(inputArgs: kind == "after" ? "--avhw --fake-input-end 11" : "--avhw",
            filterText: "--vpp-tweak brightness=0.01");
        bool neighbor = kind == "neighbor";
        bool before = kind == "before";
        if (neighbor)
        {
            await session.InitializeAsync();
            await session.WaitIdleAsync();
            await session.WaitForImageAsync(false, _ => true);
        }
        var callback = (Action<OwnedPreviewEnvelope>)Delegate.CreateDelegate(typeof(Action<OwnedPreviewEnvelope>), session.Vm,
            typeof(MainWindowViewModel).GetMethod("OnOwnedCompleted", Flags)!);
        var completed = new TaskCompletionSource<OwnedPreviewEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Controller.OwnedCompleted -= callback;
        void Hold(OwnedPreviewEnvelope value)
        {
            if (value.Completed is not { } result) return;
            bool isBefore = result.Request.CorrelationId < 0 && result.Request.CorrelationId > long.MinValue / 2;
            if (isBefore == before && !completed.Task.IsCompleted)
            {
                var retained = value.Retain("TestCompletion");
                if (!completed.TrySetResult(retained)) retained.Dispose();
            }
            else callback(value);
        }
        session.Controller.OwnedCompleted += Hold;
        try
        {
            if (neighbor)
            {
                session.Cache.Clear();
                Invoke(session.Vm, "ClearInterval");
                session.Vm.StepFrameCommand.Execute(-1);
                await session.Vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(OwnedProcessSession.Safety);
                session.Clock.Advance(TimeSpan.FromMilliseconds(150));
            }
            else await session.InitializeAsync();
            using var packet = await completed.Task.WaitWithDiagnosticsAsync(OwnedProcessSession.Safety);
            var source = packet.Completed!.Segment!;
            var ids = source.Frames.Select(frame => frame.FrameId).ToHashSet();
            Assert.NotEmpty(ids);
            long sourceId = source.SegmentId;
            // この試験は完了packetのevict救済を検査する。未反映Afterの正ID失効による
            // 別世代の再取得を混ぜないよう、追い出し前に既存のAfter仕事をUIまで完了させる。
            await session.WaitIdleAsync();
            await session.WaitForImageAsync(false, _ => true);
            // 実Readerの通知をUIへ渡した直後に正規cache.Clearで追い出す。
            callback(packet);
            session.Cache.Clear();
            Assert.Null(session.Cache.GetSegment(sourceId));
            packet.Dispose();
            await session.WaitIdleAsync();
            try { await session.WaitForImageAsync(before, frame => ids.Contains(frame.FrameId)); }
            catch (TimeoutException exception)
            {
                var shown = session.DisplayedFrame(before);
                var beforeContext = OwnedVmCheck.Field(session.Vm, "_beforeExecutionContext");
                var beforeSegment = beforeContext?.GetType().GetProperty("Segment")!.GetValue(beforeContext) as PreviewSegmentSnapshot;
                var raw = session.Scope.Capture();
                // 期待や待ち時間は変えず、別run/世代への置換と所有取得失敗を区別する。
                throw new TimeoutException(JsonSerializer.Serialize(new
                {
                    kind, sourceId, expectedFrameIds = ids, shownFrameId = shown?.FrameId, shownRawRunId = shown?.RawRunId, shownXts = shown?.Xts,
                    afterFrameId = session.DisplayedFrame(false)?.FrameId,
                    generation = OwnedVmCheck.Field(session.Vm, "_generation"),
                    beforeVersion = OwnedVmCheck.Field(session.Vm, "_beforeImageGeneration"),
                    beforeRequest = beforeContext?.GetType().GetProperty("Request")!.GetValue(beforeContext),
                    beforeSegmentId = beforeSegment?.SegmentId,
                    beforeFrameIds = beforeSegment?.Frames.Select(frame => frame.FrameId).ToArray(),
                    session.Vm.CurrentSeconds, session.Vm.IsBeforeBusy, session.Vm.ErrorText,
                    raw.LiveOwners, raw.LiveTokens, raw.OwnerKinds, raw.TryRetainsByCaller
                }), exception);
            }
            var displayed = session.DisplayedFrame(before)!;
            Assert.Same(session.Scope, displayed.RawOwner!.Scope);
            var held = (PreviewSegmentSnapshot)OwnedVmCheck.Field(session.Vm,
                before ? "_beforeIntervalSegment" : "_intervalSegment")!;
            Assert.Equal(0, held.SegmentId);
            Assert.False(held.CanStitch);
            Assert.InRange(held.Frames.Count, 1, neighbor ? 3 : 1);
            Assert.All(held.Frames, frame => Assert.Contains(frame.FrameId, ids));
            Assert.Equal(Convert(displayed), Pixels(before ? session.Vm.BeforeImage! : session.Vm.PreviewImage!));
        }
        finally
        {
            session.Controller.OwnedCompleted -= Hold;
            session.Controller.OwnedCompleted += callback;
            if (completed.Task.IsCompletedSuccessfully) completed.Task.Result.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task 実owned両表示はcache全消去後もHDRを再変換できる()
    {
        await using var session = OwnedProcessSession.Create(
            inputArgs: "--avhw --fake-transfer smpte2084 --fake-colormatrix bt2020nc --fake-colorprim bt2020",
            filterText: "--vpp-tweak brightness=0.01");
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        await session.WaitForImageAsync(false, _ => true);
        await session.WaitForImageAsync(true, _ => true);
        long allocations = session.Scope.Capture().NewOwners;
        session.Cache.Clear();
        session.Vm.HdrReferenceWhiteNits = 400;
        await session.WaitIdleAsync();
        Assert.Equal(allocations, session.Scope.Capture().NewOwners);
        Assert.Equal(Convert(session.DisplayedFrame()!, 400), Pixels(session.Vm.PreviewImage!));
        Assert.Equal(Convert(session.DisplayedFrame(true)!, 400), Pixels(session.Vm.BeforeImage!));
    }

    [AvaloniaFact]
    public async Task 実入力変更は旧要求と表示のrawを新runへ引き継がない()
    {
        await using var session = OwnedProcessSession.Create(beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        var previous = session.DisplayedFrame()!;
        long oldFrame = previous.FrameId;
        long oldRun = previous.RawRunId;
        session.Vm.OpenInput("owned-process-next.ts");
        await session.WaitIdleAsync();
        await session.WaitForImageAsync(false, frame => frame.FrameId != oldFrame);
        Assert.NotEqual(oldRun, session.DisplayedFrame()!.RawRunId);
        session.Cache.Clear();
        Assert.DoesNotContain(session.Scope.Capture().Live, owner => owner.FrameId == oldFrame);
        Assert.Null(session.Vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task 実時計replayは旧世代のowned画像を復活させない()
    {
        await using var session = OwnedProcessSession.Create(
            inputArgs: "--avhw --fake-clock-backstep-at 45", beforeEnabled: false);
        long firstRun = 0;
        session.Controller.OwnedFrameReady += value =>
        {
            // 逆行を検出した後の取り直しもIsReplay=falseで届くことがあるため、最初のrunだけを記録する。
            if (value.FrameReady is { IsReplay: false } frame) Interlocked.CompareExchange(ref firstRun, frame.Frame.RawRunId, 0);
        };
        await session.InitializeAsync();
        // 逆行を検出した後の取り直しは2通りある。
        // ・Coreが同じ要求をreplayする(ReplayStarted)。
        // ・replayを始める前に、VMが変換待ちの間に区間が破棄されたことに気づいて新しい要求を出し、元の要求を打ち切る。
        // 後者はCPUが少ないと起き(2コアのCIで再現)、ReplayStartedは来ない。
        // どちらも新しいrunで表示し直すため、replayの通知ではなく、時計を信用しないと判定されたことを待つ。
        var untrusted = (System.Collections.ICollection)OwnedVmCheck.Field(session.Controller, "_untrustedClocks")!;
        await session.WaitUntilAsync(() => untrusted.Count > 0);
        await session.WaitIdleAsync();
        await session.WaitForImageAsync(false, frame => frame.RawRunId != firstRun);
        Assert.True(firstRun > 0);
        Assert.NotEqual(firstRun, session.DisplayedFrame()!.RawRunId);
        session.Cache.Clear();
        Assert.DoesNotContain(session.Scope.Capture().Live, owner => owner.RawRunId == firstRun);
        Assert.Null(session.Vm.ErrorText);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 実serverとoneshotの取消は次のowned要求で回復する(bool oneShot)
    {
        await using var session = OwnedProcessSession.Create(oneShot: oneShot, beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        long previous = session.DisplayedFrame()!.FrameId;
        string gate = Path.Combine(session.DirectoryPath, "release-output");
        long previousOwners = session.Scope.Capture().NewOwners;
        long previousRun = session.DisplayedFrame()!.RawRunId;
        session.Vm.InputArgsText = "--avhw --fake-body-gate " + gate;
        session.Vm.OpenInput("owned-cancel.ts");
        await session.Vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(OwnedProcessSession.Safety);
        session.Clock.Advance(TimeSpan.FromMilliseconds(150));
        await WaitForBlockedReaderAsync(session, gate, "owned-cancel.ts", previousOwners, previousRun);
        session.Vm.InputArgsText = "--avhw";
        session.Vm.OpenInput("owned-recovered.ts");
        await session.WaitIdleAsync();
        await session.WaitForImageAsync(false, frame => frame.FrameId != previous);
        Assert.Null(session.Vm.ErrorText);
        Assert.Same(session.Scope, session.DisplayedFrame()!.RawOwner!.Scope);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 実pauseと実read待ちのDisposeは全ownedと子を回収する(bool active)
    {
        await using var session = OwnedProcessSession.Create(beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        Assert.NotNull(session.DisplayedFrame());
        if (active)
        {
            string gate = Path.Combine(session.DirectoryPath, "release-active-output");
            long previousOwners = session.Scope.Capture().NewOwners;
            long previousRun = session.DisplayedFrame()!.RawRunId;
            session.Vm.InputArgsText = "--avhw --fake-body-gate " + gate;
            session.Vm.OpenInput("owned-active-dispose.ts");
            await session.Vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(OwnedProcessSession.Safety);
            session.Clock.Advance(TimeSpan.FromMilliseconds(150));
            await WaitForBlockedReaderAsync(session, gate, "owned-active-dispose.ts", previousOwners, previousRun);
        }
        else
        {
            await session.WaitUntilAsync(() => OwnedVmCheck.Field(session.Controller, "_stream") is { } stream
                && (bool)stream.GetType().GetProperty("IsPaused")!.GetValue(stream)!);
        }
        await session.DisposeAsync();
        Assert.True(session.Scope.Capture().IsEmpty);
    }

    private static async Task WaitForBlockedReaderAsync(OwnedProcessSession session, string gate, string input,
        long previousOwners, long previousRun)
    {
        // Fakeの2枚目は画素前半まで出力済み。新runのReader tokenが残半のread待ちを証明する。
        // Probeも300枚要求になり得るため、実ReaderのXPID/XSEEKとreadyを対応づける。
        (long FrameId, long RunId, int Pid, double Seek)? blocked = null;
        await session.WaitUntilAsync(() =>
        {
            blocked = BlockedReader(session, previousOwners, previousRun);
            return blocked is { } value && File.Exists(gate + "." + value.Pid + ".ready");
        });
        var reader = blocked!.Value;
        using var ready = JsonDocument.Parse(File.ReadAllText(gate + "." + reader.Pid + ".ready"));
        Assert.Equal(reader.Pid, ready.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(reader.Seek, ready.RootElement.GetProperty("seek").GetDouble());
        Assert.Equal(input, ready.RootElement.GetProperty("input").GetString());
        Assert.Equal(2, ready.RootElement.GetProperty("outputOrdinal").GetInt64());
        Assert.True(OwnedProcessSession.IsRunning(reader.Pid));
        Assert.True(reader.RunId > previousRun);
        Assert.False(File.Exists(gate));
        Assert.Equal(reader, BlockedReader(session, previousOwners, previousRun));
        Console.WriteLine($"取消前の実Reader待ち: pid={reader.Pid}, input={input}, seek={reader.Seek}, ordinal=2, frame={reader.FrameId}, run={reader.RunId}, previousRun={previousRun}, readerTokens=1");
    }

    private static (long FrameId, long RunId, int Pid, double Seek)? BlockedReader(OwnedProcessSession session,
        long previousOwners, long previousRun)
    {
        // 台帳のlock内でmetadataだけを読み、試験側で画素や追加tokenを保持しない。
        lock (OwnedVmCheck.Field(session.Scope, "_gate")!)
        {
            var live = (Dictionary<long, RawFrameOwner>)OwnedVmCheck.Field(session.Scope, "_live")!;
            var reader = live.Values.SingleOrDefault(owner => owner.FrameId > previousOwners
                && owner.RawRunId != previousRun && owner.Kinds.GetValueOrDefault("Reader") == 1);
            if (reader?.Frame is not { } frame) return null;
            return (reader.FrameId, reader.RawRunId, int.Parse(frame.Header.ExtTags["XPID"]),
                double.Parse(frame.Header.ExtTags["XSEEK"], System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static byte[] Convert(Y4mFrame frame, double white = 203)
    {
        using var pixels = RawFrameLease.Capture(frame, "TestReference");
        var output = new byte[frame.Header.Width * frame.Header.Height * 4];
        YuvToBgra.ConvertInto(pixels, output, new() { ReferenceWhiteNits = white });
        return output;
    }

    private static byte[] Pixels(Bitmap image)
    {
        byte[] result = new byte[image.PixelSize.Width * image.PixelSize.Height * 4];
        var handle = GCHandle.Alloc(result, GCHandleType.Pinned);
        try
        {
            image.CopyPixels(new PixelRect(image.PixelSize), handle.AddrOfPinnedObject(), result.Length, image.PixelSize.Width * 4);
        }
        finally
        {
            handle.Free();
        }
        return result;
    }

    private static object? Invoke(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
}

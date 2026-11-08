using HWEncParamViewer.Testing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.Tests;

public sealed class OwnedEvictionOrderingTests(ITestOutputHelper output)
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [AvaloniaFact]
    public async Task UI反映前のcache消失は旧frameを使わず両画像を各二runで再取得する()
    {
        await using var session = OwnedProcessSession.Create(inputArgs: "--avhw --fake-input-end 11",
            filterText: "--vpp-tweak brightness=0.01");
        var frameCallback = Callback("OnOwnedFrameReady");
        var segmentCallback = Callback("OnOwnedSegmentUpdated");
        var completionCallback = Callback("OnOwnedCompleted");
        var afterPacket = new TaskCompletionSource<OwnedPreviewEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforePacket = new TaskCompletionSource<OwnedPreviewEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        long[] afterIds = Array.Empty<long>(), beforeIds = Array.Empty<long>();
        long oldAfterRun = 0, oldBeforeRun = 0;
        long? shownAfterId = null, shownBeforeId = null, shownAfterRun = null, shownBeforeRun = null;
        bool hooked = true;

        Action<OwnedPreviewEnvelope> Callback(string name)
            => (Action<OwnedPreviewEnvelope>)Delegate.CreateDelegate(typeof(Action<OwnedPreviewEnvelope>), session.Vm,
                typeof(MainWindowViewModel).GetMethod(name, Flags)!);
        static void Save(TaskCompletionSource<OwnedPreviewEnvelope> target, OwnedPreviewEnvelope value)
        {
            if (target.Task.IsCompleted) return;
            var retained = value.Retain("TestOrderingPacket");
            if (!target.TrySetResult(retained)) retained.Dispose();
        }
        void Frame(OwnedPreviewEnvelope value)
        {
            if (value.FrameReady?.Request.CorrelationId == 1) Save(afterPacket, value);
            else frameCallback(value);
        }
        void Segment(OwnedPreviewEnvelope value)
        {
            if (value.SegmentUpdated?.Request.CorrelationId != 1) segmentCallback(value);
        }
        void Complete(OwnedPreviewEnvelope value)
        {
            if (value.Completed?.Request.CorrelationId == -1 && !beforePacket.Task.IsCompleted) Save(beforePacket, value);
            else completionCallback(value);
        }
        void Restore()
        {
            if (!hooked) return;
            hooked = false;
            session.Controller.OwnedFrameReady -= Frame;
            session.Controller.OwnedSegmentUpdated -= Segment;
            session.Controller.OwnedCompleted -= Complete;
            session.Controller.OwnedFrameReady += frameCallback;
            session.Controller.OwnedSegmentUpdated += segmentCallback;
            session.Controller.OwnedCompleted += completionCallback;
        }

        session.Controller.OwnedFrameReady -= frameCallback;
        session.Controller.OwnedSegmentUpdated -= segmentCallback;
        session.Controller.OwnedCompleted -= completionCallback;
        session.Controller.OwnedFrameReady += Frame;
        session.Controller.OwnedSegmentUpdated += Segment;
        session.Controller.OwnedCompleted += Complete;
        try
        {
            await session.InitializeAsync();
            var savedAfter = await afterPacket.Task.WaitAsync(OwnedProcessSession.Safety);
            var savedBefore = await beforePacket.Task.WaitAsync(OwnedProcessSession.Safety);
            // concurrentなBeforeが先着しても、初回After完了のUI反映だけは先に済ませる。
            // 表示通知を抑止しているため、この待機でAfter画像は作られない。
            await session.WaitUntilAsync(() => (long)OwnedVmCheck.Field(session.Vm, "_lastCompletedAfter")! >= 1);
            Assert.Null(session.Vm.PreviewImage);
            Assert.Equal(1L, OwnedVmCheck.Field(session.Vm, "_generation"));
            var originalAfter = savedAfter.FrameReady!.Frame;
            var originalBefore = savedBefore.Completed!.Segment!;
            afterIds = (savedAfter.FrameReady!.Segment?.Frames ?? new[] { originalAfter })
                .Select(frame => frame.FrameId).Append(originalAfter.FrameId).Distinct().ToArray();
            beforeIds = originalBefore.Frames.Select(frame => frame.FrameId).ToArray();
            Assert.NotEmpty(afterIds);
            Assert.NotEmpty(beforeIds);
            oldAfterRun = originalAfter.RawRunId;
            oldBeforeRun = originalBefore.Frames[0].RawRunId;

            // UIに2通知を並べてからyield前に消す。変換速度でcache消失の前後が入れ替わらない。
            frameCallback(savedAfter);
            completionCallback(savedBefore);
            session.Cache.Clear();
            savedAfter.Dispose();
            savedBefore.Dispose();
            Restore();

            await session.WaitUntilAsync(() => (long)OwnedVmCheck.Field(session.Vm, "_generation")! > 1);
            session.Clock.Advance(TimeSpan.FromMilliseconds(150));
            await session.WaitIdleAsync();
            await session.WaitForImageAsync(false, frame => frame.RawRunId != oldAfterRun && !afterIds.Contains(frame.FrameId));
            await session.WaitForImageAsync(true, frame => frame.RawRunId != oldBeforeRun && !beforeIds.Contains(frame.FrameId));
            var after = session.DisplayedFrame()!;
            var before = session.DisplayedFrame(true)!;
            shownAfterId = after.FrameId;
            shownBeforeId = before.FrameId;
            shownAfterRun = after.RawRunId;
            shownBeforeRun = before.RawRunId;
            Assert.Same(session.Scope, after.RawOwner!.Scope);
            Assert.Same(session.Scope, before.RawOwner!.Scope);
            Assert.Equal(1010d, after.Xts!.Value, 6);
            Assert.Equal(1010d, before.Xts!.Value, 6);
            Assert.Equal(Convert(after), Pixels(session.Vm.PreviewImage!));
            Assert.Equal(Convert(before), Pixels(session.Vm.BeforeImage!));
            Assert.Null(session.Vm.ErrorText);
            var runs = Runs(session.CommandLogPath);
            // freeze_v6と候補へ同じtest assemblyを載せ、物理run数を別々に固定する。
            Assert.Equal(2, runs.Count(args => args.Contains("--vpp-tweak")));
            Assert.Equal(2, runs.Count(args => !args.Contains("--vpp-tweak")));
        }
        catch (Exception exception)
        {
            // Disposeのassertが本体例外を覆う場合にも、先に元例外と順序の証拠を残す。
            string original = exception.ToString();
            output.WriteLine(original);
            Console.WriteLine(original);
            try
            {
                var runs = Runs(session.CommandLogPath);
                var state = session.Scope.Capture();
                var after = session.DisplayedFrame();
                var before = session.DisplayedFrame(true);
                string diagnostic = JsonSerializer.Serialize(new
                {
                    phase = "本体失敗", exception = original,
                    generation = OwnedVmCheck.Field(session.Vm, "_generation"),
                    lastCompletedAfter = OwnedVmCheck.Field(session.Vm, "_lastCompletedAfter"),
                    beforeVersion = OwnedVmCheck.Field(session.Vm, "_beforeImageGeneration"),
                    afterPacketReady = afterPacket.Task.IsCompletedSuccessfully,
                    beforePacketReady = beforePacket.Task.IsCompletedSuccessfully,
                    capturedAfterId = afterPacket.Task.IsCompletedSuccessfully
                        ? afterPacket.Task.Result.FrameReady?.Frame.FrameId : null,
                    capturedBeforeIds = beforePacket.Task.IsCompletedSuccessfully
                        ? beforePacket.Task.Result.Completed?.Segment?.Frames.Select(frame => frame.FrameId).ToArray() : null,
                    physicalRuns = runs,
                    afterRuns = runs.Count(args => args.Contains("--vpp-tweak")),
                    beforeRuns = runs.Count(args => !args.Contains("--vpp-tweak")),
                    afterIds, beforeIds, oldAfterRun, oldBeforeRun,
                    actualAfterId = after?.FrameId, actualBeforeId = before?.FrameId,
                    actualAfterRun = after?.RawRunId, actualBeforeRun = before?.RawRunId,
                    session.Vm.CurrentSeconds, session.Vm.IsBusy, session.Vm.IsBeforeBusy, session.Vm.ErrorText,
                    state.LiveOwners, state.LiveTokens, state.LiveBytes, state.OwnerKinds,
                    state.TotalRetains, state.TotalTokenReleases
                });
                output.WriteLine(diagnostic);
                Console.WriteLine(diagnostic);
            }
            catch (Exception diagnosticFailure)
            {
                output.WriteLine("診断出力失敗:" + diagnosticFailure);
                Console.WriteLine("診断出力失敗:" + diagnosticFailure);
            }
            Console.Out.Flush();
            throw;
        }
        finally
        {
            Restore();
            if (afterPacket.Task.IsCompletedSuccessfully) afterPacket.Task.Result.Dispose();
            if (beforePacket.Task.IsCompletedSuccessfully) beforePacket.Task.Result.Dispose();
            var runs = Runs(session.CommandLogPath);
            long generation = (long)OwnedVmCheck.Field(session.Vm, "_generation")!;
            var beforeDispose = session.Scope.Capture();
            string closing = JsonSerializer.Serialize(new
            {
                phase = "Dispose直前", physicalRuns = runs,
                afterRuns = runs.Count(args => args.Contains("--vpp-tweak")),
                beforeRuns = runs.Count(args => !args.Contains("--vpp-tweak")),
                afterIds, beforeIds, oldAfterRun, oldBeforeRun,
                shownAfterId, shownBeforeId, shownAfterRun, shownBeforeRun, generation,
                beforeDispose.LiveOwners, beforeDispose.LiveTokens, beforeDispose.LiveBytes,
                beforeDispose.OwnerKinds, beforeDispose.Live,
                beforeDispose.TotalRetains, beforeDispose.TotalTokenReleases
            });
            output.WriteLine(closing);
            Console.WriteLine(closing);
            Console.Out.Flush();
            try { await session.DisposeAsync(); }
            catch (Exception exception)
            {
                output.WriteLine("Dispose失敗:" + exception);
                Console.WriteLine("Dispose失敗:" + exception);
                var remaining = session.Scope.Capture();
                string leaked = JsonSerializer.Serialize(new
                {
                    phase = "Dispose失敗後", generation,
                    remaining.LiveOwners, remaining.LiveTokens, remaining.LiveBytes,
                    remaining.OwnerKinds, remaining.Live,
                    remaining.TotalRetains, remaining.TotalTokenReleases
                });
                output.WriteLine(leaked);
                Console.WriteLine(leaked);
                Console.Out.Flush();
                throw;
            }
            var state = session.Scope.Capture();
            output.WriteLine(JsonSerializer.Serialize(new
            {
                afterRuns = runs.Count(args => args.Contains("--vpp-tweak")),
                beforeRuns = runs.Count(args => !args.Contains("--vpp-tweak")),
                physicalRuns = runs, afterIds, beforeIds, oldAfterRun, oldBeforeRun,
                shownAfterId, shownBeforeId, shownAfterRun, shownBeforeRun, generation,
                state.NewOwners, state.TotalRetains, state.TotalTokenReleases,
                state.LiveOwners, state.LiveTokens, state.LiveBytes
            }));
            Assert.True(state.IsEmpty);
            Assert.Equal(0, state.LiveBytes);
            Assert.Equal(state.NewOwners, state.FinalReleases);
            Assert.Equal(state.TotalRetains, state.TotalTokenReleases);
        }
    }

    private static string[][] Runs(string path)
    {
        if (!FakeLogFiles.Exists(path)) return Array.Empty<string[]>();
        var runs = new List<string[]>();
        foreach (string line in FakeLogFiles.ReadAllLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("cmd").GetString() != "run") continue;
            string[] args = root.GetProperty("args").EnumerateArray().Select(item => item.GetString()!).ToArray();
            int input = Array.IndexOf(args, "-i");
            if (input >= 0 && input + 1 < args.Length && args[input + 1] == "owned-process.ts"
                && !args.Contains("input=debug")) runs.Add(args);
        }
        return runs.ToArray();
    }

    private static byte[] Convert(Y4mFrame frame)
    {
        using var lease = RawFrameLease.Capture(frame, "TestOrderingReference");
        byte[] pixels = new byte[frame.Header.Width * frame.Header.Height * 4];
        YuvToBgra.ConvertInto(lease, pixels);
        return pixels;
    }

    private static byte[] Pixels(Bitmap image)
    {
        byte[] pixels = new byte[image.PixelSize.Width * image.PixelSize.Height * 4];
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { image.CopyPixels(new PixelRect(image.PixelSize), pinned.AddrOfPinnedObject(), pixels.Length, image.PixelSize.Width * 4); }
        finally { pinned.Free(); }
        return pixels;
    }
}

using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class RawBufferPoolViewModelTests
{
    [AvaloniaFact]
    public async Task 異なる大きさの前後画像が再利用と返却番兵でも画素を保つ()
    {
        await using var session = OwnedProcessSession.Create(filterText: "--output-res 32x18");
        session.Vm.CompareMode = PreviewCompareMode.Split;
        // 返却直後に画素を壊し、解放済み配列を表示や参照画素として読み続ける誤りを検出する。
        session.Scope.SetReturnedBufferPoisonForTesting(0xa5);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        var after = session.DisplayedFrame()!;
        var before = session.DisplayedFrame(before: true)!;
        Assert.Equal(32, after.Header.Width);
        Assert.Equal(64, before.Header.Width);
        AssertPixels(after, session.Vm.PreviewImage!);
        AssertPixels(before, session.Vm.BeforeImage!);
        // 再表示された配列から期待値を作ると、同じ破損を両側で見逃すため独立した初回画像を保存する。
        byte[] firstAfterPixels = BitmapPixels(session.Vm.PreviewImage!);
        byte[] firstBeforePixels = BitmapPixels(session.Vm.BeforeImage!);
        double? firstAfterXts = after.Xts;
        double? firstBeforeXts = before.Xts;
        long hits = session.Scope.CapturePool().Hits;
        // 同じ入力の再実行なら、前の窓から解放された配列を次の読取で使える。
        session.Vm.RefreshCommand.Execute(null);
        await session.WaitIdleAsync();
        Assert.True(session.Scope.CapturePool().Hits > hits);
        Assert.Equal(firstAfterXts, session.DisplayedFrame()!.Xts);
        Assert.Equal(firstBeforeXts, session.DisplayedFrame(before: true)!.Xts);
        Assert.Equal(firstAfterPixels, BitmapPixels(session.Vm.PreviewImage!));
        Assert.Equal(firstBeforePixels, BitmapPixels(session.Vm.BeforeImage!));
        AssertPixels(session.DisplayedFrame()!, session.Vm.PreviewImage!);
        AssertPixels(session.DisplayedFrame(before: true)!, session.Vm.BeforeImage!);
    }

    [AvaloniaFact]
    public async Task 入力切替前の貸出を後から再利用しても所有中の画素と新読取の全量を保つ()
    {
        await using var session = OwnedProcessSession.Create(beforeEnabled: false);
        session.Scope.SetReturnedBufferPoisonForTesting(0xa5);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        byte[] displayed = BitmapPixels(session.Vm.PreviewImage!);
        var header = new Y4mHeader { Width = 2, Height = 2 };
        byte[] oldPixels = Enumerable.Repeat((byte)0x19, header.FrameBytes).ToArray();
        using var oldBody = new MemoryStream(oldPixels);
        using var oldReader = new Y4mReader(oldBody, header)
            .ReadOwnedFrameBody("FRAME Xts=1", session.Scope, session.Scope.NewRunId());
        string original = session.Vm.InputPath;
        session.Vm.InputPath = "another-owned-process.ts";
        Assert.Equal(0, session.Scope.CapturePool().FreeBytes);
        session.Vm.InputPath = original;
        Assert.Equal(0, session.Scope.CapturePool().FreeBytes);
        Assert.Equal(oldPixels, oldReader.Pixels.ToArray());

        byte[] currentPixels = Enumerable.Repeat((byte)0x42, header.FrameBytes).ToArray();
        using var currentBody = new MemoryStream(currentPixels);
        using var currentReader = new Y4mReader(currentBody, header)
            .ReadOwnedFrameBody("FRAME Xts=2", session.Scope, session.Scope.NewRunId());
        Assert.Equal(oldPixels, oldReader.Pixels.ToArray());
        oldReader.Dispose();
        long hits = session.Scope.CapturePool().Hits;
        byte[] latestPixels = Enumerable.Repeat((byte)0x63, header.FrameBytes).ToArray();
        using var latestBody = new MemoryStream(latestPixels);
        // 同じ長さなら読取が全体を上書きし、正しさは所有権が守るため入力世代や形式を見なくてよい。
        using var latestReader = new Y4mReader(latestBody, header)
            .ReadOwnedFrameBody("FRAME Xts=3", session.Scope, session.Scope.NewRunId());
        Assert.Equal(hits + 1, session.Scope.CapturePool().Hits);
        Assert.Equal(latestPixels, latestReader.Pixels.ToArray());
        Assert.Equal(currentPixels, currentReader.Pixels.ToArray());
        Assert.Equal(displayed, BitmapPixels(session.Vm.PreviewImage!));
    }

    [AvaloniaFact]
    public async Task 破棄入口で再利用を閉じて遅く始まった読取の返却を受け付けない()
    {
        await using var session = OwnedProcessSession.Create(beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        var frame = session.DisplayedFrame()!;
        long oldRunId = session.Scope.NewRunId();
        // 通常の破棄は所有ゼロで終える。破棄前の要求情報だけで遅れて読取を開始する場合を分けて確かめる。
        await session.Vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(OwnedProcessSession.Safety);
        Assert.True(session.Scope.Capture().IsEmpty);
        Assert.True(PoolBoolean(session.Scope, "Closed"));
        long dropped = session.Scope.CapturePool().DropClosed;
        using (var late = session.Scope.RentReader(frame.Header, frame.Xts, frame.Xdur, oldRunId))
        {
            Assert.Equal(frame.Header.FrameBytes, session.Scope.CapturePool().LoanedBytes);
        }
        Assert.Equal(dropped + 1, session.Scope.CapturePool().DropClosed);
        Assert.Equal(0, session.Scope.CapturePool().LoanedBytes);
        Assert.Equal(0, PoolNumber(session.Scope, "FreeBytes"));
        Assert.Equal(0, PoolNumber(session.Scope, "FreeBuffers"));
        Assert.True(session.Scope.Capture().IsEmpty);
    }

    private static JsonElement PoolProperty(RawOwnershipScope scope, string name)
        => JsonSerializer.SerializeToElement(scope.CapturePool()).GetProperty(name);
    private static long PoolNumber(RawOwnershipScope scope, string name) => PoolProperty(scope, name).GetInt64();
    private static bool PoolBoolean(RawOwnershipScope scope, string name) => PoolProperty(scope, name).GetBoolean();

    private static void AssertPixels(Y4mFrame frame, Bitmap bitmap)
    {
        using var raw = RawFrameLease.Capture(frame, "TestReference");
        byte[] expected = new byte[frame.Header.Width * frame.Header.Height * 4];
        YuvToBgra.ConvertInto(raw, expected, new());
        Assert.Equal(expected, BitmapPixels(bitmap));
    }

    private static byte[] BitmapPixels(Bitmap bitmap)
    {
        byte[] actual = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        var handle = GCHandle.Alloc(actual, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), handle.AddrOfPinnedObject(), actual.Length, bitmap.PixelSize.Width * 4); }
        finally { handle.Free(); }
        return actual;
    }
}

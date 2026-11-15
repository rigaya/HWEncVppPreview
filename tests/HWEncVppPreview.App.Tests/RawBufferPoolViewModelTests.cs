using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class RawBufferPoolViewModelTests
{
    [AvaloniaFact]
    public async Task 異なる大きさの前後画像は再実行後も画素を保つ()
    {
        await using var session = OwnedProcessSession.Create(filterText: "--output-res 32x18");
        session.Vm.CompareMode = PreviewCompareMode.Split;
        session.Scope.SetReturnedBufferPoisonForTesting(0xa5);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        byte[] after = session.ImageBytes();
        byte[] before = session.ImageBytes(before: true);
        session.Vm.RefreshCommand.Execute(null);
        await session.WaitIdleAsync();
        Assert.Equal(after, session.ImageBytes());
        Assert.Equal(before, session.ImageBytes(before: true));
        Assert.Null(session.Vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task 入力切替を挟んでも貸出中の画素は保持される()
    {
        await using var session = OwnedProcessSession.Create(beforeEnabled: false);
        session.Scope.SetReturnedBufferPoisonForTesting(0xa5);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        var header = new Y4mHeader { Width = 2, Height = 2 };
        byte[] pixels = Enumerable.Repeat((byte)0x19, header.FrameBytes).ToArray();
        using var body = new MemoryStream(pixels);
        using var reader = new Y4mReader(body, header).ReadOwnedFrameBody("FRAME Xts=1", session.Scope, session.Scope.NewRunId());
        session.Vm.OpenInput("another-owned-process.ts");
        await session.WaitIdleAsync();
        Assert.Equal(pixels, reader.Pixels.ToArray());
        Assert.NotNull(session.Vm.PreviewImage);
        Assert.Null(session.Vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task 破棄後の遅い読取も所有を残さない()
    {
        await using var session = OwnedProcessSession.Create(beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        var header = new Y4mHeader { Width = 2, Height = 2 };
        long run = session.Scope.NewRunId();
        await session.Vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(OwnedProcessSession.Safety);
        using (session.Scope.RentReader(header, 1, 1.0 / 30, run)) { }
        Assert.Equal(0, session.Scope.CapturePool().LoanedBytes);
        Assert.Equal(0, session.Scope.CapturePool().FreeBytes);
        Assert.True(session.Scope.Capture().IsEmpty);
    }
}

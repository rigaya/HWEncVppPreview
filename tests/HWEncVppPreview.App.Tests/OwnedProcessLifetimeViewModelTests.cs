using System.IO.Pipes;
using System.Text;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.ViewModels;

namespace HWEncVppPreview.App.Tests;

public sealed class OwnedProcessLifetimeViewModelTests
{
    [AvaloniaTheory]
    [InlineData("after")]
    [InlineData("neighbor")]
    [InlineData("before")]
    public async Task cache追出し後も選択された画像を表示する(string kind)
    {
        await using var session = OwnedProcessSession.Create(filterText: "--vpp-tweak brightness=0.01");
        session.Vm.CompareMode = PreviewCompareMode.Split;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        if (kind == "neighbor")
        {
            session.Vm.StepFrameCommand.Execute(-1);
            await session.WaitIdleAsync();
        }
        bool before = kind == "before";
        byte[] expected = session.ImageBytes(before);
        session.Cache.Clear();
        await session.WaitIdleAsync();
        Assert.Equal(expected, session.ImageBytes(before));
        Assert.Null(session.Vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task 両画像はcache消去後もHDR設定を変更できる()
    {
        await using var session = OwnedProcessSession.Create(
            inputArgs: "--avhw --fake-transfer smpte2084 --fake-colormatrix bt2020nc --fake-colorprim bt2020",
            filterText: "--vpp-tweak brightness=0.01");
        session.Vm.CompareMode = PreviewCompareMode.Split;
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        session.Cache.Clear();
        session.Vm.HdrReferenceWhiteNits = 400;
        await session.WaitIdleAsync();
        byte[] after = session.ImageBytes();
        byte[] before = session.ImageBytes(before: true);
        // 入力先頭画素と規格のPQ式・原色変換式から別途計算した参照値。
        Assert.Equal(0, after[0]);
        Assert.InRange(after[1], 94, 96);
        Assert.Equal(0, after[2]);
        Assert.Equal(0, before[0]);
        Assert.InRange(before[1], 80, 82);
        Assert.Equal(0, before[2]);
        Assert.Null(session.Vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task 入力変更後も画像を表示し所有を回収する()
    {
        await using var session = OwnedProcessSession.Create(beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        byte[] expected = session.ImageBytes();
        session.Vm.OpenInput("owned-process-next.ts");
        await session.WaitIdleAsync();
        session.Cache.Clear();
        Assert.Equal(expected, session.ImageBytes());
        Assert.Null(session.Vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task 時計逆行後の古い取得を取り消し最新画像を表示する()
    {
        // 目標3秒の到達前に45枚目で時計を逆行させ、初回要求の終了を待つ。
        await using var session = OwnedProcessSession.Create(
            inputArgs: "--avhw --fake-clock-backstep-at 45", target: 3, beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        Assert.Contains("[preview] timestamp fallback:", session.Vm.LogText, StringComparison.Ordinal);
        Assert.NotEmpty(session.ImageBytes());
        Assert.Equal(3, session.Vm.CurrentSeconds, 5);
        Assert.Null(session.Vm.ErrorText);
        session.Vm.InputArgsText = "--avhw";
        session.Vm.CurrentSeconds = 20;
        await session.WaitIdleAsync();
        // 偽入力の20秒目の画素から別途求めた表示値で、古い取得の結果が残らないことを確かめる。
        Assert.Equal(new byte[] { 0, 133, 0, 255 }, session.ImageBytes()[..4]);
        Assert.Equal(20, session.Vm.CurrentSeconds, 5);
        Assert.Null(session.Vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task 実serverの取消後は新しい入力を表示できる()
    {
        string name = "owned-body-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var session = OwnedProcessSession.Create(beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        session.Vm.InputArgsText = "--avhw --fake-body-pipe " + name;
        session.Vm.OpenInput("owned-cancel.ts");
        await pipe.WaitForConnectionAsync().WaitWithDiagnosticsAsync(OwnedProcessSession.Safety);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        Assert.NotNull(await reader.ReadLineAsync().WaitWithDiagnosticsAsync(OwnedProcessSession.Safety));
        session.Vm.InputArgsText = "--avhw";
        session.Vm.OpenInput("owned-recovered.ts");
        await session.WaitIdleAsync();
        Assert.NotEmpty(session.ImageBytes());
        Assert.Null(session.Vm.ErrorText);
        Assert.Equal("owned-recovered.ts", session.Vm.InputPath);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 表示後と読取途中の破棄は所有と子を回収する(bool active)
    {
        string name = "owned-dispose-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var session = OwnedProcessSession.Create(beforeEnabled: false);
        await session.InitializeAsync();
        await session.WaitIdleAsync();
        if (active)
        {
            session.Vm.InputArgsText = "--avhw --fake-body-pipe " + name;
            session.Vm.OpenInput("owned-active-dispose.ts");
            await pipe.WaitForConnectionAsync().WaitWithDiagnosticsAsync(OwnedProcessSession.Safety);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            Assert.NotNull(await reader.ReadLineAsync().WaitWithDiagnosticsAsync(OwnedProcessSession.Safety));
        }
        await session.DisposeAsync();
        Assert.True(session.Scope.Capture().IsEmpty);
    }

}

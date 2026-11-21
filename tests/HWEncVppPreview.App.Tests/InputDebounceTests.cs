using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class InputDebounceTests
{
    [AvaloniaFact]
    public async Task 入力中の最新値を待機後に表示する()
    {
        var clock = new FakeTimeProvider();
        var backend = new ImageBackend();
        bool processAttempted = false;
        await using var probe = TestProcesses.CreateInputProbe(backend, clock, _ =>
        {
            processAttempted = true;
            throw new InvalidOperationException("この試験ではプロセスを起動しません。");
        });
        await using var vm = new MainWindowViewModel(new OwnedInjectionViewModelTests.Services(), clock,
            backend, probe, null, null, beforeEnabled: false,
            readTextOverride: (_, argument, _) => Task.FromResult(argument == "--help" ? "" : "FakeEncoder"))
        {
            EncoderPath = "fake", InputPath = "initial.ts", InputArgsText = "", CurrentSeconds = 0,
        };
        await vm.InitializeAsync(false);
        await vm.WaitForIdleAsync();
        vm.InputPath = "first.ts";
        vm.InputPath = "latest.ts";
        // 入力変更で待機が登録されてから、一度だけ待機時間を進める。
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForPreparationAsync();
        await vm.WaitForIdleAsync();
        InputInfo info = await probe.ProbeAsync("fake", "latest.ts");
        Assert.Equal(TimeSpan.FromSeconds(10), info.Duration);
        Assert.Equal(10, vm.DurationSeconds);
        Assert.False(processAttempted);
        Assert.Equal("latest.ts", vm.InputPath);
        Assert.Null(vm.ErrorText);
        var bitmap = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
        using var pixels = bitmap.Lock();
        Assert.Equal(255, Marshal.ReadByte(pixels.Address));
    }

    private sealed class ImageBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 Ip A1:1 C420jpeg");
            sink.OnHeader(header);
            byte value = request.InputFile == "latest.ts" ? (byte)235 : (byte)16;
            for (int index = 0; index < 31; index++)
            {
                if (token.IsCancellationRequested) break;
                sink.OnFrame(new Y4mFrame(header, index / 30.0, 1.0 / 30, [value, value, value, value, 128, 128]));
            }
            return Task.FromResult(new PreviewRunResult(0, "  Duration: 00:00:10.00, start: 0"));
        }

        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

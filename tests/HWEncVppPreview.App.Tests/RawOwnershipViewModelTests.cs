using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class RawOwnershipViewModelTests
{
    [AvaloniaFact]
    public async Task cache消去後も両表示のHDR再変換を保持しDisposeで回収する()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        var cache = new FrameCache(scope);
        var backend = new HdrOwnedBackend();
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        var vm = TestViewModels.Create(new OwnedInjectionViewModelTests.Services(), backend,
            null, cache, null, owned: dependencies);
        vm.EncoderPath = OwnedInjectionViewModelTests.FakeEncoderPath();
        vm.InputPath = "owned-hdr.ts";
        vm.InputArgsText = "--avhw";
        vm.CompareMode = PreviewCompareMode.SideBySide;
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            byte[] initial = Pixels(Assert.IsAssignableFrom<Bitmap>(vm.PreviewImage));
            cache.Clear();
            vm.HdrReferenceWhiteNits = 400;
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            byte[] after = Pixels(Assert.IsAssignableFrom<Bitmap>(vm.PreviewImage));
            byte[] before = Pixels(Assert.IsAssignableFrom<Bitmap>(vm.BeforeImage));
            Assert.NotEqual(initial, after);
            Assert.Equal(after, before);
            Assert.Null(vm.ErrorText);
        }
        finally
        {
            await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), handle.AddrOfPinnedObject(), pixels.Length, bitmap.PixelSize.Width * 4); }
        finally { handle.Free(); }
        return pixels;
    }

    private sealed class HdrOwnedBackend : IPreviewBackend, IRawOwnedBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => throw new InvalidOperationException("所有権のない経路は使えません。");
        public Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink, RawOwnershipScope scope,
            CancellationToken token = default)
        {
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 Ip A1:1 C420 XTRANSFER=smpte2084 XCOLORMATRIX=bt2020nc XCOLORPRIM=bt2020");
            sink.OnHeader(header);
            long run = scope.NewRunId();
            for (int index = 0; index < 31; index++)
            {
                using var frame = scope.Create(header, index / 30.0, 1.0 / 30,
                    new byte[] { 100, 100, 100, 100, 128, 128 }, run, "試験の読取");
                sink.OnFrame(frame.Frame);
            }
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

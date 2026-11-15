using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class BeforeCompletionResultTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task キャッシュ消去と目標変更の後に元画像を最新目標で表示し回収する(bool untimed)
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope);
        var after = new ControlledBackend(false, untimed);
        var before = new ControlledBackend(true, untimed);
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        var vm = new MainWindowViewModel(new OwnedInjectionViewModelTests.Services(), TimeProvider.System, after, after,
            null, cache, (frame, token) => Task.FromResult(Enumerable.Repeat(frame.RawPixels[0],
                frame.Header.Width * frame.Header.Height * 4).ToArray()), before, before, owned: dependencies)
        {
            EncoderPath = OwnedInjectionViewModelTests.FakeEncoderPath(), InputPath = "before-result.ts",
            InputArgsText = "--avhw", CurrentSeconds = .5, CompareMode = PreviewCompareMode.SideBySide,
        };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await before.Started.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            vm.CurrentSeconds = 1;
            cache.Clear();
            before.Release.TrySetResult();
            await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.Null(vm.ErrorText);
            Assert.False(vm.IsBeforeBusy);
            Assert.Equal(1, vm.CurrentSeconds);
            using var pixels = Assert.IsType<WriteableBitmap>(vm.BeforeImage).Lock();
            using var afterPixels = Assert.IsType<WriteableBitmap>(vm.PreviewImage).Lock();
            Assert.Equal((byte)30, Marshal.ReadByte(pixels.Address));
            Assert.Equal((byte)30, Marshal.ReadByte(afterPixels.Address));
        }
        finally
        {
            before.Release.TrySetResult();
            await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    private sealed class ControlledBackend(bool holdFirst, bool untimed) : IPreviewBackend, IRawOwnedBackend
    {
        private int _calls;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => throw new InvalidOperationException("所有権のない経路は使えません。");

        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink,
            RawOwnershipScope scope, CancellationToken token = default)
        {
            if (holdFirst && Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            long run = scope.NewRunId();
            for (int index = 0; index < 61; index++)
            {
                byte value = (byte)index;
                using var frame = scope.Create(header, untimed ? null : index / 30d, 1d / 30,
                    new[] { value, value, value, value, (byte)128, (byte)128 }, run, "試験の読取");
                sink.OnFrame(frame.Frame);
            }
            return new PreviewRunResult(0, "");
        }

        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

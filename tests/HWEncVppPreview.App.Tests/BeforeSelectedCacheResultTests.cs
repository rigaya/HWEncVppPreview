using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class BeforeSelectedCacheResultTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 元画像の選択後にキャッシュを消しても最終画像を表示し回収する(bool lateDominant)
    {
        string? previous = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope);
        var after = new ControlledBackend(false, false);
        var before = new ControlledBackend(true, lateDominant);
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        var vm = TestViewModels.Create(new OwnedInjectionViewModelTests.Services(), after,
            null, cache, (frame, token) => Task.FromResult(Enumerable.Repeat(frame.RawPixels[0],
                frame.Header.Width * frame.Header.Height * 4).ToArray()), before, owned: dependencies);
        vm.EncoderPath = OwnedInjectionViewModelTests.FakeEncoderPath();
        vm.InputPath = "early.ts";
        vm.InputArgsText = "--avhw";
        vm.CurrentSeconds = .5;
        vm.CompareMode = PreviewCompareMode.SideBySide;
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.BeforeImage)) selected.TrySetResult();
        };
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await before.Started.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await selected.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            cache.Clear();
            before.Release.TrySetResult();
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.Null(vm.ErrorText);
            Assert.False(vm.IsBeforeBusy);
            using var pixels = Assert.IsType<WriteableBitmap>(vm.BeforeImage).Lock();
            Assert.Equal(lateDominant ? (byte)99 : (byte)15, Marshal.ReadByte(pixels.Address));
        }
        finally
        {
            before.Release.TrySetResult();
            try { await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang); }
            finally { Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", previous); }
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    private sealed class ControlledBackend(bool holdAfterSelection, bool lateDominant) : IPreviewBackend, IRawOwnedBackend
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => throw new InvalidOperationException("所有権のない経路は使えません。");

        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink,
            RawOwnershipScope scope, CancellationToken token = default)
        {
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            long run = scope.NewRunId();
            for (int index = 0; index < 61; index++)
            {
                byte value = (byte)index;
                using var frame = scope.Create(header, (request.TimestampOriginSeconds ?? 0) + index / 30d, 1d / 30,
                    new[] { value, value, value, value, (byte)128, (byte)128 }, run, "試験の読取");
                sink.OnFrame(frame.Frame);
                if (holdAfterSelection && index == (lateDominant ? 16 : 31))
                {
                    Started.TrySetResult();
                    await Release.Task.WaitAsync(token);
                    if (lateDominant)
                    {
                        using var final = scope.Create(header, (request.TimestampOriginSeconds ?? 0) + .6, 1,
                            new byte[] { 99, 99, 99, 99, 128, 128 }, run, "試験の読取");
                        sink.OnFrame(final.Frame);
                        break;
                    }
                }
            }
            return new PreviewRunResult(0, "");
        }

        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class BeforeUnsafeLandingResultTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 目標より後に着地した画像は未表示で完了する(bool afterLate)
    {
        string? previous = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope);
        var after = new ControlledBackend(afterLate);
        var before = new ControlledBackend(true);
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        var vm = TestViewModels.Create(new OwnedInjectionViewModelTests.Services(), after,
            null, cache, (frame, token) => Task.FromResult(Enumerable.Repeat(frame.RawPixels[0],
                frame.Header.Width * frame.Header.Height * 4).ToArray()), before, owned: dependencies);
        vm.EncoderPath = OwnedInjectionViewModelTests.FakeEncoderPath();
        vm.InputPath = "before-result.ts";
        vm.InputArgsText = "--avhw";
        vm.CurrentSeconds = .1;
        vm.CompareMode = PreviewCompareMode.SideBySide;
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.Equal(afterLate, vm.ErrorText != null);
            Assert.False(vm.IsBeforeBusy);
            Assert.Equal(.1, vm.CurrentSeconds);
            Assert.Null(vm.BeforeImage);
            Assert.Equal(!afterLate, vm.PreviewImage != null);
        }
        finally
        {
            try { await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang); }
            finally { Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", previous); }
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    private sealed class ControlledBackend(bool isBefore) : IPreviewBackend, IRawOwnedBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => throw new InvalidOperationException("所有権のない経路は使えません。");

        public Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink,
            RawOwnershipScope scope, CancellationToken token = default)
        {
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            long run = scope.NewRunId();
            for (int index = 0; index < (isBefore ? 16 : 61); index++)
            {
                byte value = (byte)index;
                using var frame = scope.Create(header, (request.TimestampOriginSeconds ?? 0) + index / 30d + (isBefore ? .5 : 0), 1d / 30,
                    new[] { value, value, value, value, (byte)128, (byte)128 }, run, "試験の読取");
                sink.OnFrame(frame.Frame);
            }
            return Task.FromResult(new PreviewRunResult(0, ""));
        }

        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

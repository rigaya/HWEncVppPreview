using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class SafeBoundaryViewModelTests
{
    [AvaloniaTheory]
    [InlineData(false, 0.000000333333333, true)]
    [InlineData(false, 0.0000015, true)]
    [InlineData(true, 0.0000015, true)]
    [InlineData(false, 0.000003, false)]
    [InlineData(true, 0.000003, false)]
    public async Task BeforeAndNavigationUseTheSameSafeBoundary(bool lowerBoundary, double error, bool expected)
    {
        string? previous = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope);
        var backend = new MissingBackend();
        var request = new PreviewRequest
        {
            EncoderPath = OwnedInjectionViewModelTests.FakeEncoderPath(), InputFile = "boundary.ts", InputArgs = ["--avhw"],
            TargetSeconds = 5, Fps = 30, Capabilities = new(true, true), TimestampOriginSeconds = 1000,
        };
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 Ip A1:1 C420");
        long run = scope.NewRunId();
        using (var first = scope.Create(header, lowerBoundary ? 1005 : 1005 - 1d / 30, 1d / 30,
            new byte[] { 20, 20, 20, 20, 128, 128 }, run, "試験の読取"))
        using (var second = scope.Create(header, lowerBoundary ? 1005 + 1d / 30 : 1005, 1d / 30,
            new byte[] { 42, 42, 42, 42, 128, 128 }, run, "試験の読取"))
        {
            cache.StoreSegment(request, [first.Frame, second.Frame], new(1000, 2, true),
                lowerBoundary ? 5 + error : 4, lowerBoundary ? 6 : 5 - error);
        }
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        var vm = TestViewModels.Create(new OwnedInjectionViewModelTests.Services(), backend,
            null, cache, (frame, token) => Task.FromResult(Enumerable.Repeat(frame.RawPixels[0],
                frame.Header.Width * frame.Header.Height * 4).ToArray()), owned: dependencies);
        vm.EncoderPath = request.EncoderPath;
        vm.InputPath = request.InputFile;
        vm.InputArgsText = "--avhw";
        vm.CurrentSeconds = lowerBoundary ? 5 + 1d / 30 : 5 - 1d / 30;
        vm.CompareMode = PreviewCompareMode.SideBySide;
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            vm.StepFrameCommand.Execute(lowerBoundary ? -1 : 1);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.Equal(expected, Math.Abs(vm.CurrentSeconds - 5) < 0.0000001);
            Assert.Equal(expected, vm.ErrorText == null);
            if (expected)
            {
                using var after = Assert.IsType<WriteableBitmap>(vm.PreviewImage).Lock();
                using var before = Assert.IsType<WriteableBitmap>(vm.BeforeImage).Lock();
                byte wanted = lowerBoundary ? (byte)20 : (byte)42;
                Assert.Equal(wanted, Marshal.ReadByte(after.Address));
                Assert.Equal(wanted, Marshal.ReadByte(before.Address));
            }
        }
        finally
        {
            try { await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang); }
            finally { Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", previous); }
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    private sealed class MissingBackend : IPreviewBackend, IRawOwnedBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => throw new InvalidOperationException("所有権のない経路は使えません。");

        public Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink,
            RawOwnershipScope scope, CancellationToken token = default)
            => Task.FromResult(new PreviewRunResult(4, "安全域外"));

        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

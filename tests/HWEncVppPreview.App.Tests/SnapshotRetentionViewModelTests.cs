using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class SnapshotRetentionViewModelTests
{
    [AvaloniaFact]
    public async Task CacheEvictionReleasesHeldIntervalsWithoutLosingTheLastDisplayedFrame()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        var cache = new FrameCache(scope);
        var backend = new OwnedInjectionViewModelTests.OwnedBackend();
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        var vm = new MainWindowViewModel(new OwnedInjectionViewModelTests.Services(), TimeProvider.System, backend, backend,
            null, cache, (frame, token) => Task.FromResult(Enumerable.Repeat((byte)42, frame.Header.Width * frame.Header.Height * 4).ToArray()),
            beforeEnabled: false, owned: dependencies)
        {
            EncoderPath = OwnedInjectionViewModelTests.FakeEncoderPath(), InputPath = "retention.ts", InputArgsText = "--avhw"
        };
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            cache.Clear();
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.Null(vm.ErrorText);
            var bitmap = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
            using var pixels = bitmap.Lock();
            Assert.Equal(42, Marshal.ReadByte(pixels.Address));
            Assert.Equal(0, cache.SizeBytes);
        }
        finally
        {
            await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        }
        Assert.True(scope.Capture().IsEmpty);
    }
}

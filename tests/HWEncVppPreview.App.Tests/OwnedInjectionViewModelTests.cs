using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class OwnedInjectionViewModelTests
{
    [AvaloniaFact]
    public async Task 所有経路で画像を表示し終了後に全て回収する()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        var cache = new FrameCache(scope);
        var backend = new OwnedBackend();
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        var vm = new MainWindowViewModel(new Services(), null, backend, null, cache,
            (frame, token) => Task.FromResult(Enumerable.Repeat((byte)42, frame.Header.Width * frame.Header.Height * 4).ToArray()),
            beforeEnabled: false, owned: dependencies)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "owned-injection.ts", InputArgsText = "--avhw"
        };
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.Null(vm.ErrorText);
            var bitmap = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
            using var pixels = bitmap.Lock();
            Assert.Equal(42, Marshal.ReadByte(pixels.Address));
        }
        finally
        {
            await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaFact]
    public async Task 変換中にキャッシュを消しても最新画像を表示して回収する()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        var cache = new FrameCache(scope);
        var backend = new OwnedBackend();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int conversions = 0;
        async Task<byte[]> ConvertAsync(Y4mFrame frame, CancellationToken token)
        {
            int sequence = Interlocked.Increment(ref conversions);
            if (sequence == 1)
            {
                started.TrySetResult();
                await release.Task;
            }
            return Enumerable.Repeat(sequence == 1 ? (byte)11 : (byte)42,
                frame.Header.Width * frame.Header.Height * 4).ToArray();
        }
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        var vm = new MainWindowViewModel(new Services(), null, backend, null, cache, ConvertAsync,
            beforeEnabled: false, owned: dependencies)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "owned-injection.ts", InputArgsText = "--avhw"
        };
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await started.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            vm.RefreshCommand.Execute(null);
            release.TrySetResult();
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.Null(vm.ErrorText);
            var bitmap = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
            using var pixels = bitmap.Lock();
            Assert.Equal(42, Marshal.ReadByte(pixels.Address));
        }
        finally
        {
            release.TrySetResult();
            await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    [AvaloniaTheory]
    [InlineData(180)]
    [InlineData(240)]
    public async Task 反転と小さいキャッシュの連続操作で最後の画像を残し回収する(int cacheFrames)
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        var cache = new FrameCache(scope, 64 * 36 * 3L / 2 * cacheFrames);
        var backend = new TraceBackend();
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        var vm = new MainWindowViewModel(new Services(), TimeProvider.System, backend, null, cache,
            (frame, token) => Task.FromResult(Enumerable.Repeat(frame.RawPixels[0], frame.Header.Width * frame.Header.Height * 4).ToArray()),
            beforeEnabled: false, owned: dependencies)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "owned-trace.ts", InputArgsText = "--avhw", CurrentSeconds = 10
        };
        var firstShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ErrorText) && vm.ErrorText != null)
            {
                var error = new InvalidOperationException(vm.ErrorText);
                firstShown.TrySetException(error);
                return;
            }
            if (e.PropertyName != nameof(vm.PreviewImage) || vm.PreviewImage == null) return;
            firstShown.TrySetResult();
        };
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await backend.Started.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            backend.Release.TrySetResult();
            await firstShown.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            // 表示後の連続操作を、各入力の合間に完了を待たず投入する。
            foreach (double target in new[] { 10.5, 9.8, 10.2, 9.9, 10.4 }) vm.CurrentSeconds = target;
            await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.Null(vm.ErrorText);
            Assert.Equal(10.4, vm.CurrentSeconds, 6);
            Assert.True(vm.PreviewImage != null, $"状態:{vm.StatusText} エラー:{vm.ErrorText} busy:{vm.IsBusy}");
            var bitmap = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
            using var pixels = bitmap.Lock();
            Assert.Equal((byte)56, Marshal.ReadByte(pixels.Address));
            Assert.InRange(cache.SizeBytes, 0, 64 * 36 * 3L / 2 * cacheFrames);
        }
        finally
        {
            backend.Release.TrySetResult();
            await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    private sealed class TraceBackend : IPreviewBackend, IRawOwnedBackend
    {
        private int _runs;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("所有権のない経路は使えません。");
        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink, RawOwnershipScope scope,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _runs) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            var plan = CommandBuilder.Plan(request);
            int first = (int)Math.Round((plan.SeekSeconds ?? 0) * 30);
            long run = scope.NewRunId();
            for (int index = 0; index < Math.Max(plan.FrameCount, 180); index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var permission = sink.BeforeReadAsync(cancellationToken);
                if (!permission.IsCompleted) sink.OnReadStateChanged(false);
                await permission;
                sink.OnReadStateChanged(true);
                int tick = first + index;
                using var frame = scope.Create(header, request.Capabilities.TimestampPassthrough
                    ? (request.TimestampOriginSeconds ?? 0) + tick / 30.0 : index / 30.0, 1.0 / 30,
                    Enumerable.Repeat((byte)(tick & 255), header.FrameBytes).ToArray(), run, "試験の読取");
                sink.OnOutput();
                sink.OnFrame(frame.Frame);
            }
            return new PreviewRunResult(0, "");
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }



    [AvaloniaFact]
    public void 所有範囲の異なるキャッシュは受け付けない()
    {
        var scope = new RawOwnershipScope();
        var backend = new OwnedBackend();
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        Assert.Throws<ArgumentException>(() => new MainWindowViewModel(new Services(), null, backend, null,
            new FrameCache(new RawOwnershipScope()), null, owned: dependencies));
    }

    [AvaloniaFact]
    public void 所有未対応のバックエンドは受け付けない()
    {
        var scope = new RawOwnershipScope();
        var backend = new LegacyBackend();
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        Assert.Throws<ArgumentException>(() => new MainWindowViewModel(new Services(), null, backend, null,
            new FrameCache(scope), null, owned: dependencies));
    }

    private sealed class LegacyBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("利用できない経路です。");
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class OwnedBackend : IPreviewBackend, IRawOwnedBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("所有権のない経路は使えません。");
        public Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink, RawOwnershipScope scope,
            CancellationToken cancellationToken = default)
        {
            var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            long run = scope.NewRunId();
            for (int index = 0; index < 31; index++)
            {
                using var frame = scope.Create(header, index / 30.0, 1.0 / 30,
                    Enumerable.Repeat((byte)128, header.FrameBytes).ToArray(), run, "試験の読取");
                sink.OnFrame(frame.Frame);
            }
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), "tests", "FakeEncoder", "bin",
            configuration, "net10.0", OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }

    internal sealed class Services : IViewModelServices
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "viewer-owned-" + Guid.NewGuid().ToString("N"));
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
}

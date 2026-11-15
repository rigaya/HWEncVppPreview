using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class BeforeStaleResultTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 元画像の早期通知を配送する前の目標変更と入力解除に追従する(bool cancel)
    {
        string? previous = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope);
        var after = new ControlledBackend(false);
        var before = new ControlledBackend(true);
        var postSync = new object();
        var queuedPosts = new Queue<Action>();
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool holding = false;
        void Post(Action action)
        {
            lock (postSync)
            {
                if (holding) { queuedPosts.Enqueue(action); queued.TrySetResult(); return; }
            }
            Dispatcher.UIThread.Post(action);
        }
        void ReleasePosts()
        {
            Action[] actions;
            lock (postSync) { holding = false; actions = queuedPosts.ToArray(); queuedPosts.Clear(); }
            foreach (var action in actions) Dispatcher.UIThread.Post(action);
        }
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, Post);
        var vm = new MainWindowViewModel(new OwnedInjectionViewModelTests.Services(), TimeProvider.System, after,
            null, cache, (frame, token) => Task.FromResult(Enumerable.Repeat(frame.RawPixels[0],
                frame.Header.Width * frame.Header.Height * 4).ToArray()), before, owned: dependencies)
        {
            EncoderPath = OwnedInjectionViewModelTests.FakeEncoderPath(), InputPath = "early.ts", InputArgsText = "--avhw",
            CurrentSeconds = .5, CompareMode = PreviewCompareMode.SideBySide,
        };
        vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-tweak brightness=0.01" });
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await before.Started.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            lock (postSync) holding = true;
            before.Begin.TrySetResult();
            await before.Emitted.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await queued.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            if (cancel) vm.OpenInput("");
            else vm.CurrentSeconds = 1;
            ReleasePosts();
            before.Finish.TrySetResult();
            await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
            Assert.Null(vm.ErrorText);
            Assert.False(vm.IsBeforeBusy);
            if (cancel) Assert.Null(vm.BeforeImage);
            else
            {
                Assert.Equal(1, vm.CurrentSeconds);
                using var pixels = Assert.IsType<WriteableBitmap>(vm.BeforeImage).Lock();
                Assert.Equal((byte)30, Marshal.ReadByte(pixels.Address));
            }
        }
        finally
        {
            before.Begin.TrySetResult();
            before.Finish.TrySetResult();
            ReleasePosts();
            try { await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang); }
            finally { Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", previous); }
        }
        Assert.True(scope.Capture().IsEmpty);
    }

    private sealed class ControlledBackend(bool holdFirst) : IPreviewBackend, IRawOwnedBackend
    {
        private int _calls;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Begin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Emitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => throw new InvalidOperationException("所有権のない経路は使えません。");

        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink,
            RawOwnershipScope scope, CancellationToken token = default)
        {
            bool first = holdFirst && Interlocked.Increment(ref _calls) == 1;
            if (first)
            {
                Started.TrySetResult();
                await Begin.Task.WaitAsync(token);
            }
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            long run = scope.NewRunId();
            for (int index = 0; index < 61; index++)
            {
                byte value = (byte)index;
                using var frame = scope.Create(header, (request.TimestampOriginSeconds ?? 0) + index / 30d, 1d / 30,
                    new[] { value, value, value, value, (byte)128, (byte)128 }, run, "試験の読取");
                sink.OnFrame(frame.Frame);
                if (first && index == 16)
                {
                    Emitted.TrySetResult();
                    await Finish.Task.WaitAsync(token);
                }
            }
            return new PreviewRunResult(0, "");
        }

        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

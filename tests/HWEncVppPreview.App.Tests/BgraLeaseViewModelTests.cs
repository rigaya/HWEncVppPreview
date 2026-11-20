using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class BgraLeaseViewModelTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static Y4mFrame Frame() => new(new Y4mHeader { Width = 2, Height = 2 }, 1, 1, new byte[6]);
    private static MainWindowViewModel Create(BgraBufferPool pool)
    {
        var scope = new RawOwnershipScope();
        var backend = new OwnedInjectionViewModelTests.OwnedBackend();
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action), pool);
        return TestViewModels.Create(new OwnedInjectionViewModelTests.Services(), backend,
            null, new FrameCache(scope), null, owned: dependencies);
    }
    [AvaloniaFact]
    public async Task UIコピー完了まで返却せず入力変更で旧貸出を退役する()
    {
        var pool = new BgraBufferPool();
        await using var vm = Create(pool);
        var entered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = vm.WithConvertedBgraAsync(Frame(), new(), CancellationToken.None, async data =>
        { entered.TrySetResult(data); await copied.Task; });
        byte[] output;
        try
        {
            output = await entered.Task.WaitWithDiagnosticsAsync(Safety);
            vm.InputPath = "different-input.ts";
        }
        finally { copied.TrySetResult(); }
        await work.WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(16, output.Length);
        Assert.Equal(0, pool.Capture().Leased);
        Assert.Equal(0, pool.Capture().Buffers);
    }
    [AvaloniaTheory]
    [InlineData("cancel_before")]
    [InlineData("invalid_frame")]
    [InlineData("ui_exception")]
    [InlineData("ui_cancel")]
    [InlineData("stale_noop")]
    public async Task 取消と例外と世代棄却でも貸出が戻る(string reason)
    {
        var pool = new BgraBufferPool();
        await using var vm = Create(pool);
        using var cancellation = new CancellationTokenSource();
        if (reason == "cancel_before") cancellation.Cancel();
        var frame = reason == "invalid_frame" ? new Y4mFrame(Frame().Header, 0, 1, new byte[1]) : Frame();
        Task work = vm.WithConvertedBgraAsync(frame, new(), cancellation.Token, _ => reason switch
        {
            "ui_exception" => Task.FromException(new InvalidOperationException("UIコピー試験")),
            "ui_cancel" => Task.FromCanceled(new CancellationToken(true)),
            _ => Task.CompletedTask,
        });
        if (reason == "stale_noop") await work.WaitWithDiagnosticsAsync(Safety);
        else await Assert.ThrowsAnyAsync<Exception>(() => work).WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(0, pool.Capture().Leased);
        Assert.InRange(pool.Capture().Buffers, 0, 1);
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 実変換loopのUI完了と旧世代棄却で貸出を解放する(bool supersede)
    {
        var pool = new BgraBufferPool();
        var scope = new RawOwnershipScope();
        var backend = new ControlledOwnedBackend();
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new Queue<Action>();
        var gate = new object();
        bool hold = false;
        void Post(Action action)
        {
            lock (gate)
            {
                if (hold) { held.Enqueue(action); queued.TrySetResult(); return; }
            }
            Dispatcher.UIThread.Post(action);
        }
        void ReleasePosts()
        {
            Action[] actions;
            lock (gate) { hold = false; actions = held.ToArray(); held.Clear(); }
            foreach (var action in actions) Dispatcher.UIThread.Post(action);
        }
        var dependencies = new MainWindowViewModel.OwnedPreviewDependencies(scope, Post, pool);
        var vm = TestViewModels.Create(new OwnedInjectionViewModelTests.Services(), backend,
            null, new FrameCache(scope), null, beforeEnabled: false, owned: dependencies);
        vm.EncoderPath = OwnedInjectionViewModelTests.FakeEncoderPath();
        vm.InputPath = "a.ts";
        vm.InputArgsText = "--avhw";
        try
        {
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            vm.OpenInput("b.ts");
            await backend.SecondStarted.Task.WaitWithDiagnosticsAsync(Safety);
            lock (gate) hold = true;
            backend.ReleaseSecond.TrySetResult();
            await queued.Task.WaitWithDiagnosticsAsync(Safety);
            if (supersede) vm.OpenInput("c.ts");
            ReleasePosts();
            await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
            Assert.Null(vm.ErrorText);
            var bitmap = Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(vm.PreviewImage);
            using var pixels = bitmap.Lock();
            Assert.Equal(supersede ? (byte)255 : (byte)0, System.Runtime.InteropServices.Marshal.ReadByte(pixels.Address));
        }
        finally
        {
            backend.ReleaseSecond.TrySetResult();
            ReleasePosts();
            await vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        }
        Assert.Equal(0, pool.Capture().Leased);
        Assert.True(scope.Capture().IsEmpty);
    }

    private sealed class ControlledOwnedBackend : IPreviewBackend, IRawOwnedBackend
    {
        private int _runs;
        internal TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => throw new InvalidOperationException("所有権のない経路は使えません。");
        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink, RawOwnershipScope scope,
            CancellationToken token = default)
        {
            if (Interlocked.Increment(ref _runs) == 2)
            {
                SecondStarted.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(token);
            }
            var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            byte value = request.InputFile == "c.ts" ? (byte)235 : request.InputFile == "b.ts" ? (byte)16 : (byte)128;
            long run = scope.NewRunId();
            for (int index = 0; index < 31; index++)
            {
                using var frame = scope.Create(header, index / 30.0, 1.0 / 30,
                    new[] { value, value, value, value, (byte)128, (byte)128 }, run, "試験の読取");
                sink.OnFrame(frame.Frame);
            }
            return new PreviewRunResult(0, "");
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

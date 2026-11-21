using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class ActiveStreamOverlapViewModelTests
{
    [AvaloniaFact]
    public async Task ドラッグ確定後の背景取得を挟んでも連続移動の最新画像を表示する()
    {
        var clock = new FakeTimeProvider();
        await using var input = new SyntheticInputProbe(clock);
        var foreground = new SparseBackend(true);
        var background = new SparseBackend(false);
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope);
        var owned = new MainWindowViewModel.OwnedPreviewDependencies(scope, action => Dispatcher.UIThread.Post(action));
        var vm = new MainWindowViewModel(null, clock, foreground, input.Probe, cache,
            (frame, _) => Task.FromResult(Enumerable.Repeat(frame.RawPixels[0], frame.Header.Width * frame.Header.Height * 4).ToArray()),
            backgroundBackend: background, owned: owned, debounceOverride: TimeSpan.Zero,
            readTextOverride: (_, arg, _) => Task.FromResult(arg == "--help" ? "  --timestamp-passthrough input timestamps\n" : "FakeEncoder"))
        { EncoderPath = "fake", InputPath = "fake.ts", CurrentSeconds = 10 };
        byte resultPixel = 0;
        double resultSeconds = 0;
        string? resultError = null;
        try
        {
            await vm.InitializeAsync(false);
            await foreground.Started.Task;
            // 最初の出力前にドラッグを確定して同じ入力のBefore取得を登録する。
            vm.BeginSeekDrag();
            vm.EndSeekDrag(10);
            foreground.Release.TrySetResult();
            await foreground.Paused.Task;
            background.Release.TrySetResult();
            await background.Done.Task;
            await vm.WaitForIdleAsync();

            vm.CurrentSeconds = 12.2;
            await vm.WaitForIdleAsync();
            resultError = vm.ErrorText;
            resultSeconds = vm.CurrentSeconds;
            using var pixels = Assert.IsType<WriteableBitmap>(vm.PreviewImage).Lock();
            resultPixel = Marshal.ReadByte(pixels.Address);
        }
        finally
        {
            foreground.Release.TrySetResult();
            background.Release.TrySetResult();
            await vm.DisposeAsync().AsTask();
            Assert.True(scope.Capture().IsEmpty);
        }
        Assert.Null(resultError);
        Assert.Equal(12.2, resultSeconds, 6);
        Assert.Equal(80, resultPixel);
    }

    private sealed class SparseBackend(bool streaming) : IPreviewBackend, IRawOwnedBackend
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => throw new InvalidOperationException("所有権付き経路を使う。");
        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink, RawOwnershipScope scope, CancellationToken token = default)
        {
            Started.TrySetResult();
            var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            long run = scope.NewRunId();
            try
            {
                await Release.Task.WaitAsync(token);
                for (int i = 0; i < 100; i++)
                {
                    var permission = sink.BeforeReadAsync(token);
                    if (!permission.IsCompleted) { sink.OnReadStateChanged(false); Paused.TrySetResult(); }
                    await permission;
                    sink.OnReadStateChanged(true);
                    sink.OnOutput();
                    double time = streaming ? (i == 0 ? 10 : 12.1 + (i - 1) * 0.1) : 10 + i * 0.1;
                    byte pixel = streaming && i > 1 ? (byte)80 : (byte)40;
                    using var frame = scope.Create(header, 1000 + time, 0.3,
                        Enumerable.Repeat(pixel, header.FrameBytes).ToArray(), run, "試験の読取");
                    sink.OnFrame(frame.Frame);
                    if (!streaming && i == 2) break;
                }
                Done.TrySetResult();
                return new(0, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(-12, "", true); }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

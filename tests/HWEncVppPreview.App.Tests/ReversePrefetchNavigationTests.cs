using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class ReversePrefetchNavigationTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;

    [AvaloniaFact]
    public async Task PrefetchFinishesBeforeTheBoundaryWithoutReplacingForegroundImageOrStatus()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.StartPrefetchAsync();
        double seconds = fixture.Vm.CurrentSeconds;
        var image = fixture.Vm.PreviewImage;
        string status = fixture.Vm.StatusText;
        string log = fixture.Vm.LogText;
        fixture.Backend.Release.TrySetResult();
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(seconds, fixture.Vm.CurrentSeconds);
        Assert.Same(image, fixture.Vm.PreviewImage);
        Assert.Equal(status, fixture.Vm.StatusText);
        Assert.Equal(log, fixture.Vm.LogText);
        Assert.Single(fixture.Backend.Requests, IsPrefetch);
        Assert.Equal(2, fixture.Backend.Requests.Count);
        Assert.Null(fixture.Vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task BoundaryRepeatsWaitForOnePrefetchAndResolveFreshCachedNeighbor()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.StartPrefetchAsync();
        for (int i = 0; i < 25; ++i) await fixture.StepAsync(-1);
        fixture.Vm.StepFrameCommand.Execute(-1);

        for (int i = 0; i < 20; ++i) fixture.Vm.StepFrameCommand.Execute(-1);
        fixture.Backend.Release.TrySetResult();
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(9 - 1.0 / 30, fixture.Vm.CurrentSeconds, 6);
        Assert.Equal(2, fixture.Backend.Requests.Count);
        Assert.False(fixture.Vm.IsBusy);
        Assert.Null(fixture.Vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task ForwardTurnDoesNotCancelPrefetchOrApplyItsOldReverseWait()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.StartPrefetchAsync();
        for (int i = 0; i < 25; ++i) await fixture.StepAsync(-1);
        fixture.Vm.StepFrameCommand.Execute(-1);

        await fixture.StepAsync(1);
        double forward = fixture.Vm.CurrentSeconds;
        fixture.Backend.Release.TrySetResult();
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(9 + 1.0 / 30, forward, 6);
        Assert.Equal(forward, fixture.Vm.CurrentSeconds);
        Assert.DoesNotContain(fixture.Backend.Cancelled, IsPrefetch);
        Assert.Equal(2, fixture.Backend.Requests.Count);
        Assert.Null(fixture.Vm.ErrorText);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeSeekOrEditCancelsOnlyTheObsoletePrefetch(bool edit)
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.StartPrefetchAsync();
        if (edit) fixture.Vm.FilterLines.Add(new FilterLineViewModel { Text = "--vpp-nlmeans" });
        else fixture.Vm.CurrentSeconds = 30;
        await fixture.Vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        if (edit) fixture.Clock.Advance(TimeSpan.FromMilliseconds(150));
        try
        {
            await fixture.Backend.PrefetchCancelled.Task.WaitWithDiagnosticsAsync(Safety);
        }
        catch (Exception exception)
        {
            string requests = string.Join(" | ", fixture.Backend.Requests.Select(request =>
                $"id={request.CorrelationId}, target={request.TargetSeconds:R}, input={request.InputFile}"));
            string cancelled = string.Join(" | ", fixture.Backend.Cancelled.Select(request =>
                $"id={request.CorrelationId}, target={request.TargetSeconds:R}"));
            throw new InvalidOperationException(
                $"古い逆先読みの取消を観測できません: edit={edit}, current={fixture.Vm.CurrentSeconds:R}, "
                + $"busy={fixture.Vm.IsBusy}, duration={fixture.Vm.DurationSeconds:R}, error={fixture.Vm.ErrorText}, "
                + $"started={fixture.Backend.Started.Task.Status}, release={fixture.Backend.Release.Task.Status}, "
                + $"requests=[{requests}], cancelled=[{cancelled}]", exception);
        }
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Null(fixture.Vm.ErrorText);
        if (!edit) Assert.Equal(30, fixture.Vm.CurrentSeconds, 6);
        Assert.Single(fixture.Backend.Requests, IsPrefetch);
    }

    [AvaloniaFact]
    public async Task PixelMismatchDropsOldAnchorAndUsesOneNormalFiniteRefetch()
    {
        await using var fixture = await Fixture.StartAsync(mismatch: true);
        await fixture.StartPrefetchAsync();
        double previous = fixture.Vm.CurrentSeconds;
        fixture.Backend.Release.TrySetResult();
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        fixture.Vm.StepFrameCommand.Execute(-1);
        await fixture.Vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(previous - 1.0 / 30, fixture.Vm.CurrentSeconds, 6);
        Assert.Equal(3, fixture.Backend.Requests.Count);
        Assert.False(fixture.Backend.Requests.Last().Continuous);
        Assert.True(fixture.Backend.Requests.Last().Capabilities.TimestampPassthrough);
        Assert.Null(fixture.Vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task ReachingInputStartDoesNotPrefetchOrRequestNegativeFrames()
    {
        await using var fixture = await Fixture.StartAsync(seconds: 0.2);
        for (int i = 0; i < 6; ++i) await fixture.StepAsync(-1);
        for (int i = 0; i < 5; ++i) fixture.Vm.StepFrameCommand.Execute(-1);
        await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(0, fixture.Vm.CurrentSeconds, 6);
        Assert.False(fixture.Vm.IsBusy);
        Assert.DoesNotContain(fixture.Backend.Requests, IsPrefetch);
        Assert.Single(fixture.Backend.Requests);
    }

    private static bool IsPrefetch(PreviewRequest request) => request.CorrelationId < long.MinValue / 2;
    private static async Task WaitForPropertyAsync(MainWindowViewModel vm, Func<bool> condition)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => { if (condition()) ready.TrySetResult(); };
        vm.PropertyChanged += changed;
        try
        {
            if (condition()) ready.TrySetResult();
            await ready.Task.WaitWithDiagnosticsAsync(Safety);
        }
        finally { vm.PropertyChanged -= changed; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Services _services = new();
        public FakeTimeProvider Clock { get; } = new();
        public PrefetchBackend Backend { get; }
        public MainWindowViewModel Vm { get; }
        private bool _processAttempted;
        private Fixture(bool mismatch, double seconds)
        {
            Backend = new PrefetchBackend(mismatch);
            var probe = TestProcesses.CreateInputProbe(new ProbeBackend(), Clock, _ =>
            {
                _processAttempted = true;
                throw new InvalidOperationException("この試験ではプロセスを起動しません。");
            });
            Vm = new MainWindowViewModel(_services, Clock, Backend, probe, null, null,
                beforeEnabled: false, readTextOverride: (_, argument, _) => Task.FromResult(argument == "--help"
                    ? "  --timestamp-passthrough  入力時刻を保持する。\n  --opencl-task-threads <int>  待機スレッド数。\n" : "FakeEncoder"))
            { EncoderPath = "fake", InputPath = "reverse-prefetch.ts", CurrentSeconds = seconds,
                InputArgsText = "--avhw --fake-duration 00:01:00.00" };
        }
        public static async Task<Fixture> StartAsync(bool mismatch = false, double seconds = 10)
        {
            var fixture = new Fixture(mismatch, seconds);
            try
            {
                await fixture.Vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(150));
                await fixture.Backend.Paused.Task.WaitWithDiagnosticsAsync(Safety);
                await fixture.Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async Task StepAsync(int direction)
        {
            var previous = Vm.PreviewImage;
            Vm.StepFrameCommand.Execute(direction);
            await Vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
            await WaitForPropertyAsync(Vm, () => Vm.PreviewImage != null && !ReferenceEquals(Vm.PreviewImage, previous) && !Vm.IsBusy);
        }
        public async Task StartPrefetchAsync()
        {
            for (int i = 0; i < 5; ++i) await StepAsync(-1);
            await Backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        }
        public async ValueTask DisposeAsync()
        {
            Backend.Release.TrySetResult();
            await Vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
            Assert.False(_processAttempted);
            _services.Dispose();
        }
    }

    private sealed class PrefetchBackend(bool mismatch) : IPreviewBackend
    {
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public ConcurrentQueue<PreviewRequest> Cancelled { get; } = new();
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PreviewRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PrefetchCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Enqueue(request);
            try
            {
                if (IsPrefetch(request))
                {
                    Started.TrySetResult(request);
                    await Release.Task.WaitAsync(token).ConfigureAwait(false);
                }
                var plan = CommandBuilder.Plan(request);
                double seek = plan.SeekSeconds ?? 0;
                var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420jpeg");
                sink.OnHeader(header);
                int count = request.Continuous ? int.MaxValue : plan.FrameCount;
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    ValueTask permission = sink.BeforeReadAsync(token);
                    if (!permission.IsCompleted)
                    {
                        sink.OnReadStateChanged(false);
                        Paused.TrySetResult();
                        await permission.AsTask().WaitAsync(token).ConfigureAwait(false);
                    }
                    else await permission.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    sink.OnReadStateChanged(true);
                    long index = (long)Math.Round(seek * 30) + i;
                    byte[] pixels = new byte[header.FrameBytes];
                    for (int j = 0; j < pixels.Length; j++) pixels[j] = unchecked((byte)(j + index));
                    if (mismatch && IsPrefetch(request)) pixels[0] ^= 0xff;
                    sink.OnFrame(new Y4mFrame(header, 1000 + index / 30.0, 1.0 / 30, pixels));
                    sink.OnOutput();
                }
                return new PreviewRunResult(0, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Cancelled.Enqueue(request);
                if (IsPrefetch(request)) PrefetchCancelled.TrySetResult();
                throw;
            }
        }
        public ValueTask AbortAsync() { Release.TrySetResult(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => AbortAsync();
    }

    private sealed class ProbeBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420jpeg");
            sink.OnHeader(header);
            sink.OnFrame(new Y4mFrame(header, 1000, 1.0 / 30, new byte[header.FrameBytes]));
            return Task.FromResult(new PreviewRunResult(0, "  Duration: 00:01:00.00, start: 0"));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "FakeEncoder", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }

    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "viewer-reverse-prefetch-" + Guid.NewGuid().ToString("N"));
        public Services() => Directory.CreateDirectory(_directory);
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}

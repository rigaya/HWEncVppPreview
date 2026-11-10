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
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public async Task OneReverseStepThenForwardKeepsPausedPhysicalStream()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.StepAsync(-1);
        Assert.Single(fixture.Backend.Requests);
        Assert.Empty(fixture.Backend.Cancelled);
        await fixture.StepAsync(1);
        Assert.Equal(10, fixture.Vm.CurrentSeconds, 6);
        Assert.Single(fixture.Backend.Requests);
        Assert.Empty(fixture.Backend.Cancelled);
    }

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
        await fixture.Vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(seconds, fixture.Vm.CurrentSeconds);
        Assert.Same(image, fixture.Vm.PreviewImage);
        Assert.Equal(status, fixture.Vm.StatusText);
        Assert.Equal(log, fixture.Vm.LogText);
        for (int i = 0; i < 30; ++i) await fixture.StepAsync(-1);
        Assert.Equal(10 - 35.0 / 30, fixture.Vm.CurrentSeconds, 6);
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
        Assert.Equal(9, fixture.Vm.CurrentSeconds, 6);
        fixture.Vm.StepFrameCommand.Execute(-1);
        Assert.True(fixture.Vm.IsBusy);
        for (int i = 0; i < 20; ++i) fixture.Vm.StepFrameCommand.Execute(-1);
        Assert.Equal(9, fixture.Vm.CurrentSeconds, 6);
        Assert.Equal(2, fixture.Backend.Requests.Count);
        fixture.Backend.Release.TrySetResult();
        await fixture.Vm.WaitForIdleAsync().WaitAsync(Safety);
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
        Assert.True(fixture.Vm.IsBusy);
        await fixture.StepAsync(1);
        double forward = fixture.Vm.CurrentSeconds;
        Assert.Equal(9 + 1.0 / 30, forward, 6);
        Assert.DoesNotContain(fixture.Backend.Cancelled, IsPrefetch);
        fixture.Backend.Release.TrySetResult();
        await fixture.Vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(forward, fixture.Vm.CurrentSeconds);
        Assert.DoesNotContain(fixture.Backend.Cancelled, IsPrefetch);
        Assert.Equal(2, fixture.Backend.Requests.Count);
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
        await fixture.Vm.WaitForPreparationAsync().WaitAsync(Safety);
        if (edit) fixture.Clock.Advance(TimeSpan.FromMilliseconds(150));
        try
        {
            await PumpUntilAsync(() => fixture.Backend.Cancelled.Any(IsPrefetch)).WaitAsync(Safety);
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
        await fixture.Vm.WaitForIdleAsync().WaitAsync(Safety);
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
        await fixture.Vm.WaitForIdleAsync().WaitAsync(Safety);
        fixture.Vm.StepFrameCommand.Execute(-1);
        await fixture.Vm.WaitForPreparationAsync().WaitAsync(Safety);
        await fixture.Vm.WaitForIdleAsync().WaitAsync(Safety);
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
        Assert.Equal(0, fixture.Vm.CurrentSeconds, 6);
        for (int i = 0; i < 5; ++i) fixture.Vm.StepFrameCommand.Execute(-1);
        await fixture.Vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(0, fixture.Vm.CurrentSeconds, 6);
        Assert.False(fixture.Vm.IsBusy);
        Assert.DoesNotContain(fixture.Backend.Requests, IsPrefetch);
        Assert.Single(fixture.Backend.Requests);
    }

    private static bool IsPrefetch(PreviewRequest request) => request.CorrelationId < long.MinValue / 2;
    private static Task PumpUntilAsync(Func<bool> condition) => UiTestWait.UntilAsync(condition, Safety);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _capabilities = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        private readonly Services _services = new();
        public FakeTimeProvider Clock { get; } = new();
        public PrefetchBackend Backend { get; }
        public MainWindowViewModel Vm { get; }
        private Fixture(bool mismatch, double seconds)
        {
            Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
            Backend = new PrefetchBackend(Clock, mismatch);
            var background = new OneShotBackend(timeProvider: Clock);
            Vm = new MainWindowViewModel(_services, Clock, Backend, Backend, null, null, null, background, background)
            { EncoderPath = FakeEncoderPath(), InputPath = "reverse-prefetch.ts", CurrentSeconds = seconds,
                InputArgsText = "--avhw --fake-duration 00:01:00.00" };
        }
        public static async Task<Fixture> StartAsync(bool mismatch = false, double seconds = 10)
        {
            var fixture = new Fixture(mismatch, seconds);
            try
            {
                await fixture.Vm.InitializeAsync(false).WaitAsync(Safety);
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(150));
                await fixture.Backend.Paused.Task.WaitAsync(Safety);
                await fixture.Vm.WaitForIdleAsync().WaitAsync(Safety);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async Task StepAsync(int direction)
        {
            Vm.StepFrameCommand.Execute(direction);
            await Vm.WaitForPreparationAsync().WaitAsync(Safety);
            await PumpUntilAsync(() => !Vm.IsBusy).WaitAsync(Safety);
        }
        public async Task StartPrefetchAsync()
        {
            for (int i = 0; i < 5; ++i) await StepAsync(-1);
            await Backend.Started.Task.WaitAsync(Safety);
        }
        public async ValueTask DisposeAsync()
        {
            Backend.Release.TrySetResult();
            await Vm.DisposeAsync().AsTask().WaitAsync(Safety);
            Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", _capabilities);
            _services.Dispose();
        }
    }

    private sealed class PrefetchBackend(TimeProvider clock, bool mismatch) : IPreviewBackend
    {
        private readonly ServerBackend _inner = new(timeProvider: clock);
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public ConcurrentQueue<PreviewRequest> Cancelled { get; } = new();
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PreviewRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Enqueue(request);
            using var registration = token.Register(() => Cancelled.Enqueue(request));
            if (IsPrefetch(request))
            {
                Started.TrySetResult(request);
                await Release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            return await _inner.RunAsync(request, new ObservingSink(this, sink, mismatch && IsPrefetch(request)), token).ConfigureAwait(false);
        }
        public ValueTask AbortAsync() => _inner.AbortAsync();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        private sealed class ObservingSink(PrefetchBackend owner, IPreviewSink inner, bool mismatch) : IPreviewSink
        {
            public void OnHeader(Y4mHeader header) => inner.OnHeader(header);
            public void OnOutput() => inner.OnOutput();
            public ValueTask BeforeReadAsync(CancellationToken token) => inner.BeforeReadAsync(token);
            public void OnReadStateChanged(bool reading)
            { if (!reading) owner.Paused.TrySetResult(); inner.OnReadStateChanged(reading); }
            public void OnFrame(Y4mFrame frame)
            {
                if (mismatch)
                {
                    var data = (byte[])frame.Data.Clone();
                    data[0] ^= 0xff;
                    frame = new(frame.Header, frame.Xts, frame.Xdur, data);
                }
                inner.OnFrame(frame);
            }
        }
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

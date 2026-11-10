using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class StreamNeighborReuseViewModelTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyForwardNeighborReusesReadingStream(bool centerOnly)
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(centerOnly);
        await using var vm = Create(services, clock, backend);
        await Start(vm, clock, backend);
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        backend.Next.TrySetResult();
        await Until(() => Math.Abs(vm.CurrentSeconds - 10.37) < 1e-8 && !vm.IsBusy);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        // 入力30fpsの推測値ではなく、実VFRの隣10.37へ1回だけ進む。
        Assert.Single(backend.Requests);
        Assert.False(backend.FirstCancelled.Task.IsCompleted);
        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.PreviewImage);
    }
    [AvaloniaFact]
    public async Task ExplicitRefreshStillStartsAnotherRun()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(false);
        await using var vm = Create(services, clock, backend);
        await Start(vm, clock, backend);
        vm.RefreshCommand.Execute(null);
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await backend.SecondStarted.Task.WaitWithDiagnosticsAsync(Safety);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
        Assert.True(backend.FirstCancelled.Task.IsCompleted);
        Assert.Null(vm.ErrorText);
    }
    [AvaloniaFact]
    public async Task MissingReverseNeighborStillRefetchesFiniteWindow()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        var backend = new ControlledBackend(true);
        await using var vm = Create(services, clock, backend);
        await Start(vm, clock, backend);
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await Until(() => Math.Abs(vm.CurrentSeconds - 9.5) < 1e-8 && !vm.IsBusy);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
        Assert.False(backend.Requests.Last().Continuous);
        Assert.True(backend.FirstCancelled.Task.IsCompleted);
        Assert.Null(vm.ErrorText);
    }
    private static MainWindowViewModel Create(Services services, FakeTimeProvider clock, ControlledBackend backend)
        => new(services, clock, backend, backend)
        { EncoderPath = FakeEncoderPath(), InputPath = "c0b-neighbor.ts", CurrentSeconds = 10, InputArgsText = "--avhw" };
    private static async Task Start(MainWindowViewModel vm, FakeTimeProvider clock, ControlledBackend backend)
    {
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await backend.Center.Task.WaitWithDiagnosticsAsync(Safety);
        await Until(() => vm.PreviewImage != null && !vm.IsBusy);
        Assert.Single(backend.Requests);
        Assert.Equal(10, vm.CurrentSeconds);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Safety);
        while (!predicate()) await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitAsync(timeout.Token);
    }
    private sealed class ControlledBackend(bool centerOnly) : IPreviewBackend
    {
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public TaskCompletionSource Center { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Next { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            int call = Interlocked.Increment(ref _calls);
            Requests.Enqueue(request);
            if (call > 1) SecondStarted.TrySetResult();
            using var registration = token.Register(() => { if (call == 1) FirstCancelled.TrySetResult(); });
            var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            var times = new List<double>();
            if (!(call == 1 && centerOnly)) for (double time = seek; time < 10 - 1e-8; time += 0.5) times.Add(time);
            times.AddRange(new[] { 10d, 10.37, 10.9, 11.4, 12.1 });
            try
            {
                int index = 0;
                for (;;)
                {
                    var permission = sink.BeforeReadAsync(token);
                    if (!permission.IsCompleted) sink.OnReadStateChanged(false);
                    await permission;
                    sink.OnReadStateChanged(true);
                    token.ThrowIfCancellationRequested();
                    double time = index < times.Count ? times[index] : 12.1 + (index - times.Count + 1) * 0.5;
                    double duration = index + 1 < times.Count ? times[index + 1] - time : 0.5;
                    sink.OnOutput();
                    sink.OnFrame(new(header, 1000 + time, duration, Enumerable.Repeat((byte)((int)Math.Round(time * 100) % 251), header.FrameBytes).ToArray()));
                    ++index;
                    if (call == 1 && time == 10) { Center.TrySetResult(); await Next.Task.WaitAsync(token); }
                    if (!request.Continuous && index >= times.Count) return new(0, "");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // WaitAsyncの取消が先に再開するとregistrationを破棄できるため、観測はcatchでも確定する。
                if (call == 1) FirstCancelled.TrySetResult();
                return new(-12, "", true);
            }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CapabilityScope : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        public CapabilityScope() => Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
        public void Dispose() => Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", _old);
    }
    private static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "FakeEncoder", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }
    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-c0b-{Guid.NewGuid():N}");
        public Services() => Directory.CreateDirectory(_directory);
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose() => Directory.Delete(_directory, true);
    }
}

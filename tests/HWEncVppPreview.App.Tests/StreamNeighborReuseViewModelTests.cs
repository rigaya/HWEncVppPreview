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
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyForwardNeighborReusesReadingStream(bool centerOnly)
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        await using var input = new SyntheticInputProbe(clock);
        var backend = new ControlledBackend(centerOnly);
        await using var vm = Create(services, clock, input, backend);
        await Start(vm, clock, backend);
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForPreparationAsync();
        backend.Next.TrySetResult();
        await vm.WaitForIdleAsync();
        // 入力30fpsの推測値ではなく、実VFRの隣へ進んだ最終時刻を見る。
        Assert.Equal(10.37, vm.CurrentSeconds, 8);
        Assert.Single(backend.Requests);
        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.PreviewImage);
    }
    [AvaloniaFact]
    public async Task ExplicitRefreshStillStartsAnotherRun()
    {
        using var capability = new CapabilityScope();
        using var services = new Services();
        var clock = new FakeTimeProvider();
        await using var input = new SyntheticInputProbe(clock);
        var backend = new ControlledBackend(false);
        await using var vm = Create(services, clock, input, backend);
        await Start(vm, clock, backend);
        vm.RefreshCommand.Execute(null);
        await vm.WaitForPreparationAsync();
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await backend.SecondStarted.Task;
        await vm.WaitForIdleAsync();
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
        await using var input = new SyntheticInputProbe(clock);
        var backend = new ControlledBackend(true);
        await using var vm = Create(services, clock, input, backend);
        await Start(vm, clock, backend);
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForPreparationAsync();
        await vm.WaitForIdleAsync();
        Assert.Equal(9.5, vm.CurrentSeconds, 8);
        Assert.Equal(2, backend.Requests.Count);
        Assert.False(backend.Requests.Last().Continuous);
        Assert.True(backend.FirstCancelled.Task.IsCompleted);
        Assert.Null(vm.ErrorText);
    }
    private static MainWindowViewModel Create(Services services, FakeTimeProvider clock, SyntheticInputProbe input, ControlledBackend backend)
        => new(services, clock, backend, input.Probe, null, null, readTextOverride: SyntheticInputProbe.ReadTextAsync)
        { EncoderPath = FakeEncoderPath(), InputPath = "c0b-neighbor.ts", CurrentSeconds = 10, InputArgsText = "--avhw" };
    private static async Task Start(MainWindowViewModel vm, FakeTimeProvider clock, ControlledBackend backend)
    {
        var displayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler? changed = null;
        changed = (_, args) =>
        {
            if (args.PropertyName != nameof(vm.PreviewImage)) return;
            vm.PropertyChanged -= changed;
            displayed.TrySetResult();
        };
        vm.PropertyChanged += changed;
        try
        {
            await vm.InitializeAsync(false);
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await backend.Center.Task;
            await displayed.Task;
            Assert.Single(backend.Requests);
            Assert.Equal(10, vm.CurrentSeconds);
        }
        finally { vm.PropertyChanged -= changed; }
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

using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.App.Tests;

public sealed class SeekIntentViewModelTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(15);

    [AvaloniaTheory]
    [InlineData("slider")]
    [InlineData("time")]
    [InlineData("home")]
    [InlineData("page")]
    [InlineData("seconds")]
    [InlineData("release")]
    [InlineData("end")]
    public async Task BackwardSeekUsesForwardPhysicalPlan(string operation)
    {
        using var fixture = new Fixture();
        await using var vm = fixture.Create();
        await fixture.Start(vm);
        switch (operation)
        {
            case "end": vm.GoToEndCommand.Execute(null); break;
            case "release": vm.BeginSeekDrag(); vm.EndSeekDrag(20); break;
            case "slider": vm.BeginSeekDrag(); vm.CurrentSeconds = 20; vm.EndSeekDrag(20); break;
            case "time": vm.CurrentTimeText = "00:00:20"; vm.CommitTimeTextCommand.Execute(null); break;
            case "home": vm.GoToStartCommand.Execute(null); break;
            case "page": vm.StepSecondsCommand.Execute(-80.0); break;
            case "seconds": vm.CurrentSeconds = 20; break;
        }
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        var request = fixture.Backend.Requests.Last(value => value.CorrelationId >= 0);
        double target = operation == "home" ? 0 : operation == "end" ? 180 : 20;
        Assert.Equal(target, request.TargetSeconds);
        Assert.True(request.Continuous);
        Assert.Equal(0, request.WarmupSeconds);
        Assert.Equal(target == 0 ? null : target - 1, CommandBuilder.Plan(request).SeekSeconds);
        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.PreviewImage);
    }

    [AvaloniaFact]
    public async Task PageWithinConnectedCacheReusesThePhysicalStream()
    {
        using var fixture = new Fixture();
        await using var vm = fixture.Create();
        await fixture.Start(vm);
        vm.StepSecondsCommand.Execute(-0.1);
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Single(fixture.Backend.Requests);
        Assert.Equal(99.9, vm.CurrentSeconds, 6);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task BackwardFramesAfterSeekStartReversePrefetchOnlyWhenNeeded()
    {
        using var fixture = new Fixture();
        await using var vm = fixture.Create();
        await fixture.Start(vm);
        vm.BeginSeekDrag(); vm.CurrentSeconds = 20; vm.EndSeekDrag(20);
        await vm.WaitForPreparationAsync().WaitAsync(Safety);
        await vm.WaitForIdleAsync().WaitAsync(Safety);
        Assert.Equal(2, fixture.Backend.Requests.Count);
        Assert.All(fixture.Backend.Requests, request => Assert.True(request.Continuous));
        for (int i = 0; i < 6; i++)
        {
            vm.StepFrameCommand.Execute(-1);
            await vm.WaitForIdleAsync().WaitAsync(Safety);
        }
        await Until(() => fixture.Backend.Requests.Any(request => request.CorrelationId < 0));
        var prefetch = Assert.Single(fixture.Backend.Requests, request => request.CorrelationId < 0);
        Assert.False(prefetch.Continuous);
        Assert.Equal(5, prefetch.PreMarginSeconds);
        Assert.Equal(2, prefetch.WarmupSeconds);
        Assert.Null(vm.ErrorText);
        Assert.True(vm.CurrentSeconds < 20);
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Safety);
        while (!predicate()) await Dispatcher.UIThread.InvokeAsync(() => { }).GetTask().WaitAsync(timeout.Token);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _oldCapabilities = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        public FakeTimeProvider Clock { get; } = new();
        public RecordingBackend Backend { get; } = new();
        private readonly Services _services = new();
        public Fixture() => Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
        public MainWindowViewModel Create() => new(_services, Clock, Backend, Backend)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "seek-intent.ts", CurrentSeconds = 100,
            InputArgsText = "--avhw --fake-duration 00:03:00.00",
        };
        public async Task Start(MainWindowViewModel vm)
        {
            await vm.InitializeAsync(false).WaitAsync(Safety);
            Clock.Advance(TimeSpan.FromMilliseconds(150));
            await vm.WaitForIdleAsync().WaitAsync(Safety);
            Assert.Single(Backend.Requests);
            Assert.Null(vm.ErrorText);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", _oldCapabilities);
            _services.Dispose();
        }
    }

    private sealed class RecordingBackend : IPreviewBackend
    {
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Enqueue(request);
            var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            var plan = CommandBuilder.Plan(request);
            int start = (int)Math.Round((plan.SeekSeconds ?? 0) * 30);
            try
            {
                for (int i = 0; request.Continuous || i < plan.FrameCount; i++)
                {
                    var permission = sink.BeforeReadAsync(token);
                    if (!permission.IsCompleted) sink.OnReadStateChanged(false);
                    await permission;
                    sink.OnReadStateChanged(true);
                    token.ThrowIfCancellationRequested();
                    sink.OnOutput();
                    sink.OnFrame(new(header, 1000 + (start + i) / 30.0, 1.0 / 30,
                        Enumerable.Repeat((byte)80, header.FrameBytes).ToArray()));
                }
                return new(0, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(-12, "", true); }
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
        return Path.Combine(directory.FullName, "tests", "FakeEncoder", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }

    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-seek-intent-{Guid.NewGuid():N}");
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

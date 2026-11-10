using System.Reflection;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class SafeBoundaryViewModelTests
{
    [AvaloniaTheory]
    [InlineData(false, 0.000000333333333, true)]
    [InlineData(false, 0.0000015, true)]
    [InlineData(true, 0.0000015, true)]
    [InlineData(false, 0.000003, false)]
    [InlineData(true, 0.000003, false)]
    public async Task BeforeAndNavigationUseTheSameSafeBoundary(bool lowerBoundary, double error, bool expected)
    {
        using var services = new Services();
        var backend = new UnusedBackend();
        await using var vm = new MainWindowViewModel(services, new FakeTimeProvider(), backend, backend);
        var request = new PreviewRequest
        {
            EncoderPath = "encoder", InputFile = "rounded.ts", TargetSeconds = 5, Fps = 30,
            InputDurationSeconds = 20, TimestampOriginSeconds = 0, Capabilities = new(true, true),
        };
        var header = Y4mHeader.Parse("YUV4MPEG2 W2 H2 F24:1 C420");
        var frames = new[] { new Y4mFrame(header, 5, null, new byte[6]) };
        double start = lowerBoundary ? 5 + error : 4;
        double end = lowerBoundary ? 6 : 5 - error;
        var segment = new PreviewSegmentSnapshot(1, request, new(0, 2, true), frames, start, end, false, false);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // 公開APIを増やさず、Before再利用とコマ送りの最終safe判定を同じ区間で確認する。
        var before = typeof(MainWindowViewModel).GetMethod("ContainsBeforeFrameTarget", flags)!;
        Assert.Equal(expected, before.Invoke(vm, new object[] { request, frames, 5d, segment }));
        typeof(MainWindowViewModel).GetField("_intervalSegment", flags)!.SetValue(vm, segment);
        var navigation = typeof(MainWindowViewModel).GetMethod("FrameIsSafe", flags)!;
        Assert.Equal(expected, navigation.Invoke(vm, new object[] { 5d }));
        Assert.Equal(start, segment.SafeStartSeconds);
        Assert.Equal(end, segment.SafeEndSeconds);
        Assert.Equal(5d, frames[0].Xts);
        Assert.Null(frames[0].Xdur);
    }

    private sealed class UnusedBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("このテストではエンコーダを起動しない。");
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-safe-{Guid.NewGuid():N}");
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

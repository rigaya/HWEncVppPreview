using System.Reflection;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
namespace HWEncVppPreview.App.Tests;

public sealed class EncoderPreparationViewModelTests
{
    [AvaloniaFact]
    public async Task MetadataCompletesWhileEncoderInitializationIsStillPending()
    {
        var backend = new HeldInitializationBackend();
        await using var vm = new MainWindowViewModel(new Services(), null, backend, backend, null, null, null)
        { EncoderPath = FakeEncoderPath() };
        try
        {
            await vm.InitializeAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
            await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task metadata = (Task)typeof(MainWindowViewModel).GetField("_metadataTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
            await metadata.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(vm.HelpSchema);
            Assert.False(backend.Release.Task.IsCompleted);
        }
        finally { backend.Release.TrySetResult(); }
        await vm.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class HeldInitializationBackend : IPreviewBackend
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeEncoderAsync(string encoderPath, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); return Release.Task.WaitAsync(cancellationToken); }
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("入力を開く前なのでRunは呼ばれない");
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
    private sealed class Services : IViewModelServices
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "viewer-metadata-unused-" + Guid.NewGuid().ToString("N"));
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
}

using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

internal static class FailureViewModelFixture
{
    internal static MainWindowViewModel Create(string diagnostics, int exitCode, Exception? exception = null)
    {
        var backend = new FailureBackend(diagnostics, exitCode, exception);
        var vm = TestViewModels.Create(new Services(), backend);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "diagnostics.ts";
        vm.InputArgsText = "--avhw";
        return vm;
    }

    internal static async Task RunAsync(MainWindowViewModel vm)
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.ErrorText)) failed.TrySetResult();
        };
        await vm.InitializeAsync(false);
        await failed.Task;
        await vm.WaitForIdleAsync();
    }

    private sealed class FailureBackend(string diagnostics, int exitCode, Exception? exception) : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => exception == null ? Task.FromResult(new PreviewRunResult(exitCode, diagnostics))
                : Task.FromException<PreviewRunResult>(new PreviewBackendException("処理に失敗しました。", diagnostics, exception));
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), "tests", "FakeEncoder", "bin",
            configuration, "net10.0", OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }

    private sealed class Services : IViewModelServices
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "viewer-diagnostics-" + Guid.NewGuid().ToString("N"));
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
}

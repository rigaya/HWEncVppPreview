using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.App.Tests;

public sealed class DurationTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public async Task ExitDurationDoesNotDelayImageOrReplaceImageStateAndSurvivesEditingAndSeeking()
    {
        using var files = new TestFiles();
        var clock = new FakeTimeProvider();
        int starts = 0;
        await using var probe = new InputProbe(clock, configureStartInfo: info =>
        {
            if (!IsDuration(info)) return;
            Interlocked.Increment(ref starts);
            HoldDuration(info, files);
        });
        await using var vm = Create(files, clock, probe);
        vm.CurrentSeconds = 15;
        var line = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.1" };
        vm.FilterLines.Add(line);
        await PrepareAsync(vm, clock);
        await WaitForFileAsync(files.Ready);
        await WaitUntilAsync(vm, () => vm.PreviewImage != null && !vm.IsBusy);
        Assert.False(vm.HasDuration);
        Assert.Equal(15, vm.CurrentSeconds);
        var image = vm.PreviewImage;
        string status = vm.StatusText, log = vm.LogText, elapsed = vm.ElapsedText;
        await File.WriteAllTextAsync(files.Gate, "");
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        Assert.True(vm.HasDuration);
        Assert.Equal(10, vm.DurationSeconds);
        Assert.Equal("0:00:10", vm.DurationText);
        Assert.Equal(15, vm.CurrentSeconds);
        Assert.Same(image, vm.PreviewImage);
        Assert.Equal(status, vm.StatusText);
        Assert.Equal(log, vm.LogText);
        Assert.Equal(elapsed, vm.ElapsedText);
        Assert.Null(vm.ErrorText);
        line.Text = "--vpp-nlmeans sigma=0.2";
        await vm.WaitForPreparationAsync().WaitAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        vm.CurrentSeconds = 2;
        await vm.WaitForPreparationAsync().WaitAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        Assert.Equal(1, starts);
        Assert.True(vm.HasDuration);
    }

    [AvaloniaFact]
    public async Task FilterAndSeekDoNotCancelOrDuplicatePendingDuration()
    {
        using var files = new TestFiles();
        var clock = new FakeTimeProvider();
        int starts = 0;
        await using var probe = new InputProbe(clock, configureStartInfo: info =>
        {
            if (!IsDuration(info)) return;
            Interlocked.Increment(ref starts);
            HoldDuration(info, files);
        });
        await using var vm = Create(files, clock, probe);
        await PrepareAsync(vm, clock);
        await WaitForFileAsync(files.Ready);
        vm.FilterLines.Add(new() { Text = "--vpp-nlmeans sigma=0.1" });
        await vm.WaitForPreparationAsync().WaitAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await WaitUntilAsync(vm, () => vm.PreviewImage != null && !vm.IsBusy);
        vm.CurrentSeconds = 2;
        await vm.WaitForPreparationAsync().WaitAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await WaitUntilAsync(vm, () => !vm.IsBusy);
        Assert.Equal(1, starts);
        Assert.False(vm.HasDuration);
        await File.WriteAllTextAsync(files.Gate, "");
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        Assert.Equal(10, vm.DurationSeconds);
        Assert.Equal(2, vm.CurrentSeconds);
        Assert.Equal(1, starts);
    }

    [AvaloniaFact]
    public async Task NonzeroDurationExitStillUpdatesLengthWithoutChangingPreviewDiagnostics()
    {
        using var files = new TestFiles();
        var clock = new FakeTimeProvider();
        await using var probe = new InputProbe(clock, configureStartInfo: info =>
        {
            if (IsDuration(info)) { info.ArgumentList.Add("--fake-rc"); info.ArgumentList.Add("23"); }
        });
        await using var vm = Create(files, clock, probe);
        await PrepareAsync(vm, clock);
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        Assert.True(vm.HasDuration);
        Assert.Equal(10, vm.DurationSeconds);
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
        Assert.DoesNotContain("rc=23", vm.LogText);
    }

    [AvaloniaFact]
    public async Task InputChangeCancelsAndReapsOldDurationAndRejectsItsResult()
    {
        using var files = new TestFiles();
        var clock = new FakeTimeProvider();
        await using var probe = new InputProbe(clock, configureStartInfo: info =>
        {
            if (!IsDuration(info)) return;
            if (info.ArgumentList.Contains("old.ts")) HoldDuration(info, files);
            else
            {
                int index = info.ArgumentList.IndexOf("--fake-duration");
                info.ArgumentList[index + 1] = "00:00:20.00";
            }
        });
        await using var vm = Create(files, clock, probe);
        await PrepareAsync(vm, clock);
        await WaitForFileAsync(files.Ready);
        int pid = int.Parse(await File.ReadAllTextAsync(files.Pid));
        vm.InputPath = "new.ts";
        await vm.WaitForPreparationAsync().WaitAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        AssertStopped(pid);
        Assert.True(vm.HasDuration);
        Assert.Equal(20, vm.DurationSeconds);
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
        await File.WriteAllTextAsync(files.Gate, "");
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        Assert.Equal(20, vm.DurationSeconds);
    }

    [AvaloniaFact]
    public async Task DisposeCancelsAndReapsPendingDuration()
    {
        using var files = new TestFiles();
        var clock = new FakeTimeProvider();
        await using var probe = new InputProbe(clock, configureStartInfo: info => { if (IsDuration(info)) HoldDuration(info, files); });
        var vm = Create(files, clock, probe);
        try
        {
            await PrepareAsync(vm, clock);
            await WaitForFileAsync(files.Ready);
            int pid = int.Parse(await File.ReadAllTextAsync(files.Pid));
            await vm.DisposeAsync().AsTask().WaitAsync(SafetyTimeout);
            AssertStopped(pid);
            Assert.False(vm.HasDuration);
            Assert.Null(vm.PreviewImage);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task KnownProbeDurationDoesNotStartAnotherProcess()
    {
        using var files = new TestFiles();
        var clock = new FakeTimeProvider();
        int starts = 0;
        await using var probe = new InputProbe(clock, configureStartInfo: _ => Interlocked.Increment(ref starts));
        await using var vm = Create(files, clock, probe);
        vm.InputArgsText = "--avhw --fake-duration 00:00:10.00";
        await PrepareAsync(vm, clock);
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        Assert.True(vm.HasDuration);
        Assert.Equal(10, vm.DurationSeconds);
        Assert.Equal(1, starts);
    }

    private static bool IsDuration(ProcessStartInfo info)
    {
        int index = info.ArgumentList.IndexOf("--frames");
        return index >= 0 && info.ArgumentList[index + 1] == "1";
    }
    private static void HoldDuration(ProcessStartInfo info, TestFiles files)
    {
        info.ArgumentList.Add("--fake-duration-exit-gate");
        info.ArgumentList.Add(files.Gate);
        info.Environment["FAKE_ENCODER_DURATION_EXIT_READY_FILE"] = files.Ready;
        info.Environment["FAKE_ENCODER_PID_FILE"] = files.Pid;
    }
    private static MainWindowViewModel Create(TestFiles files, FakeTimeProvider clock, InputProbe probe)
    {
        var backend = new OneShotBackend();
        return new(files, clock, preferred: backend, fallback: backend, probe: probe)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "old.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00 --fake-duration-at-exit",
        };
    }
    private static async Task PrepareAsync(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        await vm.InitializeAsync(false).WaitAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
    }
    private static async Task WaitUntilAsync(MainWindowViewModel vm, Func<bool> condition)
    {
        if (condition()) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, _) => { if (condition()) completion.TrySetResult(); };
        vm.PropertyChanged += changed;
        try { await completion.Task.WaitAsync(SafetyTimeout); }
        finally { vm.PropertyChanged -= changed; }
    }
    private static async Task WaitForFileAsync(string path)
    {
        async Task WaitAsync() { while (!File.Exists(path)) await Task.Yield(); }
        await WaitAsync().WaitAsync(SafetyTimeout);
    }
    private static void AssertStopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited, $"子プロセス{pid}が残っています"); }
        catch (ArgumentException) { }
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
    private sealed class TestFiles : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-duration-{Guid.NewGuid():N}");
        public TestFiles() => Directory.CreateDirectory(_directory);
        public string Gate => Path.Combine(_directory, "release");
        public string Ready => Path.Combine(_directory, "ready");
        public string Pid => Path.Combine(_directory, "pid");
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}

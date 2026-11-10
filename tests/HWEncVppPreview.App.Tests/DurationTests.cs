using HWEncVppPreview.Testing;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

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
        // 元画像の静止待ちも偽時計で進め、Durationの進行とは分けて確認する。
        clock.Advance(TimeSpan.FromMilliseconds(300));
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
        // 元画像の静止待ちも偽時計で進め、Durationの進行とは分けて確認する。
        clock.Advance(TimeSpan.FromMilliseconds(300));
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
        int pid = int.Parse(Assert.Single(FakeLogFiles.ReadAllLines(files.Pid)));
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
        var probe = new InputProbe(clock, configureStartInfo: info => { if (IsDuration(info)) HoldDuration(info, files); });
        var vm = Create(files, clock, probe);
        Exception? failure = null;
        try
        {
            await PrepareAsync(vm, clock);
            await WaitForFileAsync(files.Ready);
            int pid = int.Parse(Assert.Single(FakeLogFiles.ReadAllLines(files.Pid)));
            await vm.DisposeAsync().AsTask().WaitAsync(SafetyTimeout);
            AssertStopped(pid);
            Assert.False(vm.HasDuration);
            Assert.Null(vm.PreviewImage);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            // 偽時計が進まない失敗時も、同じDisposeの再待機を実時間で打ち切る。
            try
            {
                try { await vm.DisposeAsync().AsTask().WaitAsync(SafetyTimeout); }
                finally { await probe.DisposeAsync().AsTask().WaitAsync(SafetyTimeout); }
            }
            catch (Exception cleanupFailure) when (failure != null)
            {
                // 後始末の失敗で本体の検証失敗を隠さず、診断情報として保持する。
                failure.Data["DurationCleanupFailure"] = cleanupFailure;
            }
        }
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
    [AvaloniaFact]
    public async Task OmittedBeforeStartsDurationAfterTheAfterImage()
    {
        using var files = new TestFiles();
        var clock = new FakeTimeProvider();
        var probe = HeldDurationProbe(files, clock);
        var backend = new OneShotBackend();
        await using var vm = new MainWindowViewModel(files, clock, backend, backend, probe, null, null, beforeEnabled: false)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "no-before.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00 --fake-duration-at-exit"
        };
        try
        {
            await vm.InitializeAsync(false).WaitAsync(SafetyTimeout);
            await WaitForFileAsync(files.Ready);
            Assert.NotNull(vm.PreviewImage);
            Assert.Null(vm.BeforeImage);
        }
        finally { await File.WriteAllTextAsync(files.Gate, ""); }
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        Assert.True(vm.HasDuration);
    }

    [AvaloniaTheory]
    [InlineData(19)]
    [InlineData(0)]
    public async Task MissingOrFailedBeforeDoesNotPreventDuration(int beforeExitCode)
    {
        using var files = new TestFiles();
        var clock = new FakeTimeProvider();
        var probe = HeldDurationProbe(files, clock);
        var after = new OneShotBackend();
        var before = new MissingBeforeBackend(beforeExitCode);
        await using var vm = new MainWindowViewModel(files, clock, after, after, probe, null, null, before, before)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "missing-before.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00 --fake-duration-at-exit"
        };
        vm.FilterLines.Add(new() { Text = "--vpp-tweak brightness=0.01" });
        try
        {
            await vm.InitializeAsync(false).WaitAsync(SafetyTimeout);
            await WaitForFileAsync(files.Ready);
            Assert.NotNull(vm.PreviewImage);
            Assert.Null(vm.BeforeImage);
            Assert.Null(vm.ErrorText);
        }
        finally { await File.WriteAllTextAsync(files.Gate, ""); }
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        Assert.True(vm.HasDuration);
    }

    [AvaloniaFact]
    public async Task ComparisonFramesAcrossNavigationGenerationsStartDuration()
    {
        using var files = new TestFiles();
        string? old = Environment.GetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES");
        Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
        var clock = new FakeTimeProvider();
        var probe = HeldDurationProbe(files, clock);
        var after = new DelayedStreamBackend();
        var before = new DurationBeforeBackend();
        var secondBefore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int beforeConversions = 0;
        async Task<byte[]> ConvertAsync(Y4mFrame frame, CancellationToken token)
        {
            if (frame.Data[0] == 16 && Interlocked.Increment(ref beforeConversions) > 1)
                await secondBefore.Task.WaitAsync(token);
            return YuvToBgra.Convert(frame);
        }
        await using var vm = new MainWindowViewModel(files, clock, after, after, probe, null, ConvertAsync, before, before)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "cross-generation.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00 --fake-duration-at-exit"
        };
        vm.FilterLines.Add(new() { Text = "--vpp-tweak brightness=0.01" });
        try
        {
            await vm.InitializeAsync(false).WaitAsync(SafetyTimeout);
            await WaitUntilAsync(vm, () => vm.BeforeImage != null);
            Assert.Null(vm.PreviewImage);
            Assert.False(File.Exists(files.Ready));
            vm.CurrentSeconds = 0.2;
            await vm.WaitForPreparationAsync().WaitAsync(SafetyTimeout);
            after.Release.TrySetResult();
            await WaitForFileAsync(files.Ready);
            Assert.NotNull(vm.PreviewImage);
            Assert.NotNull(vm.BeforeImage);
            Assert.Equal(0.2, vm.CurrentSeconds, 6);
        }
        finally
        {
            after.Release.TrySetResult();
            secondBefore.TrySetResult();
            await File.WriteAllTextAsync(files.Gate, "");
            Environment.SetEnvironmentVariable("FAKE_ENCODER_PREVIEW_CAPABILITIES", old);
        }
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
        Assert.True(vm.HasDuration);
    }

    private static InputProbe HeldDurationProbe(TestFiles files, FakeTimeProvider clock) => new(clock, configureStartInfo: info =>
    {
        if (IsDuration(info)) HoldDuration(info, files);
        else { info.ArgumentList.Add("--fake-hang-after"); info.ArgumentList.Add("1"); }
    });
    private sealed class MissingBeforeBackend(int exitCode) : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => Task.FromResult(new PreviewRunResult(exitCode, "元画像がないテスト"));
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static Y4mFrame DurationFrame(Y4mHeader header, double seconds, byte y)
    {
        byte[] data = Enumerable.Repeat((byte)128, header.FrameBytes).ToArray();
        Array.Fill(data, y, 0, header.Width * header.Height);
        return new(header, 1000 + seconds, 1.0 / 30, data);
    }
    private sealed class DurationBeforeBackend : IPreviewBackend
    {
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420");
            sink.OnHeader(header);
            for (int index = 0; index < 31; index++) sink.OnFrame(DurationFrame(header, index / 30.0, 16));
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class DelayedStreamBackend : IPreviewBackend
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(false);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            try
            {
                await Release.Task.WaitAsync(token);
                var header = Y4mHeader.Parse("YUV4MPEG2 W64 H36 F30:1 Ip A1:1 C420");
                sink.OnHeader(header);
                for (int index = 0; ; index++)
                {
                    var permission = sink.BeforeReadAsync(token);
                    if (!permission.IsCompleted) sink.OnReadStateChanged(false);
                    await permission.AsTask().WaitAsync(token);
                    sink.OnReadStateChanged(true);
                    token.ThrowIfCancellationRequested();
                    sink.OnFrame(DurationFrame(header, index / 30.0, 48));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(-12, "", true); }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        void Check(object? sender, FileSystemEventArgs args) { if (File.Exists(path)) ready.TrySetResult(); }
        watcher.Created += Check;
        watcher.Changed += Check;
        watcher.EnableRaisingEvents = true;
        // UIへの反映を待ってから長さ検査が始まるので、Yield連打でUIのPostを飢餓させない。
        if (File.Exists(path)) ready.TrySetResult();
        await ready.Task.WaitAsync(SafetyTimeout);
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

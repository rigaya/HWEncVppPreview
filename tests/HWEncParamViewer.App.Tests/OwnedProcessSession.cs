using HWEncParamViewer.Testing;
using System.Diagnostics;
using Avalonia.Threading;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.App.Tests;

internal sealed class OwnedProcessSession : IAsyncDisposable
{
    internal static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);
    private readonly Dictionary<string, string?> _environment = new();
    private readonly OwnedVmCheck _check;
    private readonly SignalClock _signalClock;
    private readonly bool _beforeEnabled;
    private readonly CancellationTokenSource _clockPumpStop = new();
    private readonly Task? _clockPump;
    private bool _disposed;
    internal MainWindowViewModel Vm { get; }
    internal FakeTimeProvider Clock { get; } = new();
    internal RawOwnershipScope Scope => OwnedVmCheck.Scope(Vm)!;
    internal FrameCache Cache => (FrameCache)OwnedVmCheck.Field(Vm, "_cache")!;
    internal PreviewController Controller => (PreviewController)OwnedVmCheck.Field(Vm, "_controller")!;
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "owned-process-" + Guid.NewGuid().ToString("N"));
    internal string CommandLogPath => Path.Combine(DirectoryPath, "commands.jsonl");
    internal string PidPath => Path.Combine(DirectoryPath, "pids.txt");
    internal static OwnedProcessSession Create(string inputArgs = "--avhw", string? filterText = null,
        double target = 10, bool oneShot = false, bool beforeEnabled = true)
        => new(inputArgs, filterText, target, oneShot, beforeEnabled);

    private OwnedProcessSession(string inputArgs, string? filterText, double target, bool oneShot, bool beforeEnabled)
    {
        Directory.CreateDirectory(DirectoryPath);
        _beforeEnabled = beforeEnabled;
        SetEnvironment("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
        SetEnvironment("FAKE_ENCODER_READY_ENCODER", "QSVEnc");
        SetEnvironment("FAKE_ENCODER_PROBE_SUPPORTED", "1");
        SetEnvironment("FAKE_ENCODER_PROBE_HISTORY", "0");
        SetEnvironment("FAKE_ENCODER_WINDOW_SEEK", "1");
        SetEnvironment("FAKE_ENCODER_COMMAND_LOG_FILE", CommandLogPath);
        SetEnvironment("FAKE_ENCODER_PID_FILE", PidPath);
        SetEnvironment("FAKE_ENCODER_READY_NONE", oneShot ? "1" : null);
        _signalClock = new SignalClock(Clock);
        if (oneShot)
        {
            _clockPump = Task.Run(async () =>
            {
                try
                {
                    await foreach (bool _ in _signalClock.ReadyTimers.Reader.ReadAllAsync(_clockPumpStop.Token))
                    {
                        await Task.Yield();
                        Clock.Advance(TimeSpan.FromSeconds(3));
                    }
                }
                catch (OperationCanceledException) when (_clockPumpStop.IsCancellationRequested) { }
            });
        }
        Vm = new MainWindowViewModel(new Services(Path.Combine(DirectoryPath, "settings.json")), _signalClock,
            null, null, null, null, null, beforeEnabled: beforeEnabled)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "owned-process.ts", InputArgsText = inputArgs,
            CurrentSeconds = target, IsShowingBefore = beforeEnabled,
        };
        if (filterText != null)
        {
            Vm.FilterLines.Add(new FilterLineViewModel { Text = filterText });
        }
        _check = new OwnedVmCheck(Vm, requirePixels: true);
    }

    internal void SetEnvironment(string name, string? value)
    {
        if (!_environment.ContainsKey(name))
        {
            _environment[name] = Environment.GetEnvironmentVariable(name);
        }
        Environment.SetEnvironmentVariable(name, value);
    }

    internal async Task InitializeAsync()
    {
        Task initialize = Vm.InitializeAsync(false);
        await initialize.WaitAsync(Safety);
        await Vm.WaitForPreparationAsync().WaitAsync(Safety);
        Clock.Advance(TimeSpan.FromMilliseconds(150));
    }

    internal async Task WaitIdleAsync()
    {
        await Vm.WaitForPreparationAsync().WaitAsync(Safety);
        Clock.Advance(TimeSpan.FromMilliseconds(150));
        if (_beforeEnabled)
        {
            Vm.IsShowingBefore = true;
        }
        await Vm.WaitForIdleAsync().WaitAsync(Safety);
    }

    internal Y4mFrame? DisplayedFrame(bool before = false)
        => (Y4mFrame?)OwnedVmCheck.Field(Vm, before ? "_beforeDisplayedFrame" : "_displayedFrame");

    internal async Task WaitForImageAsync(bool before, Func<Y4mFrame, bool> ready)
    {
        bool IsReady() => DisplayedFrame(before) is { } frame && ready(frame);
        if (IsReady())
        {
            return;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler changed = (_, value) =>
        {
            if (value.PropertyName == (before ? nameof(Vm.BeforeImage) : nameof(Vm.PreviewImage)) && IsReady())
                completion.TrySetResult();
        };
        Vm.PropertyChanged += changed;
        try
        {
            if (IsReady())
            {
                completion.TrySetResult();
            }
            await completion.Task.WaitAsync(Safety);
        }
        finally
        {
            Vm.PropertyChanged -= changed;
        }
    }

    internal async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(Safety);
        while (!ready())
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask().WaitAsync(timeout.Token);
    }

    internal static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "FakeEncoder", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }

    internal static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.Contains("FakeEncoder", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            await _check.DisposeAsync();
            if (FakeLogFiles.Exists(PidPath))
            {
                foreach (string line in FakeLogFiles.ReadAllLines(PidPath))
                    if (int.TryParse(line, out int pid)) Assert.False(IsRunning(pid), $"FakeEncoderが残っている: {pid}");
            }
        }
        finally
        {
            _clockPumpStop.Cancel();
            if (_clockPump != null)
            {
                await _clockPump;
            }
            _clockPumpStop.Dispose();
            foreach (var pair in _environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Directory.Delete(DirectoryPath, true);
        }
    }

    private sealed class Services(string settingsPath) : IViewModelServices
    {
        public string SettingsPath => settingsPath;
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }

    private sealed class SignalClock(FakeTimeProvider clock) : TimeProvider
    {
        internal global::System.Threading.Channels.Channel<bool> ReadyTimers { get; }
            = global::System.Threading.Channels.Channel.CreateUnbounded<bool>();
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override long GetTimestamp() => clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(3)) ReadyTimers.Writer.TryWrite(true);
            return timer;
        }
    }
}

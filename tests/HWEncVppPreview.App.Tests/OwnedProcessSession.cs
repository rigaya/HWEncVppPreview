using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Testing;

namespace HWEncVppPreview.App.Tests;

// 実プロセスの試験では、時計を進めず公開された完了境界と所有の回収を待つ。
internal sealed class OwnedProcessSession : IAsyncDisposable
{
    internal static readonly TimeSpan Safety = TestTimeouts.Hang;
    private readonly Dictionary<string, string?> _environment = new();
    private bool _disposed;
    private readonly object _postLock = new();
    private readonly Queue<Action> _heldPosts = new();
    private TaskCompletionSource? _postArrived;
    private bool _holdingPosts;
    internal MainWindowViewModel Vm { get; }
    internal RawOwnershipScope Scope { get; } = new(new RawBufferPool());
    internal FrameCache Cache { get; }
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "owned-process-" + Guid.NewGuid().ToString("N"));
    private string PidPath => Path.Combine(DirectoryPath, "pids.txt");
    private string CommandPath => Path.Combine(DirectoryPath, "commands.jsonl");

    internal static OwnedProcessSession Create(string inputArgs = "--avhw", string? filterText = null,
        double target = 10, bool beforeEnabled = true, string? durationPipe = null)
        => new(inputArgs, filterText, target, beforeEnabled, durationPipe);

    private OwnedProcessSession(string inputArgs, string? filterText, double target, bool beforeEnabled, string? durationPipe)
    {
        Directory.CreateDirectory(DirectoryPath);
        SetEnvironment("FAKE_ENCODER_PREVIEW_CAPABILITIES", "1");
        SetEnvironment("FAKE_ENCODER_READY_ENCODER", "QSVEnc");
        SetEnvironment("FAKE_ENCODER_PROBE_SUPPORTED", "1");
        SetEnvironment("FAKE_ENCODER_PROBE_HISTORY", "0");
        SetEnvironment("FAKE_ENCODER_WINDOW_SEEK", "1");
        SetEnvironment("FAKE_ENCODER_PID_FILE", PidPath);
        SetEnvironment("FAKE_ENCODER_COMMAND_LOG_FILE", CommandPath);
        Cache = new FrameCache(Scope);
        IPreviewBackend first = TestProcesses.CreateServerBackend();
        IPreviewBackend? beforeFirst = beforeEnabled ? TestProcesses.CreateServerBackend() : null;
        var owned = new MainWindowViewModel.OwnedPreviewDependencies(Scope, Post);
        InputProbe? probe = durationPipe == null ? null : TestProcesses.CreateInputProbe(configureStartInfo: info =>
        {
            int index = info.ArgumentList.IndexOf("--frames");
            if (index >= 0 && index + 1 < info.ArgumentList.Count && info.ArgumentList[index + 1] == "1")
            {
                info.ArgumentList.Add("--fake-duration-exit-pipe");
                info.ArgumentList.Add(durationPipe);
            }
        });
        Vm = TestViewModels.Create(new Services(Path.Combine(DirectoryPath, "settings.json")),
            first, probe, Cache, null, backgroundBackend: beforeFirst,
            beforeEnabled: beforeEnabled, owned: owned, debounceOverride: TimeSpan.Zero);
        Vm.EncoderPath = FakeEncoderPath();
        Vm.InputPath = "owned-process.ts";
        Vm.InputArgsText = inputArgs;
        Vm.CurrentSeconds = target;
        Vm.IsShowingBefore = beforeEnabled;
        if (filterText != null) Vm.FilterLines.Add(new FilterLineViewModel { Text = filterText });
    }

    private void Post(Action action)
    {
        lock (_postLock)
        {
            if (_holdingPosts)
            {
                _heldPosts.Enqueue(action);
                _postArrived!.TrySetResult();
                return;
            }
        }
        Dispatcher.UIThread.Post(action);
    }

    internal void HoldPosts()
    {
        lock (_postLock)
        {
            _postArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _holdingPosts = true;
        }
    }

    internal Task WaitForPostAsync()
    {
        lock (_postLock) return _postArrived!.Task.WaitWithDiagnosticsAsync(Safety);
    }

    internal void ReleasePosts()
    {
        Action[] actions;
        lock (_postLock)
        {
            _holdingPosts = false;
            actions = _heldPosts.ToArray();
            _heldPosts.Clear();
        }
        foreach (var action in actions) Dispatcher.UIThread.Post(action);
    }

    internal void SetEnvironment(string name, string? value)
    {
        if (!_environment.ContainsKey(name)) _environment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    internal async Task InitializeAsync()
    {
        await Vm.InitializeAsync(false).WaitWithDiagnosticsAsync(Safety);
        await Vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
    }

    internal async Task WaitIdleAsync()
    {
        await Vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(Safety);
        await Vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
    }

    internal byte[] ImageBytes(bool before = false)
    {
        var image = Assert.IsAssignableFrom<Bitmap>(before ? Vm.BeforeImage : Vm.PreviewImage);
        byte[] bytes = new byte[image.PixelSize.Width * image.PixelSize.Height * 4];
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { image.CopyPixels(new PixelRect(image.PixelSize), pinned.AddrOfPinnedObject(), bytes.Length, image.PixelSize.Width * 4); }
        finally { pinned.Free(); }
        return bytes;
    }

    // 入力検査のrunも常駐経由になるため、表示用runだけを数える。
    internal string[][] CompletedRuns() => FakeLogFiles.ReadAllLines(CommandPath).Select(line =>
    {
        using var json = System.Text.Json.JsonDocument.Parse(line);
        return json.RootElement.Clone();
    }).Where(root => root.GetProperty("cmd").GetString() == "run")
        .Select(root => root.GetProperty("args").EnumerateArray().Select(item => item.GetString()!).ToArray())
        .Where(args => args.Contains(Vm.InputPath) && !args.Contains("input=debug")).ToArray();

    internal static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "FakeEncoder", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            ReleasePosts();
            await Vm.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
            Assert.True(Scope.Capture().IsEmpty);
            if (FakeLogFiles.Exists(PidPath))
                foreach (string line in FakeLogFiles.ReadAllLines(PidPath))
                    if (int.TryParse(line, out int pid)) Assert.False(IsRunning(pid), $"偽エンコーダが残っています: {pid}");
        }
        finally
        {
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
}

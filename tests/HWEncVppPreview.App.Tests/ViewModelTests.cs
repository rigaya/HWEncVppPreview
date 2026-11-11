using System.Collections.Specialized;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.App.Views;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class ViewModelTests
{
    private static readonly TimeSpan SafetyTimeout = TestTimeouts.Hang;

    [AvaloniaTheory]
    [InlineData(15, false, 0)]
    [InlineData(15, true, 0)]
    [InlineData(5, false, 5)]
    [InlineData(5, true, 5)]
    [InlineData(10, false, 10)]
    [InlineData(0, true, 0)]
    public async Task RestoredStartupTimeOutsideInputReturnsToZero(double savedTime, bool durationAtExit, double expectedTime)
    {
        using var services = new TestServices();
        new AppSettings
        {
            EncoderPath = FakeEncoderPath(), InputPath = "startup.ts", CurrentSeconds = savedTime,
            InputArgsText = "--fake-duration 00:00:10.00" + (durationAtExit ? " --fake-duration-at-exit" : ""),
        }.Save(services.SettingsPath);
        var commands = new List<string[]>();
        var backend = new OneShotBackend(info => commands.Add(info.ArgumentList.ToArray()));
        await using var vm = new MainWindowViewModel(services, preferred: backend, fallback: backend);
        await vm.InitializeAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(expectedTime, vm.CurrentSeconds);
        Assert.Equal(10, vm.DurationSeconds);
        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.PreviewImage);
        Assert.NotEmpty(commands);
        if (expectedTime == 0) Assert.DoesNotContain("--seek", commands[0]);
    }

    [AvaloniaFact]
    public async Task OpensWindowAndShowsARealFrameFromFakeEncoder()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        await using var vm = Create(services, clock);
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            await InitializeAsync(vm, clock);
            var image = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
            Assert.Equal(64, image.PixelSize.Width);
            Assert.Equal(36, image.PixelSize.Height);
            Assert.Equal(64, vm.DisplayWidth);
            Assert.Equal(36, vm.DisplayHeight);
            Assert.True(vm.HasDuration);
            Assert.Equal(10, vm.DurationSeconds);
            Assert.False(vm.IsBusy);
            Assert.Null(vm.ErrorText);
            using var rendered = window.CaptureRenderedFrame();
            Assert.NotNull(rendered);
            Assert.True(rendered.PixelSize.Width > 0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FilterEditingAndCheckboxChangeRequestAndImage()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        int starts = 0;
        await using var vm = Create(services, clock, new OneShotBackend(_ => ++starts));
        var line = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.1" };
        vm.FilterLines.Add(line);
        vm.SelectedFilterLine = line;
        await InitializeAsync(vm, clock);
        string first = ImageHash(vm);
        Assert.Contains("--vpp-nlmeans", vm.HelpText);
        // 初回はフィルタ後と、空のVPP引数で取得するフィルタ前の2回。
        Assert.Equal(2, starts);
        line.Text = "--vpp-nlmeans sigma=0.2";
        await RunPreparedAsync(vm, clock);
        // フィルタ前の同一区間はキャッシュから再利用する。
        Assert.Equal(3, starts);
        Assert.NotEqual(first, ImageHash(vm));
        line.IsEnabled = false;
        await RunPreparedAsync(vm, clock);
        Assert.Equal(3, starts);
        line.IsEnabled = true;
        await RunPreparedAsync(vm, clock);
        // 完了区間がキャッシュされていれば、再度有効にしても追加起動しない。
        Assert.Equal(3, starts);
    }

    [AvaloniaFact]
    public async Task AdjacentFrameUsesCacheAndRefreshBypassesIt()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        int starts = 0;
        await using var vm = Create(services, clock, new OneShotBackend(_ => ++starts));
        vm.CurrentSeconds = 0.3;
        await InitializeAsync(vm, clock);
        string first = ImageHash(vm);
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(1, starts);
        Assert.Equal(10 / 30.0, vm.CurrentSeconds, 7);
        Assert.NotEqual(first, ImageHash(vm));
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(1, starts);
        Assert.Equal(0.3, vm.CurrentSeconds, 7);
        vm.RefreshCommand.Execute(null);
        await RunPreparedAsync(vm, clock);
        Assert.Equal(2, starts);
    }

    [AvaloniaFact]
    public async Task SeekDragRequestsFramesBeforeReleaseAndTimeFormatsAreAccepted()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        int starts = 0;
        await using var vm = Create(services, clock, new OneShotBackend(_ => ++starts));
        await InitializeAsync(vm, clock);
        vm.BeginSeekDrag();
        vm.CurrentSeconds = 2;
        vm.CurrentSeconds = 3;
        // ドラッグを離す前に最新位置の要求を完了でき、編集用の待機時間は不要。
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True(starts >= 2);
        Assert.Equal(3, vm.CurrentSeconds);
        Assert.NotNull(vm.PreviewImage);
        vm.EndSeekDrag(3);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(3, vm.CurrentSeconds);
        foreach (var item in new[] { ("0:04", 4.0), ("0:00:05.500", 5.5), ("6.25", 6.25) })
        {
            vm.CurrentTimeText = item.Item1;
            vm.CommitTimeTextCommand.Execute(null);
            await RunPreparedAsync(vm, clock);
            Assert.Equal(item.Item2, vm.CurrentSeconds, 7);
        }
        vm.CurrentTimeText = "invalid";
        vm.CommitTimeTextCommand.Execute(null);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.NotNull(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task EncoderErrorShowsDiagnosticsAndKeepsAnImage()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        await using var vm = Create(services, clock);
        var line = new FilterLineViewModel();
        vm.FilterLines.Add(line);
        await InitializeAsync(vm, clock);
        Assert.NotNull(vm.PreviewImage);
        line.Text = "--fake-rc 19";
        await RunPreparedAsync(vm, clock);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("19", vm.ErrorText);
        Assert.Contains("FAKE requested rc=19", vm.LogText);
        Assert.NotNull(vm.PreviewImage);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task SettingsFiltersClipboardAndFileCommandsRoundTrip()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        await using (var vm = Create(services, clock))
        {
            vm.FilterLines.Add(new() { Text = "--vpp-nlmeans sigma=0.1" });
            vm.FilterLines.Add(new() { Text = "--vpp-edgelevel strength=10", IsEnabled = false });
            await InitializeAsync(vm, clock);
            vm.CopyCommandLineCommand.Execute(null);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.Contains("--vpp-nlmeans", services.ClipboardText);
            Assert.DoesNotContain("--vpp-edgelevel", services.ClipboardText);
            await vm.SaveSettingsAsync(1200, 780);
        }
        await using (var restored = new MainWindowViewModel(services, clock))
        {
            await restored.InitializeAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await restored.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.Equal(FakeEncoderPath(), restored.EncoderPath);
            Assert.Equal("fake input.ts", restored.InputPath);
            Assert.Equal(1200, restored.WindowWidth);
            Assert.Equal(780, restored.WindowHeight);
            Assert.Equal(2, restored.FilterLines.Count);
            Assert.False(restored.FilterLines[1].IsEnabled);
            restored.SaveFiltersCommand.Execute(null);
            await restored.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.True(File.Exists(services.FiltersPath));
            Assert.Contains("# --vpp-edgelevel", await File.ReadAllTextAsync(services.FiltersPath));
            restored.FilterLines[1].IsEnabled = true;
            await LoadFiltersAndWaitAsync(restored, clock,
                () => restored.FilterLines.Any(line => !line.IsEnabled && line.Text.Contains("--vpp-edgelevel", StringComparison.Ordinal)));
            Assert.False(restored.FilterLines[1].IsEnabled);
            await File.WriteAllTextAsync(services.FiltersPath, "--vpp-nlmeans sigma=0.25\n--vpp-deband\n");
            await LoadFiltersAndWaitAsync(restored, clock,
                () => restored.FilterLines.Any(line => line.Text.Contains("--vpp-deband", StringComparison.Ordinal)));
            Assert.Contains(restored.FilterLines, line => line.Text.Contains("sigma=0.25", StringComparison.Ordinal));
            Assert.Contains(restored.FilterLines, line => line.Text.Contains("--vpp-deband", StringComparison.Ordinal));
        }
    }

    [AvaloniaFact]
    public async Task OrdinaryCommentsSurviveRepeatedLoadAndSaveUnchanged()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        await using var vm = Create(services, clock);
        await InitializeAsync(vm, clock);
        string[] lines = ["# 通常コメント", "   # インデントと末尾空白を保持  ", "#\tタブを含む説明", "--vpp-nlmeans sigma=0.1", "# --vpp-edgelevel strength=10", "", "# 最終コメント"];
        string original = string.Join(Environment.NewLine, lines);
        await File.WriteAllTextAsync(services.FiltersPath, original);
        for (int i = 0; i < 2; ++i)
        {
            await LoadFiltersAndWaitAsync(vm, clock, () => vm.FilterLines.Count == lines.Length);
            Assert.False(vm.FilterLines[4].IsEnabled);
            Assert.Equal(lines[0], vm.FilterLines[0].Text);
            Assert.Equal(lines[1], vm.FilterLines[1].Text);
            Assert.Equal(lines[2], vm.FilterLines[2].Text);
            vm.SaveFiltersCommand.Execute(null);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.Equal(original, await File.ReadAllTextAsync(services.FiltersPath));
        }
    }

    [AvaloniaFact]
    public async Task ChangedInputCancelsAnOlderProbeWithoutShowingItsError()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var probe = new InputProbe(clock, configureStartInfo: _ => started.TrySetResult());
        await using var vm = new MainWindowViewModel(services, clock, probe: probe)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "old input.ts", InputArgsText = "--fake-hang-after 0",
        };
        var initialization = vm.InitializeAsync(false);
        await started.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        vm.InputArgsText = "--avhw --fake-duration 00:00:10.00";
        vm.InputPath = "new input.ts";
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await initialization.WaitWithDiagnosticsAsync(SafetyTimeout);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task HelpSchemaLoadsWithHelpAndIsRebuiltAfterEncoderChanges()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        await using var vm = Create(services, clock);
        vm.InputPath = "";
        var line = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.1" };
        vm.FilterLines.Add(line);
        vm.SelectedFilterLine = line;
        Assert.Null(vm.HelpSchema);
        await InitializeAsync(vm, clock);
        var first = Assert.IsType<HelpSchema>(vm.HelpSchema);
        Assert.NotNull(first.Lookup("--vpp-nlmeans"));
        Assert.Contains("--vpp-nlmeans", vm.HelpText);
        vm.EncoderPath = "";
        Assert.Null(vm.HelpSchema);
        vm.EncoderPath = FakeEncoderPath();
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Null(vm.HelpSchema);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.NotSame(first, vm.HelpSchema);
        Assert.NotNull(vm.HelpSchema!.Lookup("--vpp-nlmeans"));
        Assert.Contains("--vpp-nlmeans", vm.HelpText);
    }

    [AvaloniaFact]
    public async Task HelpSchemaUsesActualHelpProvidedToFakeEncoder()
    {
        string? previous = Environment.GetEnvironmentVariable("FAKE_ENCODER_HELP_FILE");
        try
        {
            Environment.SetEnvironmentVariable("FAKE_ENCODER_HELP_FILE", Path.Combine(AppContext.BaseDirectory, "Data", "help", "qsvenc-h1.txt"));
            using var services = new TestServices();
            var clock = new FakeTimeProvider();
            await using var vm = Create(services, clock);
            vm.InputPath = "";
            await InitializeAsync(vm, clock);
            var schema = Assert.IsType<HelpSchema>(vm.HelpSchema);
            Assert.Equal(85, schema.Options.Count);
            Assert.Equal(671, schema.Options.Sum(option => option.Parameters.Count));
            var kfm = schema.Lookup("--vpp-kfm")!;
            Assert.Equal("deinterlace", kfm.Category);
            Assert.False(kfm.IsNameOnly);
            Assert.Contains("60", kfm.Parameters.Single(parameter => parameter.Name == "mode").Values);
        }
        finally { Environment.SetEnvironmentVariable("FAKE_ENCODER_HELP_FILE", previous); }
    }

    [AvaloniaFact]
    public async Task ConfirmedOperationsRunImmediatelyWhileTextEditingKeepsDebounce()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        int starts = 0;
        await using var vm = Create(services, clock, new OneShotBackend(_ => Interlocked.Increment(ref starts)));
        // 初回と入力交換は偽時計を進めずに比較まで終わる。
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(SafetyTimeout);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.NotNull(vm.PreviewImage);
        vm.OpenInput("second input.ts");
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Null(vm.ErrorText);
        var line = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.1" };
        vm.FilterLines.Add(line);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        int beforeText = starts;
        line.Text = "--vpp-nlmeans sigma=0.2";
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Equal(beforeText, starts);
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(beforeText, starts);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True(starts > beforeText);
        line.IsEnabled = false;
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        vm.FilterLines.Remove(line);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        vm.RefreshCommand.Execute(null);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task TypingInputPathWaitsBeforeStartingTheProbeAndUsesOnlyTheLatestText()
    {
        using var services = new TestServices();
        var clock = new FakeTimeProvider();
        var commands = new List<string[]>();
        var probe = new InputProbe(clock, configureStartInfo: info => commands.Add(info.ArgumentList.ToArray()));
        await using var vm = new MainWindowViewModel(services, clock, probe: probe)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "initial.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00"
        };
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(SafetyTimeout);
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        int before = commands.Count;
        vm.InputPath = "typing-1.ts";
        vm.InputPath = "typing-2.ts";
        Assert.Equal(before, commands.Count);
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(before, commands.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        Assert.True(commands.Count > before);
        Assert.DoesNotContain(commands.Skip(before), command => command.Contains("typing-1.ts"));
        Assert.Contains(commands.Skip(before), command => command.Contains("typing-2.ts"));
        Assert.Null(vm.ErrorText);
    }

    private static MainWindowViewModel Create(TestServices services, FakeTimeProvider clock, IPreviewBackend? backend = null) =>
        new(services, clock, preferred: backend)
        {
            EncoderPath = FakeEncoderPath(), InputPath = "fake input.ts",
            InputArgsText = "--avhw --fake-duration 00:00:10.00",
        };

    private static async Task InitializeAsync(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
    }

    private static async Task RunPreparedAsync(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        await vm.WaitForPreparationAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
    }

    private static async Task LoadFiltersAndWaitAsync(MainWindowViewModel vm, FakeTimeProvider clock, Func<bool> condition)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyCollectionChangedEventHandler loading = (_, _) =>
        {
            if (condition())
                // 読込後のSubmit登録まで同じUI処理が進んでから、偽時計を進める。
                Dispatcher.UIThread.Post(() => loaded.TrySetResult());
        };
        vm.FilterLines.CollectionChanged += loading;
        try
        {
            vm.LoadFiltersCommand.Execute(null);
            await loaded.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        }
        finally { vm.FilterLines.CollectionChanged -= loading; }
        await RunPreparedAsync(vm, clock);
    }

    private static string ImageHash(MainWindowViewModel vm)
    {
        var bitmap = Assert.IsType<WriteableBitmap>(vm.PreviewImage);
        using var frame = bitmap.Lock();
        byte[] row = new byte[bitmap.PixelSize.Width * 4];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int y = 0; y < bitmap.PixelSize.Height; ++y)
        {
            Marshal.Copy(IntPtr.Add(frame.Address, y * frame.RowBytes), row, 0, row.Length);
            hash.AppendData(row);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
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

    private sealed class TestServices : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-ui-{Guid.NewGuid():N}");
        public TestServices() => Directory.CreateDirectory(_directory);
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public string FiltersPath => Path.Combine(_directory, "filters.txt");
        public string ClipboardText { get; private set; } = "";
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(FakeEncoderPath());
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>("fake input.ts");
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(FiltersPath);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(FiltersPath);
        public Task CopyTextAsync(string text) { ClipboardText = text; return Task.CompletedTask; }
        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}

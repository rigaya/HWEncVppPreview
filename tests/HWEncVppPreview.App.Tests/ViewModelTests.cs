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
        var backend = TestProcesses.CreateServerBackend(configureStartInfo: info => commands.Add(info.ArgumentList.ToArray()));
        await using var vm = TestViewModels.Create(services, backend: backend);
        await vm.InitializeAsync();
        await vm.WaitForIdleAsync();
        Assert.Equal(expectedTime, vm.CurrentSeconds);
        Assert.Equal(10, vm.DurationSeconds);
        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.PreviewImage);
        Assert.NotEmpty(commands);
        Assert.Contains("--preview-server", commands[0]);
    }

    [AvaloniaFact]
    public async Task OpensWindowAndShowsARealFrameFromFakeEncoder()
    {
        using var services = new TestServices();
        await using var vm = Create(services);
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            await InitializeAsync(vm);
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
        await using var vm = Create(services, TestProcesses.CreateServerBackend());
        var line = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.1" };
        vm.FilterLines.Add(line);
        vm.SelectedFilterLine = line;
        await InitializeAsync(vm);
        string first = ImageHash(vm);
        Assert.Contains("--vpp-nlmeans", vm.HelpText);

        line.Text = "--vpp-nlmeans sigma=0.2";
        await RunPreparedAsync(vm);

        Assert.NotEqual(first, ImageHash(vm));
        line.IsEnabled = false;
        await RunPreparedAsync(vm);

        line.IsEnabled = true;
        await RunPreparedAsync(vm);

    }

    [AvaloniaFact]
    public async Task AdjacentFrameUsesCacheAndRefreshBypassesIt()
    {
        using var services = new TestServices();
        await using var vm = Create(services, TestProcesses.CreateServerBackend());
        vm.CurrentSeconds = 0.3;
        await InitializeAsync(vm);
        string first = ImageHash(vm);
        vm.StepFrameCommand.Execute(1);
        await vm.WaitForIdleAsync();

        Assert.Equal(10 / 30.0, vm.CurrentSeconds, 7);
        Assert.NotEqual(first, ImageHash(vm));
        vm.StepFrameCommand.Execute(-1);
        await vm.WaitForIdleAsync();

        Assert.Equal(0.3, vm.CurrentSeconds, 7);
        vm.RefreshCommand.Execute(null);
        await RunPreparedAsync(vm);

    }

    [AvaloniaFact]
    public async Task SeekDragRequestsFramesBeforeReleaseAndTimeFormatsAreAccepted()
    {
        using var services = new TestServices();
        await using var vm = Create(services, TestProcesses.CreateServerBackend());
        await InitializeAsync(vm);
        vm.BeginSeekDrag();
        vm.CurrentSeconds = 2;
        vm.CurrentSeconds = 3;
        // ドラッグを離す前に最新位置の要求を完了でき、編集用の待機時間は不要。
        await vm.WaitForIdleAsync();

        Assert.Equal(3, vm.CurrentSeconds);
        Assert.NotNull(vm.PreviewImage);
        vm.EndSeekDrag(3);
        await vm.WaitForIdleAsync();
        Assert.Equal(3, vm.CurrentSeconds);
        foreach (var item in new[] { ("0:04", 4.0), ("0:00:05.500", 5.5), ("6.25", 6.25) })
        {
            vm.CurrentTimeText = item.Item1;
            vm.CommitTimeTextCommand.Execute(null);
            await RunPreparedAsync(vm);
            Assert.Equal(item.Item2, vm.CurrentSeconds, 7);
        }
        vm.CurrentTimeText = "invalid";
        vm.CommitTimeTextCommand.Execute(null);
        await vm.WaitForIdleAsync();
        Assert.NotNull(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task EncoderErrorShowsDiagnosticsAndKeepsAnImage()
    {
        using var services = new TestServices();
        await using var vm = Create(services);
        var line = new FilterLineViewModel();
        vm.FilterLines.Add(line);
        await InitializeAsync(vm);
        Assert.NotNull(vm.PreviewImage);
        line.Text = "--fake-rc 19";
        await RunPreparedAsync(vm);
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
        await using (var vm = Create(services))
        {
            vm.FilterLines.Add(new() { Text = "--vpp-nlmeans sigma=0.1" });
            vm.FilterLines.Add(new() { Text = "--vpp-edgelevel strength=10", IsEnabled = false });
            await InitializeAsync(vm);
            vm.CopyCommandLineCommand.Execute(null);
            await vm.WaitForIdleAsync();
            Assert.Contains("--vpp-nlmeans", services.ClipboardText);
            Assert.DoesNotContain("--vpp-edgelevel", services.ClipboardText);
            await vm.SaveSettingsAsync(1200, 780);
        }
        await using (var restored = TestViewModels.Create(services, null, null, null, null, debounceOverride: TimeSpan.Zero))
        {
            await restored.InitializeAsync();

            await restored.WaitForIdleAsync();
            Assert.Equal(FakeEncoderPath(), restored.EncoderPath);
            Assert.Equal("fake input.ts", restored.InputPath);
            Assert.Equal(1200, restored.WindowWidth);
            Assert.Equal(780, restored.WindowHeight);
            Assert.Equal(2, restored.FilterLines.Count);
            Assert.False(restored.FilterLines[1].IsEnabled);
            restored.SaveFiltersCommand.Execute(null);
            await restored.WaitForIdleAsync();
            Assert.True(File.Exists(services.FiltersPath));
            Assert.Contains("# --vpp-edgelevel", await File.ReadAllTextAsync(services.FiltersPath));
            restored.FilterLines[1].IsEnabled = true;
            await LoadFiltersAndWaitAsync(restored,
                () => restored.FilterLines.Any(line => !line.IsEnabled && line.Text.Contains("--vpp-edgelevel", StringComparison.Ordinal)));
            Assert.False(restored.FilterLines[1].IsEnabled);
            await File.WriteAllTextAsync(services.FiltersPath, "--vpp-nlmeans sigma=0.25\n--vpp-deband\n");
            await LoadFiltersAndWaitAsync(restored,
                () => restored.FilterLines.Any(line => line.Text.Contains("--vpp-deband", StringComparison.Ordinal)));
            Assert.Contains(restored.FilterLines, line => line.Text.Contains("sigma=0.25", StringComparison.Ordinal));
            Assert.Contains(restored.FilterLines, line => line.Text.Contains("--vpp-deband", StringComparison.Ordinal));
        }
    }

    [AvaloniaFact]
    public async Task OrdinaryCommentsSurviveRepeatedLoadAndSaveUnchanged()
    {
        using var services = new TestServices();
        await using var vm = Create(services);
        await InitializeAsync(vm);
        string[] lines = ["# 通常コメント", "   # インデントと末尾空白を保持  ", "#\tタブを含む説明", "--vpp-nlmeans sigma=0.1", "# --vpp-edgelevel strength=10", "", "# 最終コメント"];
        string original = string.Join(Environment.NewLine, lines);
        await File.WriteAllTextAsync(services.FiltersPath, original);
        for (int i = 0; i < 2; ++i)
        {
            await LoadFiltersAndWaitAsync(vm, () => vm.FilterLines.Count == lines.Length);
            Assert.False(vm.FilterLines[4].IsEnabled);
            Assert.Equal(lines[0], vm.FilterLines[0].Text);
            Assert.Equal(lines[1], vm.FilterLines[1].Text);
            Assert.Equal(lines[2], vm.FilterLines[2].Text);
            vm.SaveFiltersCommand.Execute(null);
            await vm.WaitForIdleAsync();
            Assert.Equal(original, await File.ReadAllTextAsync(services.FiltersPath));
        }
    }

    [AvaloniaFact]
    public async Task ChangedInputCancelsAnOlderProbeWithoutShowingItsError()
    {
        using var services = new TestServices();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var probe = TestProcesses.CreateInputProbe(configureStartInfo: _ => started.TrySetResult());
        await using var vm = TestViewModels.Create(services, null, probe, null, null, debounceOverride: TimeSpan.Zero);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "old input.ts";
        vm.InputArgsText = "--fake-hang-after 0";
        var initialization = vm.InitializeAsync(false);
        await started.Task;
        vm.InputArgsText = "--avhw --fake-duration 00:00:10.00";
        vm.InputPath = "new input.ts";
        await vm.WaitForPreparationAsync();

        await initialization;
        await vm.WaitForIdleAsync();
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task HelpSchemaLoadsWithHelpAndIsRebuiltAfterEncoderChanges()
    {
        using var services = new TestServices();
        await using var vm = Create(services);
        vm.InputPath = "";
        var line = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.1" };
        vm.FilterLines.Add(line);
        vm.SelectedFilterLine = line;
        Assert.Null(vm.HelpSchema);
        await InitializeAsync(vm);
        var first = Assert.IsType<HelpSchema>(vm.HelpSchema);
        Assert.NotNull(first.Lookup("--vpp-nlmeans"));
        Assert.Contains("--vpp-nlmeans", vm.HelpText);
        vm.EncoderPath = "";
        Assert.Null(vm.HelpSchema);
        vm.EncoderPath = FakeEncoderPath();
        await vm.WaitForIdleAsync();
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
            await using var vm = Create(services);
            vm.InputPath = "";
            await InitializeAsync(vm);
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
    public async Task ConfirmedAndEditedOperationsComplete()
    {
        using var services = new TestServices();
        await using var vm = Create(services, TestProcesses.CreateServerBackend());
        await vm.InitializeAsync(false);
        await vm.WaitForIdleAsync();
        Assert.NotNull(vm.PreviewImage);
        vm.OpenInput("second input.ts");
        await vm.WaitForIdleAsync();
        Assert.Null(vm.ErrorText);
        var line = new FilterLineViewModel { Text = "--vpp-nlmeans sigma=0.1" };
        vm.FilterLines.Add(line);
        await vm.WaitForIdleAsync();
        line.Text = "--vpp-nlmeans sigma=0.2";
        await vm.WaitForPreparationAsync();



        await vm.WaitForIdleAsync();

        line.IsEnabled = false;
        await vm.WaitForIdleAsync();
        vm.FilterLines.Remove(line);
        await vm.WaitForIdleAsync();
        vm.RefreshCommand.Execute(null);
        await vm.WaitForIdleAsync();
        Assert.Null(vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task TypingInputPathUsesTheLatestText()
    {
        using var services = new TestServices();
        await using var vm = Create(services);
        await InitializeAsync(vm);
        vm.InputPath = "typing-1.ts";
        vm.InputPath = "typing-2.ts";
        await vm.WaitForPreparationAsync();
        await vm.WaitForIdleAsync();
        Assert.Equal("typing-2.ts", vm.InputPath);
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
    }

    private static MainWindowViewModel Create(TestServices services, IPreviewBackend? backend = null)
    {
        var vm = TestViewModels.Create(services, backend, debounceOverride: TimeSpan.Zero);
        vm.EncoderPath = FakeEncoderPath();
        vm.InputPath = "fake input.ts";
        vm.InputArgsText = "--avhw --fake-duration 00:00:10.00";
        return vm;
    }

    private static async Task InitializeAsync(MainWindowViewModel vm)
    {
        await vm.InitializeAsync(false);

        await vm.WaitForIdleAsync();
    }

    private static async Task RunPreparedAsync(MainWindowViewModel vm)
    {
        await vm.WaitForPreparationAsync();

        await vm.WaitForIdleAsync();
    }

    private static async Task LoadFiltersAndWaitAsync(MainWindowViewModel vm, Func<bool> condition)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyCollectionChangedEventHandler loading = (_, _) =>
        {
            if (condition())
                // 読込後の要求登録まで同じUI処理を進める。
                Dispatcher.UIThread.Post(() => loaded.TrySetResult());
        };
        vm.FilterLines.CollectionChanged += loading;
        try
        {
            vm.LoadFiltersCommand.Execute(null);
            await loaded.Task;
        }
        finally { vm.FilterLines.CollectionChanged -= loading; }
        await RunPreparedAsync(vm);
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

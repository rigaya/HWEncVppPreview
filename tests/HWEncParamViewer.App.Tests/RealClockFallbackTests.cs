using System.ComponentModel;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.Tests;

/// <summary>実入力のwrap前後を公開VM経由で移動し、時計の保護後も操作できることを調べる。</summary>
public sealed class RealClockFallbackTests
{
    [AvaloniaFact]
    public async Task RealWrapInputsRemainNavigable()
    {
        string? encoder = Environment.GetEnvironmentVariable("HWENC_VIEWER_CLOCK_ENCODER");
        string? planPath = Environment.GetEnvironmentVariable("HWENC_VIEWER_CLOCK_PLAN");
        if (string.IsNullOrWhiteSpace(encoder) || string.IsNullOrWhiteSpace(planPath))
            Assert.Skip("実時計の検証はエンコーダと計画を明示したときだけ実行する");
        Assert.True(OperatingSystem.IsWindows(), "実時計の検証はWindowsのVSTestで実行する");
        Assert.True(PerfTrace.Enabled, "時計の検証にはトレース出力先が必要");
        Assert.Equal("QSVEncC64.exe", Path.GetFileName(encoder), ignoreCase: true);
        Assert.Equal("airi_p0_win", Path.GetFileName(Path.GetDirectoryName(encoder)), ignoreCase: true);
        var plan = JsonSerializer.Deserialize<ClockPlan>(await File.ReadAllTextAsync(planPath!),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(plan);
        string? selected = Environment.GetEnvironmentVariable("HWENC_VIEWER_CLOCK_CASE");
        var cases = plan.Cases.Where(item => string.IsNullOrWhiteSpace(selected) || item.Id == selected).ToArray();
        Assert.NotEmpty(cases);
        foreach (var item in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Input));
            Assert.NotEmpty(item.Positions);
            Assert.All(item.Positions, seconds => Assert.True(double.IsFinite(seconds) && seconds >= 0));
            Assert.InRange(item.Steps, 1, 8);
            string options = item.Filter.ToUpperInvariant() switch
            {
                "F0" or "NONE" => "", "AFS" => "--vpp-afs", "KFM" => "--vpp-kfm",
                _ => throw new ArgumentException("未知の実時計フィルタ条件"),
            };
            await using var vm = new MainWindowViewModel(new ClockServices())
            { EncoderPath = encoder!, InputPath = item.Input, InputArgsText = "--avhw" };
            if (options.Length > 0) vm.FilterLines.Add(new FilterLineViewModel { Text = options });
            PerfTrace.Write("clock.case.begin", new { item.Id, item.Input, item.Filter, item.EventSeconds, item.Expectation, item.Positions, item.Steps });
            PerfTrace.Flush();
            try
            {
                await vm.InitializeAsync(false);
                await vm.WaitForIdleAsync();
                Check(vm);
                foreach (double target in item.Positions)
                {
                    await PerformAsync("seek", Math.Sign(target - vm.CurrentSeconds), () => vm.CurrentSeconds = target);
                    for (int i = 0; i < item.Steps; ++i)
                        await PerformAsync("right", 1, () => vm.StepFrameCommand.Execute(1));
                    for (int i = 0; i < item.Steps; ++i)
                        await PerformAsync("left", -1, () => vm.StepFrameCommand.Execute(-1));
                }
                PerfTrace.Write("clock.case.end", new { item.Id, vm.CurrentSeconds, vm.StatusText, vm.ErrorText, vm.BackendModeText, image = vm.PreviewImage != null });
            }
            catch (Exception exception)
            {
                PerfTrace.Write("clock.case.error", new { item.Id, type = exception.GetType().Name, exception.Message, vm.CurrentSeconds, vm.StatusText, vm.ErrorText });
                throw;
            }
            finally
            {
                PerfTrace.Write("clock.teardown.begin", new { item.Id, utc = DateTimeOffset.UtcNow });
                PerfTrace.Flush();
            }

            async Task PerformAsync(string actionName, int direction, Action action)
            {
                var history = new List<double> { vm.CurrentSeconds };
                PropertyChangedEventHandler changed = (_, args) =>
                { if (args.PropertyName == nameof(MainWindowViewModel.CurrentSeconds)) history.Add(vm.CurrentSeconds); };
                vm.PropertyChanged += changed;
                PerfTrace.Write("clock.action.begin", new { item.Id, actionName, direction, vm.CurrentSeconds });
                PerfTrace.Flush();
                try
                {
                    action();
                    await vm.WaitForPreparationAsync();
                    // 時間を性能の合否にせず、製品の出力進行監視とbackendの回収期限を使う。
                    await vm.WaitForIdleAsync();
                }
                finally { vm.PropertyChanged -= changed; }
                Check(vm);
                Assert.All(history, seconds => Assert.True(double.IsFinite(seconds) && seconds >= -1e-6));
                if (direction != 0)
                    for (int i = 1; i < history.Count; ++i)
                        Assert.True((history[i] - history[i - 1]) * direction >= -1e-6,
                            $"{item.Id}/{actionName}:目標時刻が操作と逆方向へ動いた ({history[i - 1]:R}→{history[i]:R})");
                PerfTrace.Write("clock.action.end", new { item.Id, actionName, direction, history, vm.CurrentSeconds, vm.IsBusy, vm.IsBeforeBusy, vm.ErrorText, vm.StatusText });
                PerfTrace.Flush();
            }
        }
    }

    private static void Check(MainWindowViewModel vm)
    {
        Assert.NotNull(vm.PreviewImage);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
        Assert.True(double.IsFinite(vm.CurrentSeconds) && vm.CurrentSeconds >= -1e-6);
    }

    private sealed class ClockPlan
    {
        public ClockPlan() { }
        public ClockCase[] Cases { get; set; } = [];
    }
    private sealed class ClockCase
    {
        public ClockCase() { }
        public string Id { get; set; } = "";
        public string Input { get; set; } = "";
        public string Filter { get; set; } = "F0";
        public double? EventSeconds { get; set; }
        public string Expectation { get; set; } = "observe";
        public double[] Positions { get; set; } = [];
        public int Steps { get; set; } = 4;
    }
    private sealed class ClockServices : IViewModelServices
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "hwenc-clock-unused.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
}

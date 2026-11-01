using System.Diagnostics;
using Avalonia.Headless.XUnit;
using HWEncParamViewer.App.Services;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.App.Views;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.Tests;

/// <summary>実エンコーダを指定したときだけ、移動操作と描画の時系列を採取する。</summary>
public sealed class NavigationBenchmarkTests
{
    [AvaloniaFact]
    public async Task RealEncoderNavigationTimeline()
    {
        string? encoder = Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_ENCODER");
        string? input = Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_INPUT");
        if (string.IsNullOrWhiteSpace(encoder) || string.IsNullOrWhiteSpace(input))
            Assert.Skip("実エンコーダ計測は環境変数を指定したときだけ実行する");
        Assert.True(OperatingSystem.IsWindows(), "M1はWindowsのdotnet.exeで実行する");
        Assert.True(PerfTrace.Enabled, "時系列の出力先が必要");
        string scenario = Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_SCENARIO") ?? "S1";
        string filter = Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_FILTER") ?? "F0";
        string options = filter switch { "F0" => "", "F1" => "--vpp-nlmeans", "F2" => "--vpp-kfm", _ => throw new ArgumentException("未知のフィルタ条件") };
        int count = scenario switch { "S1" or "S2" => 150, "S3" => 20, _ => throw new ArgumentException("未知の移動条件") };
        int direction = scenario == "S2" ? -1 : 1;
        double frequency = scenario == "S3" ? 60 : 30;
        await using var vm = new MainWindowViewModel(new BenchmarkServices())
        { EncoderPath = encoder!, InputPath = input!, InputArgsText = "--avhw", CurrentSeconds = 60 };
        if (options.Length > 0) vm.FilterLines.Add(new FilterLineViewModel { Text = options });
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        var process = Process.GetCurrentProcess();
        TimeSpan pauseStart = GC.GetTotalPauseDuration();
        int[] collectionStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        try
        {
            PerfTrace.Write("bench.prepare", new { scenario, filter, encoder, input,
                before = Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_DISABLE_BEFORE") != "1",
                postMargin = Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_POST_MARGIN") ?? "0.5" });
            await vm.InitializeAsync(false).WaitAsync(TimeSpan.FromSeconds(90));
            await vm.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Null(vm.ErrorText);
            Assert.NotNull(vm.PreviewImage);
            // 通常のキー反復と同様、処理完了を待たず一定周期で次の入力を送る。
            // S3もViewのホイール処理と同じStepFrameCommandへ接続する。
            TimeSpan scenarioPauseStart = GC.GetTotalPauseDuration();
            int[] scenarioCollections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            var watch = Stopwatch.StartNew();
            PerfTrace.Write("bench.scenario.start", new { scenario, filter, count, frequency, vm.CurrentSeconds });
            for (int i = 0; i < count; ++i)
            {
                double due = i / frequency;
                double remaining = due - watch.Elapsed.TotalSeconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining));
                PerfTrace.Write("bench.input", new { index = i, direction, dueSeconds = due, actualSeconds = watch.Elapsed.TotalSeconds });
                vm.StepFrameCommand.Execute(direction);
            }
            double tail = count / frequency - watch.Elapsed.TotalSeconds;
            if (tail > 0) await Task.Delay(TimeSpan.FromSeconds(tail));
            PerfTrace.Write("bench.scenario.end", new { vm.CurrentSeconds, vm.StatusText, vm.ErrorText,
                pauseMs = (GC.GetTotalPauseDuration() - scenarioPauseStart).TotalMilliseconds,
                collectionDelta = new[] { GC.CollectionCount(0) - scenarioCollections[0], GC.CollectionCount(1) - scenarioCollections[1], GC.CollectionCount(2) - scenarioCollections[2] } });
            await vm.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));
            PerfTrace.Write("bench.settled", new { vm.CurrentSeconds, vm.StatusText, vm.ErrorText });
            Assert.Null(vm.ErrorText);
            // 実測時だけ、需要が満たされた後の常駐プロセスを観測できる期間を設ける。
            // 連続取得の意図的なpauseは正常で、ここでは物理ENDを待たない。
            string? holdText = Environment.GetEnvironmentVariable("HWENC_VIEWER_BENCH_IDLE_HOLD_MS");
            if (!string.IsNullOrWhiteSpace(holdText))
            {
                Assert.True(int.TryParse(holdText, out int holdMs) && holdMs is >= 0 and <= 30000,
                    "idle計測の保持時間は0〜30000msで指定する");
                if (holdMs > 0)
                {
                    PerfTrace.Write("bench.idle.begin", new { utc = DateTimeOffset.UtcNow, holdMs, vm.CurrentSeconds, vm.StatusText, vm.IsBeforeBusy });
                    PerfTrace.Flush();
                    await Task.Delay(holdMs);
                    PerfTrace.Write("bench.idle.end", new { utc = DateTimeOffset.UtcNow, vm.CurrentSeconds, vm.StatusText, vm.IsBeforeBusy });
                }
            }
        }
        catch (Exception exception)
        {
            PerfTrace.Write("bench.error", new { type = exception.GetType().FullName, exception.Message, vm.StatusText, vm.ErrorText });
            throw;
        }
        finally
        {
            PerfTrace.Write("bench.teardown.begin", new { utc = DateTimeOffset.UtcNow });
            window.Close();
            process.Refresh();
            PerfTrace.Write("bench.metrics", new
            {
                totalPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                pauseMs = (GC.GetTotalPauseDuration() - pauseStart).TotalMilliseconds,
                collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
                collectionDelta = new[] { GC.CollectionCount(0) - collectionStart[0], GC.CollectionCount(1) - collectionStart[1], GC.CollectionCount(2) - collectionStart[2] },
                peakWorkingSetBytes = process.PeakWorkingSet64,
                workingSetBytes = process.WorkingSet64,
            });
            PerfTrace.Flush();
        }
    }

    private sealed class BenchmarkServices : IViewModelServices
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "hwenc-nav-benchmark-unused.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }
}

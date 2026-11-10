using System.Reflection;
using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class BgraLeaseViewModelTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static Y4mFrame Frame() => new(new Y4mHeader { Width = 2, Height = 2 }, 1, 1, new byte[6]);
    private static BgraBufferPool Pool(MainWindowViewModel vm) => (BgraBufferPool)typeof(MainWindowViewModel)
        .GetField("_bgraPool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
    [AvaloniaFact]
    public async Task UIコピー完了まで返却せず入力変更で旧貸出を退役する()
    {
        using var services = new Services();
        await using var vm = new MainWindowViewModel(services);
        var entered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = vm.WithConvertedBgraAsync(Frame(), new(), CancellationToken.None, async data =>
        { entered.TrySetResult(data); await copied.Task; });
        byte[] output = await entered.Task.WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(YuvToBgra.Convert(Frame()), output);
        Assert.Equal(1, Pool(vm).Capture().Leased); Assert.Equal(0, Pool(vm).Capture().Idle);
        vm.InputPath = "different-input.ts";
        Assert.Equal(1, Pool(vm).Capture().Leased);
        copied.TrySetResult(); await work.WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(0, Pool(vm).Capture().Buffers);
    }
    [AvaloniaTheory]
    [InlineData("cancel_before")]
    [InlineData("invalid_frame")]
    [InlineData("ui_exception")]
    [InlineData("ui_cancel")]
    [InlineData("stale_noop")]
    public async Task 取消と例外と世代棄却でも貸出が戻る(string reason)
    {
        using var services = new Services();
        await using var vm = new MainWindowViewModel(services);
        using var cancellation = new CancellationTokenSource();
        if (reason == "cancel_before") cancellation.Cancel();
        var frame = reason == "invalid_frame" ? new Y4mFrame(Frame().Header, 0, 1, new byte[1]) : Frame();
        Task work = vm.WithConvertedBgraAsync(frame, new(), cancellation.Token, _ => reason switch
        {
            "ui_exception" => Task.FromException(new InvalidOperationException("UIコピー試験")),
            "ui_cancel" => Task.FromCanceled(new CancellationToken(true)),
            _ => Task.CompletedTask,
        });
        if (reason == "stale_noop") await work.WaitWithDiagnosticsAsync(Safety);
        else await Assert.ThrowsAnyAsync<Exception>(() => work.WaitWithDiagnosticsAsync(Safety));
        Assert.Equal(0, Pool(vm).Capture().Leased);
        Assert.InRange(Pool(vm).Capture().Buffers, 0, 1);
    }
    [AvaloniaFact]
    public async Task 注入converterの配列はpoolへ取り込まない()
    {
        using var services = new Services();
        byte[] external = new byte[16];
        await using var vm = new MainWindowViewModel(services, null, null, null, null, null,
            (_, _) => Task.FromResult(external));
        await vm.WithConvertedBgraAsync(Frame(), new(), CancellationToken.None, data =>
        { Assert.Same(external, data); return Task.CompletedTask; }).WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(0, Pool(vm).Capture().Buffers);
        Assert.Equal(0, Pool(vm).Capture().Leased);
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 実変換loopのUI完了と旧世代棄却で貸出を解放する(bool supersede)
    {
        using var services = new Services();
        await using var vm = new MainWindowViewModel(services);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(MainWindowViewModel).GetField("_generation", flags)!.SetValue(vm, 1L);
        var type = typeof(MainWindowViewModel).GetNestedType("ImageWork", BindingFlags.NonPublic)!;
        var constructor = type.GetConstructors(flags).Single(item => item.GetParameters().Length > 3);
        object?[] arguments = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "Frame" => (object)Frame(), "Generation" => 1L, "EditEpoch" => 0L, _ => parameter.DefaultValue,
        }).ToArray();
        object image = constructor.Invoke(arguments);
        typeof(MainWindowViewModel).GetMethod("EnqueueImage", flags)!.Invoke(vm, new[] { image });
        // UIはまだこの同期呼出し内にいる。PostAsyncの適用前には返却してはいけない。
        Assert.True(Task.Run(() => SpinWait.SpinUntil(() => Pool(vm).Capture().Leased == 1, Safety)).GetAwaiter().GetResult());
        Assert.Equal(1, Pool(vm).Capture().Leased);
        if (supersede) typeof(MainWindowViewModel).GetMethod("BeginIntent", flags)!.Invoke(vm, new object[] { false });
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(0, Pool(vm).Capture().Leased);
        Assert.Equal(1, Pool(vm).Capture().Idle);
        if (supersede) Assert.Null(vm.PreviewImage); else Assert.NotNull(vm.PreviewImage);
    }

    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "bgra-lease-" + Guid.NewGuid().ToString("N"));
        public Services() => Directory.CreateDirectory(_directory);
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose() => Directory.Delete(_directory, true);
    }
}

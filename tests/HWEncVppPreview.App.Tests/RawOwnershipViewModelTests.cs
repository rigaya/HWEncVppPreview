using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class RawOwnershipViewModelTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2 };

    [AvaloniaFact]
    public async Task cache消去後も両表示のHDR再変換を保持しDisposeで回収する()
    {
        using var services = new Services();
        await using var vm = CreateVm(services);
        await using var ownership = new OwnedVmCheck(vm);
        var scope = Scope(vm);
        Context(vm);
        var header = new Y4mHeader
        {
            Width = 2, Height = 2,
            ExtTags = new Dictionary<string, string> { ["XTRANSFER"] = "smpte2084", ["XCOLORMATRIX"] = "bt2020nc", ["XCOLORPRIM"] = "bt2020" },
        };
        var frame = scope.Create(header, 1, 1, new byte[] { 180, 180, 180, 180, 128, 128 }, scope.NewRunId(), "TestReader");
        byte[] pixels = new byte[16];
        YuvToBgra.ConvertInto(frame, pixels);
        using (var after = (IDisposable)Image(frame.Frame)) Invoke(vm, "ApplyImage", after, pixels);
        using (var before = (IDisposable)Image(frame.Frame, before: true)) Invoke(vm, "ApplyImage", before, pixels);
        var oldAfter = vm.PreviewImage;
        var oldBefore = vm.BeforeImage;
        Assert.NotNull(oldAfter);
        Assert.NotNull(oldBefore);
        frame.Dispose();
        ((FrameCache)Field(vm, "_cache")!).Clear();
        Assert.Equal(1, scope.Capture().OwnerKinds["VmDisplayed"]);
        Assert.Equal(1, scope.Capture().OwnerKinds["VmBeforeDisplayed"]);
        vm.HdrReferenceWhiteNits = 400;
        await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.NotSame(oldAfter, vm.PreviewImage);
        Assert.NotSame(oldBefore, vm.BeforeImage);
        byte[] expected = YuvToBgra.Convert(new Y4mFrame(header, 1, 1,
            new byte[] { 180, 180, 180, 180, 128, 128 }), new() { ReferenceWhiteNits = 400 });
        Assert.Equal(expected, Pixels(vm.PreviewImage!));
        Assert.Equal(expected, Pixels(vm.BeforeImage!));
        Assert.Equal(1, scope.Capture().LiveOwners);
        vm.BeforeImage = null;
        Assert.False(scope.Capture().OwnerKinds.ContainsKey("VmBeforeDisplayed"));
        vm.PreviewImage = null;
        Assert.True(scope.Capture().IsEmpty);
        await vm.DisposeAsync();
        Assert.True(scope.Capture().IsEmpty);
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        // 回帰用の2x2画像だけを読む。実計測のフレームごとの診断コピーは増やさない。
        byte[] pixels = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), handle.AddrOfPinnedObject(), pixels.Length, bitmap.PixelSize.Width * 4);
        }
        finally
        {
            handle.Free();
        }
        return pixels;
    }

    private static MainWindowViewModel CreateVm(Services services)
        => new(services, null, null, null, null, null, null, beforeEnabled: false);
    private static RawOwnershipScope Scope(MainWindowViewModel vm) => (RawOwnershipScope)Field(vm, "_rawOwnershipScope")!;
    private static object Context(MainWindowViewModel vm, int neighborDirection = 0, PreviewRequest? request = null)
    {
        typeof(MainWindowViewModel).GetField("_generation", Flags)!.SetValue(vm, 1L);
        var type = typeof(MainWindowViewModel).GetNestedType("RequestContext", BindingFlags.NonPublic)!;
        var ctor = type.GetConstructors(Flags).Single();
        object context = ctor.Invoke(ctor.GetParameters().Select(parameter => parameter.Name switch
        {
            "generation" => (object)1L,
            "request" => request ?? new PreviewRequest { CorrelationId = 1, TargetSeconds = 1 },
            "started" => 0L,
            "neighborDirection" => neighborDirection,
            _ => parameter.DefaultValue,
        }).ToArray());
        Invoke(vm, "RegisterContext", context);
        return context;
    }
    private static object Image(Y4mFrame frame, bool before = false)
    {
        var ctor = typeof(MainWindowViewModel).GetNestedType("ImageWork", BindingFlags.NonPublic)!.GetConstructors(Flags).Single();
        return ctor.Invoke(ctor.GetParameters().Select(parameter => parameter.Name switch
        {
            "Frame" => (object)frame, "Generation" => 1L, "EditEpoch" => 0L, "Before" => before, _ => parameter.DefaultValue,
        }).ToArray());
    }
    private static object? Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private sealed class UnusedBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("この試験では実プロセスを起動しない。");
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Services : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "raw-vm-" + Guid.NewGuid().ToString("N"));
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

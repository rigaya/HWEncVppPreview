using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using HWEncVppPreview.App.Controls;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.App.Views;

namespace HWEncVppPreview.App.Tests;

/// <summary>View のマウス・ドロップ操作 (設計書 19.3、19.4)。ViewModel は初期化せず、プロパティの変化だけを見る。</summary>
public sealed class ViewInputTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WheelStepsFramesAndCtrlWheelZooms(bool useSar)
    {
        using var services = new TestServices();
        await using var vm = new MainWindowViewModel(services);
        vm.UseSar = useSar;
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            var viewer = window.FindControl<ZoomPanViewer>("MainViewer")!;
            var center = CenterOf(viewer, window);

            // 下 (手前) へ1目盛りで次のフレーム。拡大率は変わらない
            window.MouseWheel(center, new Vector(0, -1));
            double afterNext = vm.CurrentSeconds;
            Assert.True(afterNext > 0);
            Assert.True(viewer.Zoom.IsFit);

            // 上へ1目盛りで前のフレームに戻る
            window.MouseWheel(center, new Vector(0, 1));
            Assert.Equal(0, vm.CurrentSeconds, 9);

            // タッチパッドの細かい量は積算して1目盛り分で1フレーム
            window.MouseWheel(center, new Vector(0, -0.5));
            Assert.Equal(0, vm.CurrentSeconds, 9);
            window.MouseWheel(center, new Vector(0, -0.5));
            Assert.Equal(afterNext, vm.CurrentSeconds, 9);

            // Ctrl+ホイールは拡大縮小で、フレームは動かない
            window.MouseWheel(center, new Vector(0, 1), RawInputModifiers.Control);
            Assert.False(viewer.Zoom.IsFit);
            Assert.Equal(afterNext, vm.CurrentSeconds, 9);
            Assert.Equal(useSar, vm.UseSar);
            window.FindControl<Button>("ZoomFitButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(viewer.Zoom.IsFit);
            Assert.Equal(useSar, vm.UseSar);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DroppedFilesGoToTheFieldUnderThePointer()
    {
        using var services = new TestServices();
        await using var vm = new MainWindowViewModel(services);
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            string encoder = Path.Combine(services.Directory, "QSVEncC64.exe");
            string input = Path.Combine(services.Directory, "a.ts");
            string other = Path.Combine(services.Directory, "b.m2ts");
            string otherEncoder = Path.Combine(services.Directory, "NVEncC64.EXE");

            foreach (string path in new[] { encoder, input, other, otherEncoder }) File.WriteAllText(path, "");
            await DropAsync(window, window.FindControl<TextBox>("EncoderBox")!, encoder);
            Assert.Equal(encoder, vm.EncoderPath);

            await DropAsync(window, window.FindControl<TextBox>("InputBox")!, input);
            Assert.Equal(input, vm.InputPath);

            // 表示領域は拡張子で振り分ける
            var preview = window.FindControl<Border>("PreviewArea")!;
            await DropAsync(window, preview, other);
            Assert.Equal(other, vm.InputPath);
            await DropAsync(window, preview, otherEncoder);
            Assert.Equal(otherEncoder, vm.EncoderPath);
            Assert.Equal(other, vm.InputPath);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FoldersAreNotAccepted()
    {
        using var services = new TestServices();
        await using var vm = new MainWindowViewModel(services);
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            var data = new DataTransfer();
            var folder = await window.StorageProvider.TryGetFolderFromPathAsync(services.Directory) ?? throw new InvalidOperationException();
            data.Add(DataTransferItem.CreateFile(folder));
            var point = CenterOf(window.FindControl<TextBox>("InputBox")!, window);
            window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
            window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy);
            Assert.Equal("", vm.InputPath);
        }
        finally { window.Close(); }
    }

    private static async Task DropAsync(Window window, Control target, string path)
    {
        // IStorageItem は利用側で実装できないので、実在するファイルを StorageProvider から得る
        var file = await window.StorageProvider.TryGetFileFromPathAsync(path) ?? throw new InvalidOperationException(path);
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(file));
        var point = CenterOf(target, window);
        window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
        window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy);
        window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy);
    }

    private static Point CenterOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException("コントロールの位置を取得できません");

    private sealed class TestServices : IViewModelServices, IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"viewer-input-{Guid.NewGuid():N}");
        public TestServices() => System.IO.Directory.CreateDirectory(Directory);
        public string SettingsPath => Path.Combine(Directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.App.Views;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

/// <summary>フィルタ行の補完と数値の上下ドラッグ (設計書 21.7)。help は H1 の書式の実 help を偽エンコーダから出す。</summary>
public sealed class CompletionViewTests
{
    private static readonly TimeSpan SafetyTimeout = TestTimeouts.Hang;

    [AvaloniaFact]
    public async Task TypingAnOptionThenAParameterAndAValueCompletesTheLine()
    {
        await using var session = await Session.StartAsync();
        var box = await session.AddLineAsync();
        // 空の行にフォーカスが来たらオプションの候補が開く
        Assert.True(session.Popup.IsOpen);

        session.Window.KeyTextInput("--vpp-kf");
        Assert.True(session.Popup.IsOpen);
        Assert.Equal("--vpp-kfm", session.SelectedText);

        session.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal("--vpp-kfm ", box.Text);
        // 続けてパラメータ名の候補が開く
        Assert.True(session.Popup.IsOpen);
        Assert.Contains("mode", session.EntryTexts);

        session.Window.KeyTextInput("mo");
        session.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.Equal("--vpp-kfm mode=", box.Text);
        // 選択式なので値の候補が開く
        Assert.Contains("24", session.EntryTexts);

        session.Window.KeyTextInput("2");
        session.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal("--vpp-kfm mode=24", box.Text);
        Assert.False(session.Popup.IsOpen);
        Assert.Equal("--vpp-kfm mode=24", session.ViewModel.FilterLines[0].Text);
    }

    [AvaloniaFact]
    public async Task ANumericParameterGetsItsDefaultAndEscapeCloses()
    {
        await using var session = await Session.StartAsync();
        var box = await session.AddLineAsync();
        session.Window.KeyTextInput("--vpp-kfm ");

        session.Window.KeyTextInput("thsw");

        session.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        Assert.Equal("--vpp-kfm thswitch=0.5", box.Text);
        Assert.Equal(box.Text!.Length, box.CaretIndex);

        session.Window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);

        session.Window.KeyTextInput(",");

        Assert.True(session.Popup.IsOpen);
        Assert.DoesNotContain("thswitch", session.EntryTexts);
        session.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.False(session.Popup.IsOpen);
    }

    [AvaloniaFact]
    public async Task CategoryOrderShowsHeadingsAndKeepsSelectionOnItems()
    {
        await using var session = await Session.StartAsync();
        await session.AddLineAsync();
        session.Window.FindControl<Button>("OrderCategoryButton")!.Command?.Execute(null);
        session.Window.FindControl<Button>("OrderCategoryButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        var entries = session.Entries;
        Assert.True(entries[0].IsHeader);
        Assert.Equal("インターレース解除", entries[0].Text);
        Assert.Contains(entries, entry => entry.IsHeader && entry.Text == "ノイズ除去");
        Assert.False(session.SelectedEntry!.IsHeader);
        // 見出しは ↑↓ で飛ばす
        session.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);

        Assert.False(session.SelectedEntry!.IsHeader);
    }

    [AvaloniaFact]
    public async Task DraggingANumberUpAndDownChangesItWithinItsRange()
    {
        await using var session = await Session.StartAsync();
        var box = await session.AddLineAsync();
        session.SetText(box, "--vpp-kfm thswitch=0.5");


        var start = session.PointAtChar(box, "--vpp-kfm thswitch=0.".Length);
        session.Window.MouseDown(start, MouseButton.Left);
        session.Window.MouseMove(start - new Vector(0, 16));

        Assert.Equal("--vpp-kfm thswitch=0.7", box.Text);
        // 押した時点から計算し直すので、戻せば元の値
        session.Window.MouseMove(start);

        Assert.Equal("--vpp-kfm thswitch=0.5", box.Text);
        // 範囲 (0.0..1.0) の端で止まる
        session.Window.MouseMove(start - new Vector(0, 8 * 20));

        Assert.Equal("--vpp-kfm thswitch=1.0", box.Text);
        session.Window.MouseUp(start - new Vector(0, 8 * 20), MouseButton.Left);

        Assert.Equal("--vpp-kfm thswitch=1.0", session.ViewModel.FilterLines[0].Text);
    }

    [AvaloniaFact]
    public async Task ClickingAChoiceValueOpensItsList()
    {
        await using var session = await Session.StartAsync();
        var box = await session.AddLineAsync();
        session.SetText(box, "--vpp-kfm mode=vfr,thswitch=0.5");

        var point = session.PointAtChar(box, "--vpp-kfm mode=v".Length);
        session.Window.MouseDown(point, MouseButton.Left);
        session.Window.MouseUp(point, MouseButton.Left);

        Assert.True(session.Popup.IsOpen);
        Assert.Equal(new[] { "vfr", "60", "24" }.OrderBy(v => v), session.EntryTexts.OrderBy(v => v));

        // 一覧から選ぶと、値全体が置き換わる
        session.SelectEntry("60");
        session.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        Assert.Equal("--vpp-kfm mode=60,thswitch=0.5", box.Text);
    }

    internal static async Task WaitForLayoutAsync(Control root, Func<bool> condition)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args) { if (condition()) done.TrySetResult(); }
        root.LayoutUpdated += Changed;
        try
        {
            if (condition()) done.TrySetResult();
            await done.Task.WaitWithDiagnosticsAsync(SafetyTimeout);
        }
        finally { root.LayoutUpdated -= Changed; }
    }

    /// <summary>偽エンコーダの help を読んだ画面。V8 のフォームのテスト (FormViewTests) でも使う。</summary>
    internal sealed class Session : IAsyncDisposable
    {
        private readonly string? _previousHelp;
        private readonly TestServices _services;
        public MainWindowViewModel ViewModel { get; }
        public MainWindow Window { get; }
        public Popup Popup => Window.FindControl<Popup>("CompletionPopup")!;
        private ListBox List => Window.FindControl<ListBox>("CompletionList")!;
        public IReadOnlyList<CompletionEntry> Entries => (IReadOnlyList<CompletionEntry>)List.ItemsSource!;
        public IEnumerable<string> EntryTexts => Entries.Where(entry => !entry.IsHeader).Select(entry => entry.Text);
        public CompletionEntry? SelectedEntry => List.SelectedItem as CompletionEntry;
        public string? SelectedText => SelectedEntry?.Text;

        private Session(string? previousHelp, TestServices services, MainWindowViewModel vm, MainWindow window)
        {
            _previousHelp = previousHelp;
            _services = services;
            ViewModel = vm;
            Window = window;
        }

        public static async Task<Session> StartAsync(bool textListMode = true)
        {
            string? previous = Environment.GetEnvironmentVariable("FAKE_ENCODER_HELP_FILE");
            Environment.SetEnvironmentVariable("FAKE_ENCODER_HELP_FILE", Path.Combine(AppContext.BaseDirectory, "Data", "help", "qsvenc-h1.txt"));
            var services = new TestServices();
            // 入力ファイルなしでプレビューは要求せず、ヘルプの読み込みだけを行う。
            var vm = new MainWindowViewModel(services, TimeProvider.System) { EncoderPath = FakeEncoderPath(), InputPath = "" };
            await vm.InitializeAsync(false).WaitWithDiagnosticsAsync(SafetyTimeout);
            await vm.WaitForIdleAsync().WaitWithDiagnosticsAsync(SafetyTimeout);
            Assert.NotNull(vm.HelpSchema);
            // 補完はテキストの一覧の行で確かめる (V8 のフォームの上の行のテキスト欄も同じ処理を使う)
            vm.IsTextListMode = textListMode;
            var window = new MainWindow { DataContext = vm, Width = 1300, Height = 800 };
            window.Show();
            await WaitForLayoutAsync(window, () => window.Bounds.Width > 0 && window.GetVisualDescendants().OfType<Button>().Any());
            return new Session(previous, services, vm, window);
        }

        public async Task<TextBox> AddLineAsync()
        {
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOpened(object? sender, EventArgs args) => opened.TrySetResult();
            Popup.Opened += OnOpened;
            var button = Window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "＋ 行を追加");
            button.Command!.Execute(null);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var line = ViewModel.FilterLines[^1];
            await WaitForLayoutAsync(Window, () => Window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Classes.Contains("filterLine") && ReferenceEquals(t.DataContext, line) && t.IsFocused));
            var box = Window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("filterLine") && ReferenceEquals(t.DataContext, line));
            Assert.True(box.IsFocused);
            try { await opened.Task.WaitWithDiagnosticsAsync(SafetyTimeout); }
            finally { Popup.Opened -= OnOpened; }
            return box;
        }

        public void SetText(TextBox box, string text)
        {
            box.Text = text;
            box.CaretIndex = text.Length;
        }

        public void SelectEntry(string text)
        {
            List.SelectedItem = Entries.First(entry => !entry.IsHeader && entry.Text == text);
        }

        /// <summary>文字位置 index の文字の中央 (ウィンドウ座標)。</summary>
        public Point PointAtChar(TextBox box, int index)
        {
            var presenter = box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().First();
            var rect = presenter.TextLayout.HitTestTextPosition(index);
            var local = new Point(rect.X + 3, presenter.Bounds.Height / 2);
            return presenter.TranslatePoint(local, Window) ?? throw new InvalidOperationException();
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await ViewModel.DisposeAsync();
            _services.Dispose();
            Environment.SetEnvironmentVariable("FAKE_ENCODER_HELP_FILE", _previousHelp);
        }
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

    internal sealed class TestServices : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-completion-{Guid.NewGuid():N}");
        public TestServices() => Directory.CreateDirectory(_directory);
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}

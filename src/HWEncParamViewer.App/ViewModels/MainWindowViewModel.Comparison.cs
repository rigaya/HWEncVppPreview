using System.Windows.Input;
using Avalonia.Media.Imaging;

namespace HWEncParamViewer.App.ViewModels;

/// <summary>プレビュー画像の比較表示方式。</summary>
public enum PreviewCompareMode { Single, Toggle, Split, SideBySide }

public sealed partial class MainWindowViewModel
{
    private PreviewCompareMode _compareMode;
    private bool _isShowingBefore;
    private Bitmap? _beforeImage;
    private bool _isBeforeBusy;
    private double _beforeDisplayWidth = 1920, _beforeDisplayHeight = 1080;
    private double _splitPosition = 0.5;
    private bool _useSar = true;

    /// <summary>ViewとのV5契約。取得処理の結合前は表示値だけを保持する。</summary>
    public PreviewCompareMode CompareMode { get => _compareMode; set => SetProperty(ref _compareMode, value); }
    public bool IsShowingBefore { get => _isShowingBefore; set => SetProperty(ref _isShowingBefore, value); }
    public Bitmap? BeforeImage { get => _beforeImage; set => SetProperty(ref _beforeImage, value); }
    public bool IsBeforeBusy { get => _isBeforeBusy; set => SetProperty(ref _isBeforeBusy, value); }
    public double BeforeDisplayWidth { get => _beforeDisplayWidth; set => SetProperty(ref _beforeDisplayWidth, value); }
    public double BeforeDisplayHeight { get => _beforeDisplayHeight; set => SetProperty(ref _beforeDisplayHeight, value); }
    public double SplitPosition { get => _splitPosition; set => SetProperty(ref _splitPosition, value); }
    public bool UseSar { get => _useSar; set => SetProperty(ref _useSar, value); }
    public ICommand SetCompareModeCommand { get; }
}

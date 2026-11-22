using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using HWEncVppPreview.Core;
using static HWEncVppPreview.Core.EncoderPreviewSession;

namespace HWEncVppPreview.App.ViewModels;

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
    /// <summary>Toggleは単一表示に正規化し、Spaceの押下状態はIsShowingBeforeで表す。</summary>
    public PreviewCompareMode CompareMode
    {
        get => _compareMode;
        set
        {
            var normalized = value is PreviewCompareMode.Split or PreviewCompareMode.SideBySide ? value : PreviewCompareMode.Single;
            if (_compareMode == normalized) return;
            _compareMode = normalized;
            _session.SetBeforeVisibility(_compareMode != PreviewCompareMode.Single || _isShowingBefore, _isShowingBefore);
            OnPropertyChanged();
            if (normalized != PreviewCompareMode.Single) IsShowingBefore = false;
            _session.RefreshBeforeSegmentProtection();
        }
    }
    public bool IsShowingBefore
    {
        get => _isShowingBefore;
        set
        {
            bool showing = value && CompareMode == PreviewCompareMode.Single;
            if (_isShowingBefore == showing) return;
            _isShowingBefore = showing;
            // 変更通知から別の操作が呼ばれても、セッションは新しい表示状態を使う。
            _session.SetBeforeVisibility(_compareMode != PreviewCompareMode.Single || _isShowingBefore, _isShowingBefore);
            OnPropertyChanged();
            RefreshColorInfo();
            _session.RefreshBeforeSegmentProtection();
            if (_isShowingBefore && BeforeImage == null) _session.PrioritizeBefore();
        }
    }
    /// <summary>前画像は独立したビットマップとして所有し、後画像と同じインスタンスを共有しない。</summary>
    public Bitmap? BeforeImage
    {
        get => _beforeImage;
        set
        {
            if (value == null) _session.InvalidateBeforePresentation();
            var previous = _beforeImage;
            if (!SetProperty(ref _beforeImage, value)) return;
            previous?.Dispose();
            if (value == null) _session.ReleaseBeforePresentationPixels();
            RefreshColorInfo();
        }
    }
    public bool IsBeforeBusy { get => _isBeforeBusy; set => SetProperty(ref _isBeforeBusy, value); }
    public double BeforeDisplayWidth { get => _beforeDisplayWidth; set => SetProperty(ref _beforeDisplayWidth, value); }
    public double BeforeDisplayHeight { get => _beforeDisplayHeight; set => SetProperty(ref _beforeDisplayHeight, value); }
    public double SplitPosition
    {
        get => _splitPosition;
        set => SetProperty(ref _splitPosition, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5);
    }
    public bool UseSar
    {
        get => _useSar;
        set { if (SetProperty(ref _useSar, value)) RecalculateDisplaySizes(); }
    }
    public ICommand SetCompareModeCommand { get; }

    private void RecalculateDisplaySizes()
    {
        RefreshColorInfo();
        if (_session.DisplayedAfter is { } after)
        {
            DisplayWidth = DisplayWidthFor(after.Header);
            DisplayHeight = after.Header.Height;
        }
        if (_session.DisplayedBefore is { } before)
        {
            BeforeDisplayWidth = DisplayWidthFor(before.Header);
            BeforeDisplayHeight = before.Header.Height;
        }
    }
    private double DisplayWidthFor(Y4mHeader header) => header.Width * (UseSar && header.SarNum > 0 && header.SarDen > 0
        ? (double)header.SarNum / header.SarDen : 1);

    private static WriteableBitmap CreateBitmap(Y4mFrame frame, byte[] data, long traceId = 0)
    {
        if (PerfTrace.Enabled) PerfTrace.Write("vm.bitmap.start", new { traceId, frame.Xts });
        var header = frame.Header;
        var bitmap = new WriteableBitmap(new PixelSize(header.Width, header.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        try
        {
            using var locked = bitmap.Lock();
            for (int y = 0; y < header.Height; y++) Marshal.Copy(data, y * header.Width * 4, locked.Address + y * locked.RowBytes, header.Width * 4);
            if (PerfTrace.Enabled) PerfTrace.Write("vm.bitmap.end", new { traceId, frame.Xts });
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}

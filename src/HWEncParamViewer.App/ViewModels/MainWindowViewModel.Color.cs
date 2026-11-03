using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>表示中の画像のRGB変換に使った行列・範囲。比較表示ではフィルタ後を基準にする。</summary>
    public string ColorInfoText
    {
        get
        {
            var frame = IsShowingBefore && BeforeImage is not null ? _beforeDisplayedFrame : _displayedFrame;
            if (frame is null) return "";
            var info = YuvToBgra.GetColorInfo(frame.Header);
            return info.Matrix + " " + info.Range + (info.IsHdr ? " · " + AppMessages.HdrAsSdr : "");
        }
    }

    private void RefreshColorInfo() => OnPropertyChanged(nameof(ColorInfoText));
}

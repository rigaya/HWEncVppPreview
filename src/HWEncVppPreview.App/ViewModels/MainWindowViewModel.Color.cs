using HWEncVppPreview.Core;
using System.Globalization;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private HdrDisplaySettings _hdrDisplaySettings = new();
    private long _hdrDisplaySettingsRevision;
    private ImageWork? _pendingAfterDisplayRefresh, _pendingBeforeDisplayRefresh;

    /// <summary>HDR表示をSDRの白へ合わせる基準輝度。変更はrawからの再描画だけに反映する。</summary>
    public double HdrReferenceWhiteNits
    {
        get => _hdrDisplaySettings.ReferenceWhiteNits;
        set
        {
            value = double.IsFinite(value) ? Math.Clamp(value, 50, 1000) : 203;
            if (value == _hdrDisplaySettings.ReferenceWhiteNits) return;
            lock (_taskLock)
            {
                _hdrDisplaySettings = _hdrDisplaySettings with { ReferenceWhiteNits = value };
                ++_hdrDisplaySettingsRevision;
            }
            OnPropertyChanged();
            RefreshColorInfo();
            // cacheは変換前のYUVを保持する。表示中だけを更新し、cacheの取得時は新しい設定で変換する。
            if (_displayedFrame is { } after && PreviewImage != null && YuvToBgra.GetColorInfo(after.Header).IsHdr)
                EnqueueImage(new(after, Volatile.Read(ref _generation), Volatile.Read(ref _editEpoch), DisplayOnly: true));
            if (_beforeDisplayedFrame is { } before && BeforeImage != null && YuvToBgra.GetColorInfo(before.Header).IsHdr)
                EnqueueImage(new(before, Volatile.Read(ref _generation), Volatile.Read(ref _editEpoch), Before: true, DisplayOnly: true));
        }
    }

    // 旧設定で終わった変換は、新しい画像の待機枠を潰さず同じrawからやり直す。
    private void RequeueForDisplaySettings(ImageWork image)
    {
        lock (_taskLock)
        {
            if (_disposed) return;
            if (image.DisplayOnly)
            { if ((image.Before ? _pendingBeforeDisplayRefresh : _pendingAfterDisplayRefresh) != null) return; }
            else if ((image.Before ? _pendingBeforeImage : _pendingAfterImage) != null) return;
            EnqueueImage(image.Retain());
        }
    }

    /// <summary>表示中の画像ごとのRGB変換内容。比較表示ではフィルタ後を基準にする。</summary>
    public string ColorInfoText
    {
        get
        {
            var frame = IsShowingBefore && BeforeImage is not null ? _beforeDisplayedFrame : _displayedFrame;
            if (frame is null) return "";
            var info = YuvToBgra.GetColorInfo(frame.Header);
            string transfer = frame.Header.Transfer?.Trim().ToLowerInvariant() is "smpte2084" or "pq" ? "PQ" : "HLG";
            return info.Matrix + " " + info.Range + (info.IsHdr
                ? " · " + transfer + "→SDR " + HdrReferenceWhiteNits.ToString("0.##", CultureInfo.InvariantCulture) + "nit" : "");
        }
    }

    /// <summary>表示中のAfterかBeforeがPQ/HLGで、SDRへ変換しているときだけ基準白の設定を出す。</summary>
    public bool IsHdrReferenceWhiteVisible => IsHdrFrame(_displayedFrame) || IsHdrFrame(_beforeDisplayedFrame);

    private static bool IsHdrFrame(Y4mFrame? frame) => frame != null && YuvToBgra.GetColorInfo(frame.Header).IsHdr;

    private void RefreshColorInfo()
    {
        OnPropertyChanged(nameof(ColorInfoText));
        OnPropertyChanged(nameof(IsHdrReferenceWhiteVisible));
    }
}

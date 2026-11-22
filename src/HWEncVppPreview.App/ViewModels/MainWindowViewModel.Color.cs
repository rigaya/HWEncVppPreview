using HWEncVppPreview.Core;
using System.Globalization;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private HdrDisplaySettings _hdrDisplaySettings = new();
    private long _hdrDisplaySettingsRevision;

    /// <summary>HDR表示をSDRの白へ合わせる基準輝度。変更はrawからの再描画だけに反映する。</summary>
    public double HdrReferenceWhiteNits
    {
        get => _hdrDisplaySettings.ReferenceWhiteNits;
        set
        {
            value = double.IsFinite(value) ? Math.Clamp(value, 50, 1000) : 203;
            if (value == _hdrDisplaySettings.ReferenceWhiteNits) return;
            lock (_session.TaskSync)
            {
                _hdrDisplaySettings = _hdrDisplaySettings with { ReferenceWhiteNits = value };
                ++_hdrDisplaySettingsRevision;
            }
            OnPropertyChanged();
            RefreshColorInfo();
            // cacheは変換前のYUVを保持する。表示中だけを更新し、cacheの取得時は新しい設定で変換する。
            if (_session.DisplayedAfter is { } after && PreviewImage != null && YuvToBgra.GetColorInfo(after.Header).IsHdr)
                _session.RequestDisplayRefresh(after, before: false);
            if (_session.DisplayedBefore is { } before && BeforeImage != null && YuvToBgra.GetColorInfo(before.Header).IsHdr)
                _session.RequestDisplayRefresh(before, before: true);
        }
    }

    /// <summary>表示中の画像ごとのRGB変換内容。比較表示ではフィルタ後を基準にする。</summary>
    public string ColorInfoText
    {
        get
        {
            var frame = IsShowingBefore && BeforeImage is not null ? _session.DisplayedBefore : _session.DisplayedAfter;
            if (frame is null) return "";
            var info = YuvToBgra.GetColorInfo(frame.Header);
            string transfer = frame.Header.Transfer?.Trim().ToLowerInvariant() is "smpte2084" or "pq" ? "PQ" : "HLG";
            return info.Matrix + " " + info.Range + (info.IsHdr
                ? " · " + transfer + "→SDR " + HdrReferenceWhiteNits.ToString("0.##", CultureInfo.InvariantCulture) + "nit" : "");
        }
    }

    /// <summary>表示中のAfterかBeforeがPQ/HLGで、SDRへ変換しているときだけ基準白の設定を出す。</summary>
    public bool IsHdrReferenceWhiteVisible => IsHdrFrame(_session.DisplayedAfter) || IsHdrFrame(_session.DisplayedBefore);

    private static bool IsHdrFrame(Y4mFrame? frame) => frame != null && YuvToBgra.GetColorInfo(frame.Header).IsHdr;

    private void RefreshColorInfo()
    {
        OnPropertyChanged(nameof(ColorInfoText));
        OnPropertyChanged(nameof(IsHdrReferenceWhiteVisible));
    }
}

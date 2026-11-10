using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private long? _beforePresentationSegmentId;
    private long _beforePresentationFrameId;
    private int _beforeProtectionDirection;

    private void SelectBeforePresentationSegment(long? segmentId, long frameId = 0)
    {
        _beforePresentationSegmentId = segmentId is > 0 ? segmentId : null;
        _beforePresentationFrameId = _beforePresentationSegmentId.HasValue ? frameId : 0;
        RefreshBeforeSegmentProtection();
    }

    private void SetBeforeProtectionDirection(int direction)
    {
        direction = Math.Sign(direction);
        if (_beforeProtectionDirection == direction) return;
        // 受理した移動だけで更新し、遅れて終わる画像変換から方向を巻き戻さない。
        _beforeProtectionDirection = direction;
        RefreshBeforeSegmentProtection();
    }

    private void RefreshBeforeSegmentProtection()
    {
        bool visible = _beforeEnabled && (CompareMode != PreviewCompareMode.Single || IsShowingBefore);
        // 同じ取得区間を共有する元画像では、後処理画像の保護だけを使って従来の回収順を保つ。
        // 次の要求を準備している間も、直前の共有区間を別画像の保護へ切り替えない。
        bool shared = (_context?.Request ?? _intervalLogicalRequest ?? _intervalRequest)?.VppArgs.Count == 0;
        long? segmentId = visible && !shared ? _beforePresentationSegmentId : null;
        _cache.ProtectDisplayedBeforeSegment(segmentId, segmentId.HasValue ? _beforePresentationFrameId : 0,
            segmentId.HasValue ? _beforeProtectionDirection : 0);
    }
}

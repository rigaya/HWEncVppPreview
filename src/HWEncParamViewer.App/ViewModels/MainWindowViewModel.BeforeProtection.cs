using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private long? _beforePresentationSegmentId;

    private void SelectBeforePresentationSegment(long? segmentId)
    {
        _beforePresentationSegmentId = segmentId is > 0 ? segmentId : null;
        RefreshBeforeSegmentProtection();
    }

    private void RefreshBeforeSegmentProtection()
    {
        bool visible = _beforeEnabled && (CompareMode != PreviewCompareMode.Single || IsShowingBefore);
        // 同じ取得区間を共有する元画像では、後処理画像の保護だけを使って従来の回収順を保つ。
        bool shared = _context?.Request.VppArgs.Count == 0;
        _cache.ProtectDisplayedBeforeSegment(visible && !shared ? _beforePresentationSegmentId : null);
    }
}

using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private ReversePrefetchWait? _reversePrefetchWait;

    private void NotifyAcceptedFrameStep(PreviewRequest logical, int direction, double seconds)
    {
        if (_disposed || direction == 0) return;
        // 表示変換の完了や捨てたキー反復ではなく、実際に確定した移動をCoreへ一度だけ渡す。
        _controller.NotifyNavigation(logical with { TargetSeconds = seconds }, Math.Sign(direction));
    }

    private bool TryWaitForReversePrefetch(int direction)
    {
        if (direction >= 0 || !_initialized || _disposed) return false;
        var logical = _intervalLogicalRequest ?? Volatile.Read(ref _context)?.Request ?? _intervalRequest;
        if (logical == null) return false;
        var pending = _controller.GetPendingReversePrefetch(logical with { TargetSeconds = CurrentSeconds });
        if (pending == null) return false;
        long generation = BeginIntent(navigation: true);
        var waiting = new ReversePrefetchWait(generation, direction, pending);
        _reversePrefetchWait = waiting;
        _pendingNeighbor = new NeighborIntent(generation, direction);
        IsBusy = true;
        ErrorText = null;
        StatusText = AppMessages.Processing;
        if (PerfTrace.Enabled) PerfTrace.Write("vm.reverse_prefetch.wait", new { generation, direction, target = CurrentSeconds });
        Track(WaitForReversePrefetchAsync(waiting));
        return true;
    }

    private async Task WaitForReversePrefetchAsync(ReversePrefetchWait waiting)
    {
        try { await waiting.Pending.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            // 先読み失敗は前景の画像や状態へ流さず、必要になった隣だけ通常取得へ戻す。
            if (PerfTrace.Enabled) PerfTrace.Write("vm.reverse_prefetch.wait_failed", new { exception.Message });
        }
        if (_disposed) return;
        await PostAsync(() =>
        {
            if (!ReferenceEquals(_reversePrefetchWait, waiting)) return;
            _reversePrefetchWait = null;
            if (_pendingNeighbor?.Generation == waiting.Generation) _pendingNeighbor = null;
            if (!IsCurrent(waiting.Generation)) return;
            // promiseは保存・画素検証・破棄まで含む。旧snapshotやindexを復活させず隣を再解決する。
            ValidateNavigationState();
            StepFrame(waiting.Direction);
        }).ConfigureAwait(false);
    }

    private void LeaveReversePrefetchWaitForForwardStep(int direction)
    {
        if (direction <= 0 || _reversePrefetchWait is not { } waiting) return;
        _reversePrefetchWait = null;
        if (_pendingNeighbor?.Generation == waiting.Generation) _pendingNeighbor = null;
        // 向きの変更はUI側の待ちだけを失効させ、Coreの有限先読みは取り消さない。
    }

    private sealed record ReversePrefetchWait(long Generation, int Direction, Task Pending);
}

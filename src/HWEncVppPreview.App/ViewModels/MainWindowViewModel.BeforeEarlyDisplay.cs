using Avalonia.Threading;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const double EarlyBeforeTargetToleranceSeconds = 1e-8;

    private void QueueEarlyBeforeFrame(RequestContext context, PreviewFrameResult value, PreviewSegmentSnapshot? segment)
    {
        if (!value.CanDisplayBeforeEnd || value.TargetNotReached || segment?.Clock.TrustedAbsolute != true
            || !IsCurrent(context) || Volatile.Read(ref context.BeforeCompletionReceived) != 0) return;
        // 同期通知の一枚だけを保ち、UI待ちのために親区間を取得し直さない。
        if (!TryRetainSelection(value.Frame, "VmBeforeEarlyPost", out var ownership))
        {
            if (PerfTrace.Enabled) PerfTrace.Write("vm.before.early.miss", new { context.Generation,
                value.Frame.FrameId, value.Frame.Xts, target = value.Request.TargetSeconds });
            return;
        }
        long revision = Volatile.Read(ref context.DisplayRevision);
        var post = CaptureOwnershipPost(context, ownership, () =>
        {
            // END先着と目標変更をUIで再確認し、古い候補を最新の画像へ戻さない。
            if (!IsCurrent(context) || revision != Volatile.Read(ref context.DisplayRevision)
                || Volatile.Read(ref context.BeforeCompletionReceived) != 0
                || Math.Abs(CurrentSeconds - value.Request.TargetSeconds) > EarlyBeforeTargetToleranceSeconds) return;
            context.BeforeEarlyFrame = value.Frame;
            context.BeforeEarlyTarget = CurrentSeconds;
            DisplayBeforeFrame(value.Frame, Volatile.Read(ref _generation), revisionContext: context,
                sourceSegment: segment, earlyDisplay: true);
        });
        try { Dispatcher.UIThread.Post(post.Execute); }
        catch { post.Dispose(); throw; }
    }

    private bool CompleteEarlyBeforeFrame(RequestContext context, Y4mFrame frame)
    {
        var early = context.BeforeEarlyFrame;
        if (early == null) return false;
        bool applied = BeforeImage != null && ReferenceEquals(_beforeDisplayedFrame, early);
        bool same = applied && ReferenceEquals(frame, early);
        bool targetChanged = Math.Abs(context.BeforeEarlyTarget - CurrentSeconds) > EarlyBeforeTargetToleranceSeconds;
        string reason = context.BeforeEarlyFallbackReason ?? (targetChanged ? "目標時刻が変わった"
            : same ? "早期画像と最終選択が一致した" : applied ? "完了時の最終選択が異なった" : "早期画像は未適用");
        if (PerfTrace.Enabled)
        {
            var data = new { context.Generation, context.Request.CorrelationId, target = CurrentSeconds,
                earlyTarget = context.BeforeEarlyTarget, oldFrameId = early.FrameId, oldXts = early.Xts,
                oldXdur = early.Xdur, newFrameId = frame.FrameId, newXts = frame.Xts, newXdur = frame.Xdur,
                applied, same, reason };
            PerfTrace.Write("vm.before.early.final", data);
            if (applied && !same) PerfTrace.Write("vm.before.early.replaced", data);
        }
        context.BeforeEarlyFrame = null;
        // 既表示の一枚なら変換だけ省き、ENDで保存した正区間への保護を必ず引き継ぐ。
        if (!same || targetChanged) return false;
        Interlocked.Increment(ref _beforeImageGeneration);
        SelectBeforePresentationSegment(_beforeIntervalSegment?.SegmentId, frame.FrameId);
        IsBeforeBusy = false;
        return true;
    }
}

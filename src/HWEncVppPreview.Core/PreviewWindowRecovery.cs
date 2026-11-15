namespace HWEncVppPreview.Core;

/// <summary>実受信した窓から診断した、目標選択の先頭側・末尾側の不足。</summary>
public sealed record PreviewWindowShortfall(bool MissingHead, bool MissingTail, double? FirstXts, double? LastXts,
    PreviewRequest RequestedWindow, double TargetSeconds, double FirstSeconds, double LastSeconds,
    double SafeStartSeconds, double SafeEndSeconds, bool ReachedInputEnd)
{
    /// <summary>信頼済みの絶対時計でのみ不足を判定する。相対時計の着地位置を推測しない。</summary>
    public static PreviewWindowShortfall? FromSegment(PreviewRequest request, PreviewSegmentSnapshot? segment)
    {
        if (segment is not { Clock.TrustedAbsolute: true } || segment.Frames.Count == 0) return null;
        var frames = segment.Frames;
        double target = request.TargetSeconds;
        double first = segment.Clock.ToInputSeconds(frames[0], 0, request.Fps);
        double last = segment.Clock.ToInputSeconds(frames[^1], frames.Count - 1, request.Fps);
        int raw = FrameSelector.Select(frames, segment.Clock.ToFrameSeconds(target), request.Fps);
        double candidate = raw < 0 ? first : segment.Clock.ToInputSeconds(frames[raw], raw, request.Fps);
        bool head = segment.ReachedInputEnd && last < segment.SafeStartSeconds - PreviewSafeRange.ToleranceSeconds
            || !PreviewSafeRange.Contains(target, segment.SafeStartSeconds, double.PositiveInfinity)
            || raw < 0 && first > target + FrameSelector.Duration(frames[0], request.Fps) / 2;
        bool tail = !(segment.ReachedInputEnd && head) && !PreviewSafeRange.IsKnownInputEnd(request, segment, target) && (PreviewSafeRange.IsAfter(target, segment.SafeEndSeconds)
            || PreviewSafeRange.IsAfter(candidate, segment.SafeEndSeconds)
            || !segment.ReachedInputEnd && target > last + FrameSelector.Duration(frames[^1], request.Fps) / 2);
        return !head && !tail ? null : new(head, tail, frames[0].Xts, frames[^1].Xts,
            segment.PhysicalRequest, target, first, last, segment.SafeStartSeconds, segment.SafeEndSeconds, segment.ReachedInputEnd);
    }
}

/// <summary>選択規則や安全域を緩めず、不足した側の物理窓だけを有限回広げる。</summary>
public static class PreviewWindowRecovery
{
    public const int MaximumRetries = 2;
    public const int MaximumEmptyRetries = 5;

    /// <summary>空の正常終了では末尾の位置を断定せず、最大31秒・5回の範囲でseekを前へ戻す。</summary>
    public static bool TryBacktrackEmpty(PreviewRequest logical, PreviewRequest physical, int attempt, out PreviewRequest expanded)
    {
        expanded = logical;
        double seek = CommandBuilder.Plan(physical).SeekSeconds ?? 0;
        if (attempt < 0 || attempt >= MaximumEmptyRetries || seek <= 0) return false;
        double nextSeek = Math.Max(0, Math.Floor((seek - Math.Pow(2, attempt)) * 1000) / 1000);
        expanded = logical with { RecoveryPhysicalWindow = physical with
        {
            TargetSeconds = logical.TargetSeconds, SeekSecondsOverride = nextSeek,
            PreMarginSeconds = Math.Max(0, logical.TargetSeconds - nextSeek), Continuous = true,
            DisableOutputStart = true, RecoveryPhysicalWindow = null,
        } };
        return true;
    }

    private const double GuardSeconds = 0.5;
    private const double ExtraMarginSeconds = 0.5;

    /// <summary>同じ論理要求を保持し、次に実行する物理窓だけを拡張する。</summary>
    public static bool TryExpand(PreviewRequest logical, PreviewWindowShortfall shortage, int attempt, out PreviewRequest expanded)
    {
        expanded = logical;
        if (attempt < 0 || attempt >= MaximumRetries || shortage.ReachedInputEnd && shortage.MissingTail) return false;
        var physical = shortage.RequestedWindow;
        var plan = CommandBuilder.Plan(physical);
        double seek = plan.SeekSeconds ?? 0;
        double nextSeek = seek, post = physical.PostMarginSeconds;
        double scale = Math.Pow(2, attempt);
        if (shortage.MissingHead)
        {
            if (seek <= 0) return false;
            // 実際のkey着地の遅れも含める。目標との差だけでは長いGOP内で同じkeyに着地し続ける。
            double missing = Math.Max(shortage.FirstSeconds - seek, shortage.SafeStartSeconds - logical.TargetSeconds);
            nextSeek = Math.Max(0, seek - (Math.Max(0, missing) + GuardSeconds + ExtraMarginSeconds) * scale);
            // 後ろへ丸めて同じ窓に戻らないよう、回復時のseekだけをmsへ切り下げる。
            nextSeek = Math.Floor(nextSeek * 1000) / 1000;
        }
        if (shortage.MissingTail)
        {
            double missing = Math.Max(0, logical.TargetSeconds - shortage.SafeEndSeconds);
            post += (missing + GuardSeconds + ExtraMarginSeconds) * scale;
        }
        if (nextSeek == seek && post == physical.PostMarginSeconds) return false;
        var next = physical with
        {
            TargetSeconds = logical.TargetSeconds, SeekSecondsOverride = nextSeek,
            PreMarginSeconds = Math.Max(0, logical.TargetSeconds - nextSeek), PostMarginSeconds = post,
            Continuous = false, RecoveryPhysicalWindow = null,
        };
        // 引数・世代・所有者は論理要求に残し、物理窓のコピーを内部だけで受け渡す。
        expanded = logical with { RecoveryPhysicalWindow = next };
        return true;
    }
}

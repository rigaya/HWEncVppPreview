namespace HWEncVppPreview.Core;

/// <summary>GOPの前余白と、監査済みの処理列に必要な暖機を同じ物理計画で管理する。</summary>
public static class PreviewWarmupPolicy
{
    public const double GopMarginSeconds = 1;
    public const double DefaultWarmupSeconds = 2;

    /// <summary>エンコーダの実probeが履歴非依存と確認した場合だけ暖機を0にする。</summary>
    public static double RequiredWarmupSeconds(PreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.DependsOnHistory == false ? 0 : DefaultWarmupSeconds;
    }

    internal static PreviewRequest PreparePhysical(PreviewRequest request, bool background)
    {
        if (!request.Capabilities.TimestampPassthrough || !request.TimestampOriginSeconds.HasValue)
            return request with { WarmupSeconds = DefaultWarmupSeconds };
        // 有限でも通常の順方向窓は対象にする。逆方向の拡張窓と明示seekは従来の2秒暖機を維持する。
        bool forwardWindow = request.Continuous || background
            || request.PreMarginSeconds <= DefaultWarmupSeconds && request.SeekSecondsOverride is null;
        bool canSkip = forwardWindow && request.TimestampOriginTrusted
            && RequiredWarmupSeconds(request) == 0;
        double warmup = canSkip ? 0 : DefaultWarmupSeconds;
        // 既存の背景要求の非ゼロ暖機はseekや安全域を変更しない。通常のBeforeはfilterなしなので上の0秒対象。
        if (background && !canSkip) return request with { WarmupSeconds = warmup };
        double displayMargin = Math.Max(GopMarginSeconds, request.PreMarginSeconds - DefaultWarmupSeconds);
        double physicalMargin = canSkip ? displayMargin : Math.Max(GopMarginSeconds + warmup, request.PreMarginSeconds);
        return request with
        {
            WarmupSeconds = warmup,
            PreMarginSeconds = canSkip ? displayMargin : request.PreMarginSeconds,
            SeekSecondsOverride = Math.Max(0, request.TargetSeconds - physicalMargin),
        };
    }

    internal static double SafeStartSeconds(PreviewRequest physical, bool background)
    {
        double seek = CommandBuilder.Plan(physical).SeekSeconds ?? 0;
        bool guarded = !background && physical.Capabilities.TimestampPassthrough && physical.TimestampOriginSeconds.HasValue;
        // 入力先頭は従来どおり除外せず、seekした区間だけ確定済みの暖機を除く。
        return guarded && seek > 0 ? seek + physical.WarmupSeconds : seek;
    }
}

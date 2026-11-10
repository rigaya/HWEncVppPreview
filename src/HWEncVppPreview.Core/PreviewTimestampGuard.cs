namespace HWEncVppPreview.Core;

/// <summary>absolute出力の要求内の後退と大jumpを検出する。正しい長durationのVFRは大きい側で許容する。</summary>
// ログ検索と不具合報告で照合できるよう、内部診断は表示言語に依存させない。
internal sealed class PreviewTimestampGuard(double requestedFps)
{
    // 長いVFRの表示時間を許しつつ、wrapなど秒単位の飛びを拾うため8倍を上限にする。
    public const double JumpDurationMultiplier = 8;
    public string Reason { get; private set; } = "要求内Xtsが不正です";
    private const double MinimumIncrementSeconds = 1e-8;
    private Y4mFrame? _previous;
    private double? _previousTimestamp;
    public bool IsInvalid(Y4mFrame frame)
    {
        bool invalid = false;
        if (frame.Xts is double current)
        {
            invalid = !double.IsFinite(current);
            if (_previousTimestamp is double previous)
            {
                double delta = current - previous;
                double duration = Math.Max(FrameSelector.Duration(_previous ?? frame, requestedFps), FrameSelector.Duration(frame, requestedFps));
                if (delta <= MinimumIncrementSeconds) { invalid = true; Reason = "要求内Xtsが増加していません"; }
                else if (duration > 0 && delta > duration * JumpDurationMultiplier + 1e-8)
                { invalid = true; Reason = "要求内Xtsのjumpが出力durationの8倍を超えました"; }
            }
            _previousTimestamp = current;
        }
        // 欠損行を挟んでも最後の有効時刻を失わない。durationは直前の物理フレームを使う。
        _previous = frame;
        return invalid;
    }
}

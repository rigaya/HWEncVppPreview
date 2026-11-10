namespace HWEncVppPreview.Core;

/// <summary>helpで確認できたプレビュー用の追加オプション。</summary>
public sealed record PreviewCapabilities(bool TimestampPassthrough = false, bool OpenClTaskThreads = false,
    bool ColorMatrix = false, bool ColorPrimaries = false, bool Transfer = false, bool ColorRange = false, bool ChromaLocation = false);

/// <summary>生のフレーム時計と入力先頭基準の時計との対応。</summary>
public sealed record PreviewClockMap(double? InputOriginSeconds, double PhysicalSeekSeconds, bool TrustedAbsolute)
{
    public const double TimestampWrapPeriodSeconds = 8589934592.0 / 90000;
    // wrap前後で原点と出力が別周回になるのを避け、境界の前後10秒は保守的に有限へ戻す。
    public const double TimestampWrapGuardSeconds = 10;

    /// <summary>33bit/90kの非zero境界の前後10秒を、passthroughに使わない原点と判定する。</summary>
    public static bool IsNearTimestampWrap(double origin)
    {
        if (!double.IsFinite(origin)) return false;
        double boundary = Math.Round(origin / TimestampWrapPeriodSeconds);
        return boundary != 0 && Math.Abs(origin - boundary * TimestampWrapPeriodSeconds) <= TimestampWrapGuardSeconds;
    }

    // prefixを捨てても、Xtsがない列の元のフレーム番号を保持する。
    internal int FrameIndexOffset { get; init; }

    /// <summary>フレームの生Xtsを入力先頭からの秒へ変換する。</summary>
    public double ToInputSeconds(Y4mFrame frame, int index, double fps)
    {
        if (frame.Xts is double timestamp)
            return TrustedAbsolute && InputOriginSeconds is double origin ? timestamp - origin : PhysicalSeekSeconds + timestamp;
        double rate = FrameSelector.FrameRate(frame, fps);
        return PhysicalSeekSeconds + (rate > 0 ? (index + FrameIndexOffset) / rate : 0);
    }

    /// <summary>配列内の選択に使う秒へ変換する。全Xtsなしの列だけ、解放済みprefixの番号を差し引く。</summary>
    public double ToSelectionSeconds(double inputSeconds, IReadOnlyList<Y4mFrame> frames, double fps)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0 || frames.Any(frame => frame.Xts.HasValue)) return ToFrameSeconds(inputSeconds);
        // XtsなしのToInputSecondsは物理seekと元番号を使うため、その逆変換も同じ基準にする。
        double rate = FrameSelector.FrameRate(frames[0], fps);
        return inputSeconds - PhysicalSeekSeconds - (rate > 0 ? FrameIndexOffset / rate : 0);
    }

    /// <summary>入力先頭からの秒を、この区間の生フレーム時計へ戻す。</summary>
    public double ToFrameSeconds(double inputSeconds) => TrustedAbsolute && InputOriginSeconds is double origin
        ? inputSeconds + origin : inputSeconds - PhysicalSeekSeconds;
}

/// <summary>取得区間と、暖機・末尾処理を除いた表示可能な範囲。</summary>
public sealed record PreviewSegmentSnapshot(long SegmentId, PreviewRequest PhysicalRequest, PreviewClockMap Clock,
    IReadOnlyList<Y4mFrame> Frames, double SafeStartSeconds, double SafeEndSeconds, bool IsActive, bool ReachedInputEnd)
{
    /// <summary>他の取得区間へ接続できるか。absolute時計の信頼性とは別で、照合待ちもfalse。</summary>
    public bool CanStitch { get; init; } = true;
}

/// <summary>物理区間を共有しながら、最新の表示要求へ通知する更新。</summary>
public sealed record PreviewSegmentUpdate(PreviewRequest Request, PreviewSegmentSnapshot Segment,
    bool IsReadPaused, bool DemandSatisfied);

/// <summary>元の物理区間と位置を保持する、安全な移動候補の参照。画像と生Xtsは複製・書換えしない。</summary>
public sealed record PreviewFrameReference(PreviewSegmentSnapshot Segment, int FrameIndex)
{
    public Y4mFrame Frame => Segment.Frames[FrameIndex];
    public PreviewClockMap Clock => Segment.Clock;
    public long SegmentId => Segment.SegmentId;
    public double InputSeconds => Clock.ToInputSeconds(Frame, FrameIndex, Segment.PhysicalRequest.Fps);
    public bool IsSafe => PreviewSafeRange.Contains(InputSeconds, Segment.SafeStartSeconds, Segment.SafeEndSeconds);

    /// <summary>移動の基準となる1枚だけを保持し、古い区間全体の画素を固定しない参照へ変える。</summary>
    public PreviewFrameReference RetainFrameOnly()
    {
        var clock = Clock with { FrameIndexOffset = Clock.FrameIndexOffset + FrameIndex };
        return new(Segment with { Frames = Array.AsReadOnly(new[] { Frame }), Clock = clock }, 0);
    }
}

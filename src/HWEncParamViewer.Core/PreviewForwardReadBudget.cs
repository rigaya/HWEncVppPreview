namespace HWEncParamViewer.Core;

// 秒から枚数やbyteを推測せず、中心より後に実在するframeだけを先読み量として数える。
internal static class PreviewForwardReadBudget
{
    internal const int MinimumAheadFrames = 4;
    internal readonly record struct State(bool NeedsRead, long AheadBytes, int AheadFrames, long ByteLimit, string Reason);

    internal static State Evaluate(PreviewSegmentSnapshot? segment, double demand, double last, long capacity)
    {
        long limit = capacity / 4;
        bool needsTime = segment?.Clock.TrustedAbsolute == false || last < demand + 2;
        long bytes = 0;
        int frames = 0;
        bool actual = segment?.Clock.TrustedAbsolute == true;
        if (segment != null)
        {
            for (int index = 0; index < segment.Frames.Count; ++index)
            {
                var frame = segment.Frames[index];
                if (frame.Xts is not double xts || !double.IsFinite(xts)) { actual = false; break; }
                double seconds = segment.Clock.ToInputSeconds(frame, index, segment.PhysicalRequest.Fps);
                if (seconds <= demand + 1e-8) continue;
                bytes = checked(bytes + frame.Data.LongLength);
                ++frames;
            }
        }
        // 4枚は実nextと最大3点の照合余地。2秒を越えて最低枚数を作る保証ではない。
        bool byteStop = capacity > 0 && actual && frames >= MinimumAheadFrames && bytes >= limit;
        bool needs = needsTime && !byteStop;
        return new(needs, bytes, frames, limit, !needsTime ? "time" : byteStop ? "bytes" : "reading");
    }
}

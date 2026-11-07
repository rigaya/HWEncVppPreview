namespace HWEncParamViewer.Core;

public sealed partial class PreviewController
{
    private sealed class OutputFormatInfo(PreviewRequest request, Y4mHeader header)
    {
        internal PreviewRequest Request = request;
        internal Y4mHeader Header = header;
        internal double? LastTimestamp;
        internal double MaximumGap;
    }

    private readonly List<OutputFormatInfo> _outputFormats = new();

    private void RememberOutputHeader(PreviewRequest request, Y4mHeader header)
    {
        lock (_sync)
        {
            var known = _outputFormats.FirstOrDefault(item => SameFormatSource(item.Request, request));
            if (known == null) known = new(request, header);
            else
            {
                _outputFormats.Remove(known);
                known.Header = header;
                known.LastTimestamp = null;
            }
            _outputFormats.Add(known);
            if (_outputFormats.Count > 32) _outputFormats.RemoveAt(0);
        }
    }

    private void RememberOutputFrame(PreviewRequest request, Y4mFrame frame)
    {
        if (frame.Xts is not double timestamp || !double.IsFinite(timestamp)) return;
        lock (_sync)
        {
            var known = _outputFormats.LastOrDefault(item => SameFormatSource(item.Request, request));
            if (known == null) return;
            if (known.LastTimestamp is double last && timestamp > last)
                known.MaximumGap = Math.Max(known.MaximumGap, timestamp - last);
            known.LastTimestamp = timestamp;
        }
    }

    private PreviewRequest WithOutputStartHint(PreviewRequest logical, PreviewRequest physical)
    {
        var header = OutputHeaderFor(logical);
        double rate = header?.Fps > 0 ? header.Fps : logical.Fps;
        lock (_sync)
        {
            var known = _outputFormats.LastOrDefault(item => SameFormatSource(item.Request, logical));
            double gap = Math.Max(rate > 0 ? 1 / rate : 0, known?.MaximumGap ?? 0);
            return physical with { OutputFrameIntervalHint = gap > 0 ? gap : null };
        }
    }

    private static bool SameFormatSource(PreviewRequest first, PreviewRequest second)
        => first.EncoderPath == second.EncoderPath && first.InputFile == second.InputFile
            && first.InputArgs.SequenceEqual(second.InputArgs) && first.VppArgs.SequenceEqual(second.VppArgs);

    private PreviewRequest LimitReverseWindow(PreviewRequest logical, PreviewRequest physical, bool background)
    {
        if (background || logical.Continuous || logical.PreMarginSeconds < 3
            || !physical.Capabilities.TimestampPassthrough || !physical.TimestampOriginSeconds.HasValue
            || logical.RecoveryPhysicalWindow != null) return physical;
        var header = OutputHeaderFor(logical);
        if (header == null) return physical;
        long limit = Math.Min(_cache.CapacityBytes > 0 ? _cache.CapacityBytes : PreviewFiniteBudget.CapacityBytes,
            PreviewFiniteBudget.CapacityBytes / 2);
        if (!PreviewByteWindow.TryLimit(physical, header, limit, out var limited))
            throw new InvalidOperationException("The preview raw byte budget cannot retain an overlap and a display frame.");
        if (PerfTrace.Enabled && CommandBuilder.Plan(limited) != CommandBuilder.Plan(physical))
            PerfTrace.Write("core.byte_window", new
            {
                logical.CorrelationId, header.FrameBytes, header.Fps, limit,
                previous = CommandBuilder.Plan(physical), next = CommandBuilder.Plan(limited), physical.WarmupSeconds,
            });
        return limited;
    }
}

/// <summary>暖機とguardは維持し、有限の逆取得で表示する部分だけをbyte上限へ収める。</summary>
internal static class PreviewByteWindow
{
    internal const double TailGuardSeconds = 0.5;
    internal const int ReducedOverlapFrameCount = 4;
    internal const int MinimumRetainedFrameCount = ReducedOverlapFrameCount + 2;

    internal static bool TryLimit(PreviewRequest physical, Y4mHeader header, long capacityBytes, out PreviewRequest limited)
    {
        limited = physical;
        double rate = header.Fps > 0 ? header.Fps : physical.Fps;
        int bytes = header.FrameBytes;
        if (bytes <= 0 || rate <= 0) return true;
        long frames = capacityBytes / bytes;
        // guardのpixelは保持しない。元の表示域が収まる場合、従来の0.5秒の重なりは変えない。
        double overlap = Math.Max(0, physical.PostMarginSeconds - TailGuardSeconds);
        double seek = CommandBuilder.Plan(physical).SeekSeconds ?? 0;
        double current = Math.Max(0, physical.TargetSeconds - seek - (seek > 0 ? physical.WarmupSeconds : 0));
        double available = (frames - 1) / rate;
        if (current + overlap > available)
        {
            // 縮める通常逆窓はguardしか持たない場合もある。照合の重なりを4出力frame残し、その分だけ表示域を狭める。
            // 余裕のある窓には加えず、既存のprefetchの縮小幅と同じ値にそろえる。
            overlap = ReducedOverlapFrameCount / rate;
            physical = physical with { PostMarginSeconds = TailGuardSeconds + overlap };
        }
        double display = available - overlap;
        if (display <= 0) return false;
        // 入力先頭の履歴は省けない。seek=0を保ち、必要なら保持するprefixだけを限定する。
        if (seek <= 0 || current <= display)
        {
            limited = physical;
            return true;
        }
        double nextSeek = Math.Max(0, physical.TargetSeconds - physical.WarmupSeconds - display);
        limited = physical with { SeekSecondsOverride = nextSeek, PreMarginSeconds = physical.WarmupSeconds + display };
        return true;
    }
}

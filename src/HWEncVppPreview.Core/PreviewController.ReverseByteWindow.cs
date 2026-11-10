namespace HWEncVppPreview.Core;

public sealed partial class PreviewController
{
    private Y4mHeader? OutputHeaderFor(PreviewRequest request)
    {
        lock (_sync)
        {
            var header = _outputFormats.LastOrDefault(item => SameFormatSource(item.Request, request))?.Header ?? request.InputFormatHint;
            if (header != null) return header;
            return _cache.TryGetAt(request, request.TargetSeconds, out var segment)
                ? segment.Frames.FirstOrDefault()?.Header : null;
        }
    }

    private bool TryPrepareReverseWindow(PreviewRequest logical, double boundary, out PreviewRequest request)
    {
        request = logical;
        var header = OutputHeaderFor(logical);
        if (header == null || header.FrameBytes <= 0) return false;
        double rate = header.Fps > 0 ? header.Fps : logical.Fps;
        if (rate <= 0) return false;
        var intended = logical with
        {
            TargetSeconds = boundary, Continuous = false,
            PreMarginSeconds = ReversePrefetchWarmupSeconds + ReversePrefetchDisplaySeconds,
            PostMarginSeconds = ReversePrefetchGuardSeconds + ReversePrefetchOverlapSeconds,
            SeekSecondsOverride = null, RecoveryPhysicalWindow = null, RetainedByteLimit = null,
        };
        var physical = PreviewWarmupPolicy.PreparePhysical(intended, false);
        long maximum = Math.Min(_cache.CapacityBytes, PreviewFiniteBudget.CapacityBytes / 2);
        if (maximum <= 0) return false;
        if (!PreviewByteWindow.TryLimit(physical, header, maximum, out var limited)) return false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            double overlap = Math.Max(0, limited.PostMarginSeconds - ReversePrefetchGuardSeconds);
            long room = _cache.ReverseWindowRoom(logical, boundary, logical.TargetSeconds, overlap);
            long capacity = Math.Min(maximum, room);
            if (capacity < (long)header.FrameBytes * PreviewByteWindow.MinimumRetainedFrameCount)
            {
                if (overlap > PreviewByteWindow.ReducedOverlapFrameCount / rate)
                {
                    limited = limited with
                    {
                        PostMarginSeconds = ReversePrefetchGuardSeconds + PreviewByteWindow.ReducedOverlapFrameCount / rate,
                    };
                    continue;
                }
                return false;
            }
            if (!PreviewByteWindow.TryLimit(physical with { PostMarginSeconds = limited.PostMarginSeconds },
                header, capacity, out var next)) return false;
            if (Math.Abs(next.PostMarginSeconds - limited.PostMarginSeconds) > 1e-9)
            {
                limited = next;
                continue;
            }
            if (!_cache.TryTrimReverseWindow(logical, boundary, logical.TargetSeconds, overlap, capacity)) return false;
            request = intended with
            {
                PreMarginSeconds = next.PreMarginSeconds, PostMarginSeconds = next.PostMarginSeconds,
                SeekSecondsOverride = next.SeekSecondsOverride, RecoveryPhysicalWindow = next, RetainedByteLimit = capacity,
            };
            return true;
        }
        return false;
    }
}

namespace HWEncVppPreview.Core;

// 需要合流だけに使う少数の観測。将来のseek時間や未知のVFR間隔を保証する統計ではない。
internal sealed class ForwardStartupHistory
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
    private sealed record Sample(IPreviewBackend Backend, PreviewRequest Request, Y4mHeader Header, long At, double Seconds);
    private readonly Sample?[] _samples = new Sample?[12];
    private int _next;

    internal void Remember(IPreviewBackend backend, PreviewRequest request, Y4mHeader header, long at, double seconds)
    {
        if (!(seconds > 0) || !double.IsFinite(seconds)) return;
        _samples[_next] = new(backend, request, header, at, seconds);
        _next = (_next + 1) % _samples.Length;
    }

    internal bool TryMinimum(TimeProvider clock, IPreviewBackend backend, PreviewRequest request, Y4mHeader header, out double seconds)
    {
        // 間隔hintは実Xtsの丸めや最大gapの更新で変わる。hintを跨ぐ最短値を使い、
        // 起動待ちを大きく見積もる方向へ選別しない。到達ETAの間隔は現streamだけで測る。
        seconds = double.PositiveInfinity;
        int count = 0;
        foreach (var sample in _samples)
        {
            if (sample == null || !ReferenceEquals(sample.Backend, backend)
                || !FrameCache.SameContent(sample.Request, request)
                || sample.Request.WarmupSeconds != request.WarmupSeconds
                || sample.Request.DependsOnHistory != request.DependsOnHistory
                || sample.Request.DisableOutputStart != request.DisableOutputStart
                || sample.Request.Fps != request.Fps
                || sample.Header.Width != header.Width || sample.Header.Height != header.Height
                || sample.Header.BitDepth != header.BitDepth || sample.Header.Chroma != header.Chroma
                || sample.Header.FpsNum != header.FpsNum || sample.Header.FpsDen != header.FpsDen
                || clock.GetElapsedTime(sample.At) > MaximumAge) continue;
            seconds = Math.Min(seconds, sample.Seconds);
            ++count;
        }
        return count >= 2;
    }
}

internal sealed class ForwardReadProgress
{
    // 固定の到達距離ではなく、短いpipe burstを速度とみなさないための観測幅。
    internal static readonly TimeSpan MinimumBlock = TimeSpan.FromMilliseconds(100);
    private readonly double[] _recentRates = new double[2];
    private int _completedBlocks, _blockFrames;
    private bool _hasFrame;
    private long _firstAt, _blockAt, _lastAt;
    private double _firstInput, _blockInput, _lastInput, _maxGap, _maxBlockSeconds;
    internal bool Reading { get; private set; }

    internal void SetReading(bool reading)
    {
        if (Reading == reading) return;
        Reading = reading;
        ResetFrames();
    }

    private void ResetFrames()
    {
        _hasFrame = false; _completedBlocks = 0; _blockFrames = 0;
        _maxGap = 0; _maxBlockSeconds = 0;
    }

    internal void Observe(TimeProvider clock, double input)
    {
        if (!Reading || !double.IsFinite(input)) { ResetFrames(); return; }
        long now = clock.GetTimestamp();
        if (_hasFrame && input <= _lastInput) ResetFrames();
        if (!_hasFrame)
        {
            _hasFrame = true; _firstAt = _blockAt = _lastAt = now;
            _firstInput = _blockInput = _lastInput = input;
            return;
        }
        _maxGap = Math.Max(_maxGap, input - _lastInput);
        _lastInput = input; _lastAt = now; ++_blockFrames;
        double span = clock.GetElapsedTime(_blockAt, now).TotalSeconds;
        if (span < MinimumBlock.TotalSeconds || _blockFrames < 2) return;
        _recentRates[_completedBlocks % _recentRates.Length] = (input - _blockInput) / span;
        ++_completedBlocks;
        _maxBlockSeconds = Math.Max(_maxBlockSeconds, span);
        _blockAt = now; _blockInput = input; _blockFrames = 0;
    }

    internal bool TryEstimate(TimeProvider clock, double center, double restartSeconds,
        out double etaSeconds, out double rate, out double age, out double maxGap)
    {
        etaSeconds = rate = age = double.NaN; maxGap = _maxGap;
        if (!Reading || !_hasFrame || _completedBlocks < _recentRates.Length
            || !(center > _lastInput) || !(restartSeconds > 0) || !double.IsFinite(restartSeconds)) return false;
        double span = clock.GetElapsedTime(_firstAt, _lastAt).TotalSeconds;
        if (!(span > 0)) return false;
        rate = Math.Min((_lastInput - _firstInput) / span, _recentRates.Min());
        // 未完了の最近blockが遅くなっている場合も、過去の速いblockだけで許可しない。
        double pendingSpan = clock.GetElapsedTime(_blockAt, _lastAt).TotalSeconds;
        if (pendingSpan > 0) rate = Math.Min(rate, (_lastInput - _blockInput) / pendingSpan);
        age = clock.GetElapsedTime(_lastAt).TotalSeconds;
        if (!(rate > 0) || !double.IsFinite(rate) || age > _maxBlockSeconds) return false;
        etaSeconds = age + (center - _lastInput + _maxGap) / rate;
        // 再取得を有利に見積もる。実測の半分未満でも、次のseekより速い保証ではない。
        return etaSeconds < restartSeconds / 2;
    }
}

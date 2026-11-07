using HWEncParamViewer.Core;
using HWEncParamViewer.Tests.Testing;

namespace HWEncParamViewer.Tests;

public sealed class ForwardAdoptionTimingTests
{
    private static ForwardReadProgress Observe(FakeTimeProvider clock, int milliseconds = 50, double step = 0.1, int count = 6)
    {
        var progress = new ForwardReadProgress(); progress.SetReading(true); progress.Observe(clock, 0);
        for (int i = 1; i <= count; ++i)
        {
            // 各frameのtrue通知でも同じ読取区間はresetしない。
            progress.SetReading(true); clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); progress.Observe(clock, i * step);
        }
        return progress;
    }

    [Theory]
    [InlineData(0.7, 1, true)]
    [InlineData(2, 1, false)]
    [InlineData(0.7, 0.1, false)]
    public void 同じ観測でも実距離と起動待ちで判断を分ける(double center, double restart, bool expected)
    {
        var clock = new FakeTimeProvider(); var progress = Observe(clock);
        Assert.Equal(expected, progress.TryEstimate(clock, center, restart, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("one_block")]
    [InlineData("burst")]
    [InlineData("slow")]
    [InlineData("stale")]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("backward")]
    [InlineData("pending_slow")]
    public void 観測不足と古い観測と遅い進捗は拒否する(string reason)
    {
        var clock = new FakeTimeProvider();
        var progress = Observe(clock, reason == "burst" ? 0 : 50, reason == "slow" ? 0.01 : 0.1, reason == "one_block" ? 2 : 6);
        double center = reason == "slow" ? 0.16 : 0.7;
        if (reason == "stale") clock.Advance(TimeSpan.FromMilliseconds(101));
        if (reason is "pause" or "resume") progress.SetReading(false);
        if (reason == "resume") progress.SetReading(true);
        if (reason == "backward") progress.Observe(clock, 0.1);
        if (reason == "pending_slow") { clock.Advance(TimeSpan.FromMilliseconds(150)); progress.Observe(clock, 0.61); }
        Assert.False(progress.TryEstimate(clock, center, 1, out _, out _, out _, out _));
    }

    [Fact]
    public void ageと実最大間隔をEtaへ加算する()
    {
        var clock = new FakeTimeProvider(); var progress = Observe(clock);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(progress.TryEstimate(clock, 0.7, 1, out double eta, out double rate, out double age, out double gap));
        Assert.Equal(2, rate, 8); Assert.Equal(0.05, age, 8); Assert.Equal(0.1, gap, 8); Assert.Equal(0.15, eta, 8);
        Assert.False(progress.TryEstimate(clock, 0.7, 0.299, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("warmup")]
    [InlineData("history")]
    [InlineData("output_start")]
    [InlineData("fps")]
    [InlineData("vpp")]
    [InlineData("header")]
    [InlineData("age")]
    public void 別条件や期限外の起動待ちを混ぜない(string reason)
    {
        var clock = new FakeTimeProvider(); var backend = new Stub(); var history = new ForwardStartupHistory();
        var request = new PreviewRequest { EncoderPath = "stub", InputFile = "input", Fps = 10 };
        var header = new Y4mHeader { Width = 2, Height = 2 };
        history.Remember(backend, request, header, clock.GetTimestamp(), 1);
        history.Remember(backend, request, header, clock.GetTimestamp(), 2);
        if (reason == "age") clock.Advance(ForwardStartupHistory.MaximumAge + TimeSpan.FromTicks(1));
        request = reason switch
        {
            "warmup" => request with { WarmupSeconds = 0 }, "history" => request with { DependsOnHistory = false },
            "output_start" => request with { DisableOutputStart = true }, "fps" => request with { Fps = 20 },
            "vpp" => request with { VppArgs = new[] { "different" } }, _ => request,
        };
        Assert.False(history.TryMinimum(clock, reason == "backend" ? new Stub() : backend, request,
            reason == "header" ? new Y4mHeader { Width = 4, Height = 2 } : header, out _));
    }

    [Fact]
    public void hint未観測から丸め実測が変わっても全条件の保守最短を使う()
    {
        var clock = new FakeTimeProvider(); var backend = new Stub(); var history = new ForwardStartupHistory();
        var request = new PreviewRequest(); var header = new Y4mHeader();
        history.Remember(backend, request, header, clock.GetTimestamp(), 0);
        history.Remember(backend, request, header, clock.GetTimestamp(), double.NaN);
        history.Remember(backend, request, header, clock.GetTimestamp(), 0.2);
        Assert.False(history.TryMinimum(clock, backend, request, header, out _));
        history.Remember(backend, request with { OutputFrameIntervalHint = 0.016683 }, header, clock.GetTimestamp(), 1);
        Assert.True(history.TryMinimum(clock, backend, request with { OutputFrameIntervalHint = 0.016689 }, header, out double minimum));
        Assert.Equal(0.2, minimum);
    }

    [Fact]
    public void 直接の既照合だけを許可し二hopや未接続は許可しない()
    {
        var cache = new FrameCache(); var request = new PreviewRequest { EncoderPath = "stub", InputFile = "input", Fps = 10 };
        Y4mFrame Frame(int i) => new(new Y4mHeader { Width = 2, Height = 2 }, 1000 + i / 10d, 0.1, new byte[6]);
        long Store(int first) => cache.StoreSegment(request, Enumerable.Range(first, 6).Select(Frame).ToArray(),
            new(1000, 0, true), first / 10d, (first + 6) / 10d)!.SegmentId;
        long a = Store(0), b = Store(4), c = Store(8);
        Assert.True(cache.AreDirectlyConnected(a, b)); Assert.True(cache.AreDirectlyConnected(b, c));
        Assert.False(cache.AreDirectlyConnected(a, c));
        long pending = cache.BeginSegment(request, new(1000, 0, true), 0.4);
        Assert.False(cache.AreDirectlyConnected(a, pending));
        Assert.True(cache.Append(pending, Frame(4)));
        Assert.False(cache.AreDirectlyConnected(a, pending));
    }

    private sealed class Stub : IPreviewBackend
    {
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => Task.FromResult(new PreviewRunResult(0, ""));
    }
}

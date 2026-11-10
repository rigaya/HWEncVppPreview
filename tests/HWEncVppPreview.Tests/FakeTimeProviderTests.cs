using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public class FakeTimeProviderTests
{
    [Fact]
    public void 期限順と同時刻の登録順で呼び出す()
    {
        var clock = new FakeTimeProvider();
        var calls = new List<int>();
        using var late = clock.CreateTimer(_ => calls.Add(3), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        using var first = clock.CreateTimer(_ => calls.Add(1), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        using var second = clock.CreateTimer(_ => calls.Add(2), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        var start = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 1, 2, 3 }, calls);
        Assert.Equal(start.AddSeconds(3), clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, clock.GetTimestamp());
        Assert.Equal(TimeSpan.TicksPerSecond, clock.TimestampFrequency);
    }

    [Fact]
    public void コールバック内で期限変更と自身の破棄ができる()
    {
        var clock = new FakeTimeProvider();
        var times = new List<long>();
        ITimer? timer = null;
        timer = clock.CreateTimer(_ =>
        {
            times.Add(clock.GetTimestamp());
            if (times.Count == 1)
                Assert.True(timer!.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
            else
                timer!.Dispose();
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        using (timer)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromSeconds(3).Ticks }, times);
            Assert.False(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        }
    }

    [Fact]
    public void 周期タイマーはAdvance内の全期限で呼び出す()
    {
        var clock = new FakeTimeProvider();
        var times = new List<long>();
        using var timer = clock.CreateTimer(_ => times.Add(clock.GetTimestamp()), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(new[] { 1L, 3L, 5L }.Select(s => s * TimeSpan.TicksPerSecond), times);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(4, times.Count);
    }

    [Fact]
    public async Task 無限待機は変更まで呼ばれず非同期破棄でも停止する()
    {
        var clock = new FakeTimeProvider();
        int calls = 0;
        var timer = clock.CreateTimer(_ => calls++, null, Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, calls);
        Assert.True(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        clock.Advance(TimeSpan.Zero);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, calls);
        Assert.True(timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        await timer.DisposeAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void コールバック内で同時刻の別タイマーを破棄できる()
    {
        var clock = new FakeTimeProvider();
        int calls = 0;
        ITimer? second = null;
        using var first = clock.CreateTimer(_ => second!.Dispose(), null,
            TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        second = clock.CreateTimer(_ => calls++, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        using (second)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(0, calls);
        }
    }

    [Fact]
    public void 負の経過と不正な期限は拒否する()
    {
        var clock = new FakeTimeProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.CreateTimer(_ => { }, null,
            TimeSpan.FromMilliseconds(-2), Timeout.InfiniteTimeSpan));
    }
}

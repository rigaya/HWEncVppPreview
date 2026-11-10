using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class RawFrameWindowTests
{
    private const int FrameCount = 3;
    private const int SingleFrameCount = 1;
    private const int FrameBytes = 6;
    private const int FrameRate = 30;
    private const int SharingIterations = 32;
    private const int RaceIterations = 64;
    private const byte PixelValue = 0x2a;
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = FrameRate, FpsDen = 1 };

    [Fact]
    public void 同じ窓の共有は取得元の所有を保って最後の返却だけで解放する()
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, FrameCount);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        long initial = scope.Capture().TotalRetains;
        using var window = RawFrameWindow.Capture(frames, "Window");
        Assert.Equal(initial + FrameCount, scope.Capture().TotalRetains);
        foreach (var producer in producers) producer.Dispose();
        long retained = scope.Capture().TotalRetains;
        for (int i = 0; i < SharingIterations; ++i)
        {
            using var copy = window.Share();
            using var same = window.ShareIfSame(frames.ToArray());
            Assert.NotNull(same);
            Assert.Equal(retained, scope.Capture().TotalRetains);
            Assert.Equal(FrameCount, scope.Capture().LiveTokens);
        }
        using var last = window.Share();
        window.Dispose();
        window.Dispose();
        Assert.Throws<ObjectDisposedException>(() => window.Share());
        Assert.Throws<ObjectDisposedException>(() => window.ShareIfSame(frames));
        for (int index = 0; index < FrameCount; ++index)
        {
            Assert.Same(frames[index], window.Frames[index]);
            Assert.Same(frames[index], last.Frames[index]);
            Assert.Equal(PixelValue, last.Frames[index].RawPixels[0]);
        }
        Assert.Equal(FrameCount, scope.Capture().OwnerKinds["Window"]);
        last.Dispose();
        AssertReleased(scope);
        Assert.Equal(frames[0].FrameId, window.Frames[0].FrameId);
        Assert.Throws<ObjectDisposedException>(() => window.Frames[0].RawPixels);
    }

    [Fact]
    public void subsetや順序違いや同じ時刻の別frameを共有窓と見なさない()
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, FrameCount);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        using var window = RawFrameWindow.Capture(frames, "Window");
        foreach (var producer in producers) producer.Dispose();
        var before = scope.Capture();
        Assert.Null(window.ShareIfSame(frames.Take(SingleFrameCount).ToArray()));
        Assert.Null(window.ShareIfSame(frames.Reverse().ToArray()));
        var replacement = frames.ToArray();
        replacement[0] = new Y4mFrame(Header, frames[0].Xts, frames[0].Xdur, new byte[FrameBytes]);
        Assert.Null(window.ShareIfSame(replacement));
        Assert.Equal(before.TotalRetains, scope.Capture().TotalRetains);
        Assert.Equal(before.LiveTokens, scope.Capture().LiveTokens);
        window.Dispose();
        AssertReleased(scope);
    }

    [Fact]
    public async Task 共有と最後の返却が競合しても解放済みの画素を復活させない()
    {
        var scope = new RawOwnershipScope();
        for (int iteration = 0; iteration < RaceIterations; ++iteration)
        {
            using var producer = Frames(scope, SingleFrameCount)[0];
            var frame = producer.Frame;
            using var window = RawFrameWindow.Capture(new[] { frame }, "Window");
            producer.Dispose();
            RawFrameWindow? shared = null;
            bool rejected = false;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task ShareAsync()
            {
                await start.Task.ConfigureAwait(false);
                try
                {
                    shared = iteration % 2 == 0 ? window.Share() : window.ShareIfSame(new[] { frame });
                }
                catch (ObjectDisposedException) { rejected = true; }
            }
            async Task ReleaseAsync()
            {
                await start.Task.ConfigureAwait(false);
                window.Dispose();
            }
            Task share = ShareAsync();
            Task release = ReleaseAsync();
            start.SetResult();
            await Task.WhenAll(share, release).WaitWithDiagnosticsAsync(Safety);
            if (shared == null) Assert.True(rejected);
            else
            {
                Assert.False(rejected);
                Assert.Same(frame, Assert.Single(shared.Frames));
                Assert.Equal(PixelValue, frame.RawPixels[0]);
                shared.Dispose();
            }
            Assert.True(scope.Capture().IsEmpty);
            Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
            Assert.Throws<ObjectDisposedException>(() => window.Share());
        }
        Assert.Equal(RaceIterations, scope.Capture().NewOwners);
        AssertReleased(scope);
    }

    [Fact]
    public void Adoptは取得済みの所有を再取得せず受け取り記述子を固定する()
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, FrameCount);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        var first = frames[0];
        var ownership = new CountingOwnership(RawFrameSetLease.Capture(frames, "CacheWindow"));
        foreach (var producer in producers) producer.Dispose();
        long retained = scope.Capture().TotalRetains;
        using var window = RawFrameWindow.Adopt(frames, ownership);
        frames[0] = new Y4mFrame(Header, 0, 1d / FrameRate, new byte[FrameBytes]);
        Assert.Same(first, window.Frames[0]);
        var list = Assert.IsAssignableFrom<IList<Y4mFrame>>(window.Frames);
        Assert.Throws<NotSupportedException>(() => list.Add(first));
        using var shared = window.Share();
        Assert.Equal(retained, scope.Capture().TotalRetains);
        window.Dispose();
        Assert.Equal(0, ownership.Calls);
        Assert.Equal(PixelValue, shared.Frames[0].RawPixels[0]);
        shared.Dispose();
        shared.Dispose();
        Assert.Equal(1, ownership.Calls);
        AssertReleased(scope);
    }

    [Fact]
    public void Adoptの記述子取得で失敗しても受け取った所有を一度返す()
    {
        var scope = new RawOwnershipScope();
        using var producer = Frames(scope, SingleFrameCount)[0];
        var ownership = new CountingOwnership(RawFrameSetLease.Capture(new[] { producer.Frame }, "CacheWindow"));
        producer.Dispose();
        Assert.Throws<InvalidOperationException>(() => RawFrameWindow.Adopt(new ThrowingFrames(), ownership));
        Assert.Equal(1, ownership.Calls);
        AssertReleased(scope);
    }

    [Fact]
    public void Capture途中の失敗はそれまで取得したframeを返す()
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, FrameCount);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        producers[^1].Dispose();
        var before = scope.Capture();
        Assert.Throws<ObjectDisposedException>(() => RawFrameWindow.Capture(frames, "Window"));
        Assert.Equal(before.LiveTokens, scope.Capture().LiveTokens);
        foreach (var producer in producers) producer.Dispose();
        AssertReleased(scope);
    }

    [Fact]
    public void 最後の所有返却は共有状態の排他を持たずに呼び出す()
    {
        RawFrameWindow? window = null;
        var ownership = new CountingOwnership(onDispose: () =>
        {
            // 別threadから共有入口へ戻れることで、返却先の処理と排他が循環しないことを確かめる。
            _ = Task.Run(() => Assert.Throws<ObjectDisposedException>(() => window!.Share()))
                .WaitWithDiagnosticsAsync(Safety).GetAwaiter().GetResult();
        });
        window = RawFrameWindow.Adopt(Array.Empty<Y4mFrame>(), ownership);
        window.Dispose();
        Assert.Equal(1, ownership.Calls);
        Assert.Empty(window.Frames);
    }

    [Fact]
    public void legacyの共有窓は返却後も元のGC配列と記述子を保持する()
    {
        byte[] pixels = new byte[FrameBytes];
        var frame = new Y4mFrame(Header, 0, 1d / FrameRate, pixels);
        using var window = RawFrameWindow.Capture(new[] { frame }, "LegacyWindow");
        using var shared = window.ShareIfSame(new[] { frame });
        Assert.NotNull(shared);
        window.Dispose();
        Assert.Same(pixels, shared.Frames[0].Data);
        shared.Dispose();
        Assert.Same(frame, Assert.Single(window.Frames));
        Assert.Same(pixels, window.Frames[0].Data);
    }

    [Theory]
    [InlineData(SingleFrameCount)]
    [InlineData(FrameCount)]
    public void Try取得した選択窓の共有は親全体を固定せず同じ順と画素を保つ(int selectedCount)
    {
        const int ParentFrameCount = FrameCount + 2;
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, ParentFrameCount);
        var parent = producers.Select(producer => producer.Frame).ToArray();
        var selected = parent.Skip(SingleFrameCount).Take(selectedCount).Reverse().ToArray();
        var initial = scope.Capture();
        Assert.True(RawFrameWindow.TryCapture(selected, "Rescue", out var window));
        Assert.NotNull(window);
        using var captured = window;
        Assert.Equal(initial.TotalRetains + selectedCount, scope.Capture().TotalRetains);
        Assert.Equal(initial.TryRetainAttempts + selectedCount, scope.Capture().TryRetainAttempts);
        Assert.Equal(initial.TryRetainSuccesses + selectedCount, scope.Capture().TryRetainSuccesses);
        Assert.Equal(initial.TryRetainFailures, scope.Capture().TryRetainFailures);
        foreach (var producer in producers) producer.Dispose();
        Assert.Equal(selectedCount, scope.Capture().LiveOwners);
        Assert.Equal(selectedCount, scope.Capture().LiveTokens);
        using var shared = captured.Share();
        Assert.Equal(initial.TotalRetains + selectedCount, scope.Capture().TotalRetains);
        captured.Dispose();
        for (int index = 0; index < selectedCount; ++index)
        {
            Assert.Same(selected[index], shared.Frames[index]);
            Assert.Equal(selected[index].FrameId, shared.Frames[index].FrameId);
            Assert.Equal(selected[index].RawRunId, shared.Frames[index].RawRunId);
            Assert.Equal(PixelValue, shared.Frames[index].RawPixels[0]);
        }
        foreach (var frame in parent.Except(selected))
            Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
        Assert.Equal(selectedCount, scope.Capture().OwnerKinds["Rescue"]);
        shared.Dispose();
        AssertReleased(scope);
        Assert.Same(selected[0], captured.Frames[0]);
        foreach (var frame in selected) Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(FrameCount - 1)]
    public void Try取得の失敗は取得済みだけを返して後続を試さず復活させない(int failedIndex)
    {
        var scope = new RawOwnershipScope();
        var producers = Frames(scope, FrameCount);
        var frames = producers.Select(producer => producer.Frame).ToArray();
        producers[failedIndex].Dispose();
        var initial = scope.Capture();
        Assert.False(RawFrameWindow.TryCapture(frames, "Rescue", out var window));
        Assert.Null(window);
        var failed = scope.Capture();
        Assert.Equal(initial.TryRetainAttempts + failedIndex + 1, failed.TryRetainAttempts);
        Assert.Equal(initial.TryRetainSuccesses + failedIndex, failed.TryRetainSuccesses);
        Assert.Equal(initial.TryRetainFailures + 1, failed.TryRetainFailures);
        Assert.Equal(initial.TotalRetains + failedIndex, failed.TotalRetains);
        Assert.Equal(initial.TotalTokenReleases + failedIndex, failed.TotalTokenReleases);
        Assert.Equal(initial.LiveTokens, failed.LiveTokens);
        Assert.False(failed.OwnerKinds.ContainsKey("Rescue"));
        foreach (var producer in producers) producer.Dispose();
        AssertReleased(scope);
        foreach (var frame in frames)
        {
            Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
            Assert.False(RawFrameWindow.TryCapture(new[] { frame }, "Retry", out var retry));
            Assert.Null(retry);
        }
        Assert.Equal(failed.TryRetainFailures + FrameCount, scope.Capture().TryRetainFailures);
        Assert.Equal(failed.TotalRetains, scope.Capture().TotalRetains);
        AssertReleased(scope);
    }

    [Fact]
    public void Try取得途中の記述子例外でも取得済みを一度だけ返す()
    {
        var scope = new RawOwnershipScope();
        using var producer = Frames(scope, SingleFrameCount)[0];
        var initial = scope.Capture();
        // 最初の一枚の取得後に一覧が壊れても、例外が窓の所有を置き去りにしないことを確かめる。
        Assert.Throws<InvalidOperationException>(() => RawFrameWindow.TryCapture(
            new PartiallyThrowingFrames(producer.Frame), "Rescue", out _));
        var failed = scope.Capture();
        Assert.Equal(initial.TryRetainAttempts + SingleFrameCount, failed.TryRetainAttempts);
        Assert.Equal(initial.TotalRetains + SingleFrameCount, failed.TotalRetains);
        Assert.Equal(initial.TotalTokenReleases + SingleFrameCount, failed.TotalTokenReleases);
        Assert.Equal(initial.LiveTokens, failed.LiveTokens);
        Assert.False(failed.OwnerKinds.ContainsKey("Rescue"));
        Assert.Equal(PixelValue, producer.Pixels.Span[0]);
        producer.Dispose();
        AssertReleased(scope);
    }

    [Fact]
    public void Try取得は空窓とlegacyを受け取りlegacy分の台帳を増やさない()
    {
        var scope = new RawOwnershipScope();
        Assert.True(RawFrameWindow.TryCapture(Array.Empty<Y4mFrame>(), "Empty", out var empty));
        Assert.NotNull(empty);
        using (empty)
        using (var copy = empty.Share()) Assert.Empty(copy.Frames);
        Assert.Empty(empty.Frames);
        byte[] pixels = Enumerable.Repeat(PixelValue, FrameBytes).ToArray();
        var legacy = new Y4mFrame(Header, 0, 1d / FrameRate, pixels);
        using var producer = Frames(scope, SingleFrameCount)[0];
        var initial = scope.Capture();
        Assert.True(RawFrameWindow.TryCapture(new[] { legacy, producer.Frame, legacy }, "Mixed", out var mixed));
        Assert.NotNull(mixed);
        using (mixed)
        {
            Assert.Equal(initial.TryRetainAttempts + SingleFrameCount, scope.Capture().TryRetainAttempts);
            Assert.Equal(initial.TotalRetains + SingleFrameCount, scope.Capture().TotalRetains);
            producer.Dispose();
            Assert.Same(legacy, mixed.Frames[0]);
            Assert.Same(legacy, mixed.Frames[^1]);
            Assert.Same(pixels, mixed.Frames[0].Data);
            Assert.Equal(PixelValue, mixed.Frames[1].RawPixels[0]);
        }
        Assert.Same(pixels, mixed.Frames[^1].Data);
        Assert.Throws<ObjectDisposedException>(() => mixed.Frames[1].RawPixels);
        AssertReleased(scope);
    }

    private sealed class PartiallyThrowingFrames(Y4mFrame first) : IReadOnlyList<Y4mFrame>
    {
        public int Count => FrameCount;
        public Y4mFrame this[int index] => index == 0 ? first : throw new InvalidOperationException("途中の記述子取得を失敗させる。");
        public IEnumerator<Y4mFrame> GetEnumerator() => throw new InvalidOperationException("indexでの取得を検証する。");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static RawFrameLease[] Frames(RawOwnershipScope scope, int count)
    {
        long run = scope.NewRunId();
        return Enumerable.Range(0, count).Select(index => scope.Create(Header, index / (double)FrameRate,
            1d / FrameRate, Enumerable.Repeat(PixelValue, FrameBytes).ToArray(), run, "Reader")).ToArray();
    }

    private static void AssertReleased(RawOwnershipScope scope)
    {
        var state = scope.Capture();
        Assert.True(state.IsEmpty);
        Assert.Equal(0, state.LiveBytes);
        Assert.Equal(state.TotalRetains, state.TotalTokenReleases);
        Assert.Equal(state.NewOwners, state.FinalReleases);
        Assert.Equal(state.NewBytes, state.FinalReleasedBytes);
    }

    private sealed class CountingOwnership(IDisposable? inner = null, Action? onDispose = null) : IDisposable
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public void Dispose()
        {
            Interlocked.Increment(ref _calls);
            try { onDispose?.Invoke(); }
            finally { inner?.Dispose(); }
        }
    }

    private sealed class ThrowingFrames : IReadOnlyList<Y4mFrame>
    {
        public int Count => SingleFrameCount;
        public Y4mFrame this[int index] => throw new InvalidOperationException("記述子の取得失敗を再現する。");
        public IEnumerator<Y4mFrame> GetEnumerator() => throw new InvalidOperationException("記述子の取得失敗を再現する。");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

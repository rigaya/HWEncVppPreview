using System.Reflection;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class BeforeDirectionalTrimTests
{
    private const int FrameBytes = 6;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 60, FpsDen = 1 };

    [Theory]
    [InlineData(-7, 103, 110)]
    [InlineData(0, 106, 113)]
    [InlineData(7, 110, 117)]
    public void 容量不足では表示一枚を含む連続した方向窓を残す(int direction, int firstTime, int lastTime)
    {
        const int CapacityFrames = 64;
        const int BeforeFrames = 21;
        const int CurrentFrames = CapacityFrames - BeforeFrames;
        const int AnchorIndex = 10;
        using var state = new Fixture(CapacityFrames * FrameBytes);
        var before = state.Store("before", Enumerable.Range(100, BeforeFrames).Select(value => (double)value).ToArray(), reachedEnd: true);
        var current = state.Store("after", Enumerable.Range(0, CurrentFrames).Select(value => (double)value).ToArray());
        state.Cache.PinCurrent(current.SegmentId, 0);
        state.ReleaseProducers();
        long retains = state.Scope.Capture().TotalRetains;
        long attempts = state.Scope.Capture().TryRetainAttempts;
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[AnchorIndex].FrameId, direction);
        Assert.True(state.Cache.CanAppend(current.SegmentId, FrameBytes));
        var kept = state.Cache.GetSegment(before.SegmentId)!;
        Assert.Equal(Enumerable.Range(firstTime, lastTime - firstTime + 1).Select(value => (double)value), kept.Frames.Select(frame => frame.Xts!.Value));
        Assert.Contains(before.Frames[AnchorIndex], kept.Frames);
        Assert.Equal(8, kept.Frames.Count);
        Assert.True(kept.Frames.Sum(frame => frame.RawByteLength) <= state.Cache.CapacityBytes / 8);
        Assert.Equal(firstTime - 100, kept.Clock.FrameIndexOffset);
        Assert.Equal(firstTime, kept.SafeStartSeconds);
        Assert.Equal(lastTime + 1d / 60, kept.SafeEndSeconds, 9);
        Assert.False(kept.ReachedInputEnd);
        Assert.Equal(retains, state.Scope.Capture().TotalRetains);
        Assert.Equal(attempts, state.Scope.Capture().TryRetainAttempts);
        Assert.Equal(0, state.Cache.PendingRawReleaseCount);
        Assert.Equal(current.Frames, state.Cache.GetSegment(current.SegmentId)!.Frames);
        Assert.True(state.Cache.Append(current.SegmentId, state.Frame(CurrentFrames)));
        Assert.Equal(CurrentFrames + 1, state.Cache.GetSegment(current.SegmentId)!.Frames.Count);
        Assert.True(state.Cache.SizeBytes <= state.Cache.CapacityBytes);
    }

    [Theory]
    [InlineData(-1, 18, 100, 102)]
    [InlineData(0, 17, 101, 103)]
    [InlineData(1, 15, 102, 104)]
    public void 固定枚数ではなく実際の可変bytesで八分の一を決める(int direction, int expectedBytes, int firstTime, int lastTime)
    {
        const int CapacityBytes = 176;
        int[] sizes = { 6, 8, 4, 5, 6 };
        using var state = new Fixture(CapacityBytes);
        var frames = sizes.Select((bytes, index) => state.Frame(100 + index, bytes)).ToArray();
        var before = state.StoreFrames("before", frames);
        var current = state.StoreFrames("after", new[] { state.Frame(0, CapacityBytes - sizes.Sum()) });
        state.Cache.PinCurrent(current.SegmentId, 0);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, frames[2].FrameId, direction);
        state.ReleaseProducers();
        Assert.True(state.Cache.CanAppend(current.SegmentId, 1));
        var kept = state.Cache.GetSegment(before.SegmentId)!;
        Assert.Equal(expectedBytes, kept.Frames.Sum(frame => frame.RawByteLength));
        Assert.Equal(firstTime, kept.Frames[0].Xts);
        Assert.Equal(lastTime, kept.Frames[^1].Xts);
        Assert.True(state.Cache.SizeBytes <= CapacityBytes);
    }

    [Fact]
    public void 方向窓で足りなければ表示一枚まで縮めそれ以上は回収しない()
    {
        const int CapacityFrames = 16;
        const int BeforeFrames = 8;
        using var state = new Fixture(CapacityFrames * FrameBytes);
        var before = state.Store("before", Enumerable.Range(10, BeforeFrames).Select(value => (double)value).ToArray());
        var current = state.Store("after", Enumerable.Range(0, BeforeFrames).Select(value => (double)value).ToArray());
        var anchor = before.Frames[4];
        state.Cache.PinCurrent(current.SegmentId, 0);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, anchor.FrameId, -1);
        state.ReleaseProducers();
        Assert.True(state.Cache.CanAppend(current.SegmentId, 7 * FrameBytes));
        Assert.Same(anchor, Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames));
        Assert.False(state.Cache.CanAppend(current.SegmentId, 8 * FrameBytes));
        Assert.Same(anchor, Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames));
        Assert.Equal((BeforeFrames + 1) * FrameBytes, state.Cache.SizeBytes);
        Assert.Equal(FrameBytes, anchor.RawPixels.Length);
    }

    [Fact]
    public void 一枚が八分の一枠より大きくても全容量内ならその一枚を残す()
    {
        const int CapacityBytes = 64;
        const int LargeFrameBytes = 12;
        using var state = new Fixture(CapacityBytes);
        var anchor = state.Frame(10, LargeFrameBytes);
        var before = state.StoreFrames("before", new[] { state.Frame(9), anchor });
        var current = state.StoreFrames("after", new[] { state.Frame(0, CapacityBytes - LargeFrameBytes - FrameBytes) });
        state.Cache.PinCurrent(current.SegmentId, 0);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, anchor.FrameId, -1);
        state.ReleaseProducers();
        Assert.True(state.Cache.CanAppend(current.SegmentId, FrameBytes));
        Assert.Same(anchor, Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames));
        Assert.True(anchor.RawByteLength > CapacityBytes / 8);
        Assert.False(state.Cache.CanAppend(current.SegmentId, CapacityBytes + 1));
        Assert.Same(anchor, Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void 全容量より大きい一枚はcacheへ保存しない(int capacity)
    {
        using var state = new Fixture(capacity);
        var frame = state.Frame(10);
        Assert.Null(state.Cache.StoreSegment(Fixture.Request("before"), new[] { frame }, new(0, 0, true), 10, 11));
        state.Cache.ProtectDisplayedBeforeSegment(long.MaxValue, frame.FrameId, -1);
        Assert.Equal(0, state.Cache.Count);
        Assert.Equal(0, state.Cache.SizeBytes);
        Assert.Null(Hint(state.Cache, "_displayedBeforeSegmentId"));
    }

    [Theory]
    [InlineData("old_api")]
    [InlineData("missing_id")]
    [InlineData("untrusted_clock")]
    public void 一枚を特定できない指定だけはAの全区間回収を維持する(string variation)
    {
        using var state = new Fixture(3 * FrameBytes);
        var frame = state.Frame(10);
        var before = state.StoreFrames("before", new[] { frame }, trusted: variation != "untrusted_clock");
        var current = state.Store("after", new[] { 0d, 1d });
        state.Cache.PinCurrent(current.SegmentId, 0);
        if (variation == "old_api") state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId);
        else state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, variation == "missing_id" ? long.MaxValue : frame.FrameId, -1);
        state.ReleaseProducers();
        Assert.True(state.Cache.Append(current.SegmentId, state.Frame(2)));
        Assert.Null(state.Cache.GetSegment(before.SegmentId));
        Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
        Assert.Null(Hint(state.Cache, "_displayedBeforeSegmentId"));
        Assert.Equal(0L, Hint(state.Cache, "_displayedBeforeFrameId"));
        Assert.Equal(0, Hint(state.Cache, "_displayedBeforeDirection"));
    }

    [Fact]
    public void 通常回収とcurrentのprefixを先に使いBeforeはまだ縮めない()
    {
        using var state = new Fixture(7 * FrameBytes);
        var before = state.Store("before", new[] { 10d, 11d });
        var ordinary = state.Store("ordinary", new[] { 20d });
        var current = state.Store("after", new[] { 0d, 1d, 2d, 3d });
        state.Cache.PinCurrent(current.SegmentId, 2);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[1].FrameId, -1);
        state.ReleaseProducers();
        Assert.True(state.Cache.CanAppend(current.SegmentId, 2 * FrameBytes));
        Assert.Null(state.Cache.GetSegment(ordinary.SegmentId));
        Assert.Equal(before.Frames, state.Cache.GetSegment(before.SegmentId)!.Frames);
        Assert.Equal(current.Frames.Skip(1), state.Cache.GetSegment(current.SegmentId)!.Frames);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("current")]
    [InlineData("appending")]
    public void 既存の取得中現在区間追加先の保護を部分回収でも破らない(string variation)
    {
        using var state = new Fixture(4 * FrameBytes);
        var beforeFrames = new[] { state.Frame(10), state.Frame(11) };
        long beforeId;
        if (variation == "active")
        {
            beforeId = state.Cache.BeginSegment(Fixture.Request("before"), new(0, 0, true), 10);
            foreach (var frame in beforeFrames) Assert.True(state.Cache.Append(beforeId, frame));
        }
        else beforeId = state.StoreFrames("before", beforeFrames).SegmentId;
        var current = state.Store("after", new[] { 0d, 1d });
        state.Cache.PinCurrent(variation == "current" ? beforeId : current.SegmentId, variation == "current" ? 10 : 0);
        state.Cache.ProtectDisplayedBeforeSegment(beforeId, beforeFrames[0].FrameId, -1);
        state.ReleaseProducers();
        if (variation == "current")
        {
            // 追加先とcurrentを別々に保護し、通常候補の回収で成功する条件を避ける。
            Assert.False(state.Cache.CanAppend(current.SegmentId, FrameBytes));
        }
        else Assert.False(state.Cache.CanAppend(variation == "appending" ? beforeId : current.SegmentId, FrameBytes));
        Assert.Equal(beforeFrames, state.Cache.GetSegment(beforeId)!.Frames);
        Assert.Equal(current.Frames, state.Cache.GetSegment(current.SegmentId)!.Frames);
    }

    [Fact]
    public void currentのdirect接続先はBefore指定があっても削らない()
    {
        using var state = new Fixture(6 * FrameBytes);
        var before = state.Store("same", new[] { 0d, 1d, 2d });
        var current = state.Store("same", new[] { 1d, 2d, 3d });
        state.Cache.PinCurrent(current.SegmentId, 1);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[1].FrameId, -1);
        Assert.True(state.Cache.AreDirectlyConnected(before.SegmentId, current.SegmentId));
        state.ReleaseProducers();
        Assert.False(state.Cache.CanAppend(current.SegmentId, FrameBytes));
        Assert.Equal(before.Frames, state.Cache.GetSegment(before.SegmentId)!.Frames);
        Assert.Equal(current.Frames, state.Cache.GetSegment(current.SegmentId)!.Frames);
    }

    [Fact]
    public void 逆先読みの経路bridgeをBeforeの部分回収へ流用しない()
    {
        using var state = new Fixture(11 * FrameBytes);
        var current = state.Store("same", new[] { 0d, 1d, 2d });
        var bridge = state.Store("same", new[] { 1d, 2d, 3d, 4d });
        var before = state.Store("same", new[] { 3d, 4d, 5d, 6d });
        state.Cache.PinCurrent(current.SegmentId, 1);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[2].FrameId, -1);
        state.ReleaseProducers();
        var request = Fixture.Request("same") with { TargetSeconds = 5 };
        Assert.Equal(0, state.Cache.ReverseWindowRoom(request, 5, 5, 1));
        Assert.False(state.Cache.TryTrimReverseWindow(request, 5, 5, 1, FrameBytes));
        Assert.Equal(current.Frames, state.Cache.GetSegment(current.SegmentId)!.Frames);
        Assert.Equal(bridge.Frames, state.Cache.GetSegment(bridge.SegmentId)!.Frames);
        Assert.Equal(before.Frames, state.Cache.GetSegment(before.SegmentId)!.Frames);
    }

    [Fact]
    public void 逆窓の見積りは表示一枚分を差し引き不足時は内容を変えない()
    {
        const int CapacityFrames = 86;
        const int WindowFrames = 43;
        const int MaximumRoomFrames = 77;
        using var state = new Fixture(CapacityFrames * FrameBytes);
        var before = state.Store("before", Enumerable.Range(0, WindowFrames).Select(index => 20 + index / 60d).ToArray());
        var current = state.Store("after", Enumerable.Range(0, WindowFrames).Select(index => 10 + index / 60d).ToArray());
        var request = Fixture.Request("after") with { TargetSeconds = 10.1 };
        var anchor = before.Frames[12];
        state.Cache.PinCurrent(current.SegmentId, 10.1);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, anchor.FrameId, -1);
        state.ReleaseProducers();
        Assert.Equal(MaximumRoomFrames * FrameBytes, state.Cache.ReverseWindowRoom(request, 10, 10.1, 4d / 60));
        Assert.False(state.Cache.TryTrimReverseWindow(request, 10, 10.1, 4d / 60, (MaximumRoomFrames + 1) * FrameBytes));
        Assert.Equal(before.Frames, state.Cache.GetSegment(before.SegmentId)!.Frames);
        Assert.Equal(current.Frames, state.Cache.GetSegment(current.SegmentId)!.Frames);
        Assert.True(state.Cache.TryTrimReverseWindow(request, 10, 10.1, 4d / 60, MaximumRoomFrames * FrameBytes));
        Assert.Same(anchor, Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames));
        Assert.Equal(8, state.Cache.GetSegment(current.SegmentId)!.Frames.Count);
        Assert.Equal(MaximumRoomFrames * FrameBytes, state.Cache.CapacityBytes - state.Cache.SizeBytes);
        Assert.True(state.Cache.TryGetAt(request, 10.1, out _));
    }

    [Fact]
    public void 共通safe点が二点残る辺は維持し一点になった辺だけ解除する()
    {
        using var state = new Fixture(16 * FrameBytes);
        var before = state.Store("same", new[] { 0d, 1d, 2d, 3d });
        long peer = state.Cache.BeginSegment(Fixture.Request("same"), new(0, 0, true), 0);
        foreach (double time in new[] { 0d, 1d, 2d, 3d }) Assert.True(state.Cache.Append(peer, state.Frame(time)));
        var current = state.Store("after", Enumerable.Range(10, 8).Select(value => (double)value).ToArray());
        state.Cache.PinCurrent(current.SegmentId, 10);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[1].FrameId, -1);
        Assert.True(state.Cache.AreDirectlyConnected(before.SegmentId, peer));
        state.ReleaseProducers();
        long retains = state.Scope.Capture().TotalRetains;
        Assert.True(state.Cache.CanAppend(current.SegmentId, FrameBytes));
        Assert.Equal(2, state.Cache.GetSegment(before.SegmentId)!.Frames.Count);
        Assert.True(state.Cache.AreDirectlyConnected(before.SegmentId, peer));
        Assert.True(state.Cache.CanAppend(current.SegmentId, 3 * FrameBytes));
        Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames);
        Assert.False(state.Cache.AreDirectlyConnected(before.SegmentId, peer));
        Assert.True(state.Cache.CanStitch(Fixture.Request("same")));
        Assert.True(state.Cache.GetSegment(before.SegmentId)!.CanStitch);
        Assert.True(state.Cache.GetSegment(peer)!.CanStitch);
        Assert.Equal(retains, state.Scope.Capture().TotalRetains);
    }

    [Fact]
    public void 古metadataを残しても削除rawは復活せず表示一枚の別所有だけ生存する()
    {
        using var state = new Fixture(16 * FrameBytes);
        var before = state.Store("before", Enumerable.Range(10, 8).Select(value => (double)value).ToArray());
        var current = state.Store("after", Enumerable.Range(0, 8).Select(value => (double)value).ToArray());
        var anchor = before.Frames[4];
        using var displayed = RawFrameLease.Capture(anchor, "TestDisplayed");
        state.Cache.PinCurrent(current.SegmentId, 0);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, anchor.FrameId, -1);
        state.ReleaseProducers();
        long retains = state.Scope.Capture().TotalRetains;
        Assert.True(state.Cache.CanAppend(current.SegmentId, 7 * FrameBytes));
        Assert.Equal(retains, state.Scope.Capture().TotalRetains);
        foreach (var frame in before.Frames)
            if (!ReferenceEquals(frame, anchor)) Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
        Assert.Equal(8, before.Frames.Count);
        state.Cache.Clear();
        Assert.Equal(1, state.Scope.Capture().LiveOwners);
        Assert.Equal(1, state.Scope.Capture().LiveTokens);
        Assert.Equal(FrameBytes, anchor.RawPixels.Length);
        Assert.False(RawFrameLease.TryCapture(before.Frames[0], "TestStale", out var unavailable));
        Assert.Null(unavailable);
        displayed.Dispose();
        Assert.True(state.Scope.Capture().IsEmpty);
        Assert.Equal(0, state.Cache.PendingRawReleaseCount);
    }

    [Fact]
    public void pendingの検索抑止はBefore縮小で解除しない()
    {
        using var state = new Fixture(4 * FrameBytes);
        var before = state.Store("before", new[] { 10d, 11d });
        var current = state.Store("after", new[] { 0d, 1d });
        state.Cache.PinCurrent(current.SegmentId, 0);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[1].FrameId, -1);
        long pending = state.Cache.BeginPendingSource(Fixture.Request("before"));
        state.ReleaseProducers();
        Assert.True(state.Cache.CanAppend(current.SegmentId, FrameBytes));
        Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames);
        Assert.False(state.Cache.TryGetAt(Fixture.Request("before"), 11, out _));
        Assert.Null(state.Cache.GetConnectedSafeRange(Fixture.Request("before") with { TargetSeconds = 11 }));
        state.Cache.EndPendingSource(pending);
        Assert.True(state.Cache.TryGetAt(Fixture.Request("before"), 11, out _));
    }

    [Fact]
    public void 明示解除消失IDとRemoveとClearは全ヒントを消す()
    {
        using var state = new Fixture(4 * FrameBytes);
        var before = state.Store("before", new[] { 10d });
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[0].FrameId, -7);
        Assert.Equal(-1, Hint(state.Cache, "_displayedBeforeDirection"));
        state.Cache.ProtectDisplayedBeforeSegment(long.MaxValue, before.Frames[0].FrameId, 1);
        AssertHintsCleared(state.Cache);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[0].FrameId, 1);
        state.Cache.RemoveSegment(before.SegmentId);
        AssertHintsCleared(state.Cache);
        var next = state.Store("next", new[] { 20d });
        state.Cache.ProtectDisplayedBeforeSegment(next.SegmentId, next.Frames[0].FrameId, 1);
        state.Cache.ProtectDisplayedBeforeSegment(null);
        AssertHintsCleared(state.Cache);
        state.Cache.ProtectDisplayedBeforeSegment(next.SegmentId, next.Frames[0].FrameId, 1);
        state.Cache.Clear();
        AssertHintsCleared(state.Cache);
    }

    [Fact]
    public void 末尾を残す方向窓では既知EOFを解除せず先頭番号だけを進める()
    {
        using var state = new Fixture(64 * FrameBytes);
        var before = state.Store("before", Enumerable.Range(100, 21).Select(value => (double)value).ToArray(), reachedEnd: true);
        var current = state.Store("after", Enumerable.Range(0, 43).Select(value => (double)value).ToArray());
        state.Cache.PinCurrent(current.SegmentId, 0);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[^1].FrameId, 1);
        state.ReleaseProducers();
        Assert.True(state.Cache.CanAppend(current.SegmentId, FrameBytes));
        var retained = state.Cache.GetSegment(before.SegmentId)!;
        Assert.Same(before.Frames[^1], Assert.Single(retained.Frames));
        Assert.True(retained.ReachedInputEnd);
        Assert.Equal(20, retained.Clock.FrameIndexOffset);
        Assert.Equal(120, retained.SafeStartSeconds);
        Assert.Equal(before.SafeEndSeconds, retained.SafeEndSeconds);
    }

    [Fact]
    public void 保存候補を照合した後にBeforeを縮めても未登録候補の不足辺を残さない()
    {
        using var state = new Fixture(10 * FrameBytes);
        var before = state.Store("same", new[] { 0d, 1d, 2d, 3d });
        var current = state.Store("after", Enumerable.Range(10, 6).Select(value => (double)value).ToArray());
        state.Cache.PinCurrent(current.SegmentId, 10);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[1].FrameId, -1);
        state.ReleaseProducers();
        // 新候補は先に3点の画素照合を済ませ、保存容量確保の段階で旧Beforeが一枚へ縮む。
        var incoming = state.Store("same", new[] { 0d, 1d, 2d });
        Assert.NotNull(incoming);
        Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames);
        Assert.False(state.Cache.AreDirectlyConnected(before.SegmentId, incoming.SegmentId));
        Assert.True(state.Cache.CanStitch(Fixture.Request("same")));
        Assert.True(incoming.CanStitch);
        Assert.Equal(10 * FrameBytes, state.Cache.SizeBytes);
    }

    [Fact]
    public void legacy保存の最後の手段も有効な表示一枚は全回収しない()
    {
        using var state = new Fixture(8 * FrameBytes);
        var before = state.Store("before", new[] { 10d, 11d, 12d, 13d });
        var current = state.Store("after", new[] { 0d, 1d, 2d, 3d });
        state.Cache.PinCurrent(current.SegmentId, 0);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, before.Frames[1].FrameId, -1);
        state.ReleaseProducers();
        state.Cache.Store(Fixture.Request("legacy"), new[] { state.Frame(30), state.Frame(31), state.Frame(32) });
        Assert.True(state.Cache.TryGet(Fixture.Request("legacy"), out var saved));
        Assert.Equal(3, saved.Count);
        Assert.Same(before.Frames[1], Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames));
        Assert.Equal(current.Frames, state.Cache.GetSegment(current.SegmentId)!.Frames);
        Assert.Equal(8 * FrameBytes, state.Cache.SizeBytes);
    }

    [Theory]
    [InlineData("trimmed_same")]
    [InlineData("trimmed_different")]
    [InlineData("trimmed_zero_matches")]
    [InlineData("natural_one_match")]
    public void 意図的縮小後の不足点は未接続に留めるが実際の画素差と自然な不足は拒否する(string variation)
    {
        const int AnchorIndex = 4;
        using var state = new Fixture(16 * FrameBytes);
        bool trimmed = variation != "natural_one_match";
        var before = state.Store("same", trimmed ? Enumerable.Range(10, 8).Select(value => (double)value).ToArray() : new[] { 14d });
        var anchor = before.Frames[trimmed ? AnchorIndex : 0];
        var current = state.Store("after", Enumerable.Range(0, 8).Select(value => (double)value).ToArray());
        state.Cache.PinCurrent(current.SegmentId, 0);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, anchor.FrameId, -1);
        state.ReleaseProducers();
        if (trimmed) Assert.True(state.Cache.CanAppend(current.SegmentId, 7 * FrameBytes));
        Assert.Same(anchor, Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames));
        // 一点の照合でも実pixelが違えば拒否する。0点だけは比較できないまま別区間として残す。
        var middle = state.Frame(variation == "trimmed_zero_matches" ? 14.01 : 14);
        if (variation == "trimmed_different") middle.RawPixels[0] ^= 1;
        var incoming = state.StoreFrames("same", new[] { state.Frame(13), middle, state.Frame(15) });
        bool accepted = variation is "trimmed_same" or "trimmed_zero_matches";
        Assert.Equal(accepted, state.Cache.CanStitch(Fixture.Request("same")));
        Assert.Equal(accepted, incoming.CanStitch);
        Assert.False(state.Cache.AreDirectlyConnected(before.SegmentId, incoming.SegmentId));
        if (accepted) Assert.Same(anchor, Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames));
        else Assert.Null(state.Cache.GetSegment(before.SegmentId));
    }

    [Fact]
    public void 反転時は残存窓だけを選び直して捨てた未来を復活させない()
    {
        using var state = new Fixture(64 * FrameBytes);
        var before = state.Store("before", Enumerable.Range(100, 21).Select(value => (double)value).ToArray());
        var current = state.Store("after", Enumerable.Range(0, 43).Select(value => (double)value).ToArray());
        var anchor = before.Frames[10];
        state.Cache.PinCurrent(current.SegmentId, 0);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, anchor.FrameId, -1);
        state.ReleaseProducers();
        Assert.True(state.Cache.CanAppend(current.SegmentId, FrameBytes));
        Assert.Equal(8, state.Cache.GetSegment(before.SegmentId)!.Frames.Count);
        state.Cache.ProtectDisplayedBeforeSegment(before.SegmentId, anchor.FrameId, 1);
        Assert.True(state.Cache.CanAppend(current.SegmentId, 14 * FrameBytes));
        Assert.Same(anchor, Assert.Single(state.Cache.GetSegment(before.SegmentId)!.Frames));
        Assert.Throws<ObjectDisposedException>(() => before.Frames[11].RawPixels);
        Assert.False(state.Cache.TryGetAt(Fixture.Request("before"), 111, out _));
        Assert.True(state.Cache.TryGetAt(Fixture.Request("before"), 110, out _));
    }

    private static object? Hint(FrameCache cache, string name)
        => typeof(FrameCache).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache);
    private static void AssertHintsCleared(FrameCache cache)
    {
        Assert.Null(Hint(cache, "_displayedBeforeSegmentId"));
        Assert.Equal(0L, Hint(cache, "_displayedBeforeFrameId"));
        Assert.Equal(0, Hint(cache, "_displayedBeforeDirection"));
    }

    private sealed class Fixture(long capacity) : IDisposable
    {
        private readonly List<RawFrameLease> _producers = new();
        internal RawOwnershipScope Scope { get; } = new();
        private FrameCache? _cache;
        internal FrameCache Cache => _cache ??= new(Scope, capacity);
        internal static PreviewRequest Request(string source) => new()
        { EncoderPath = "encoder", InputFile = source, Fps = 60, TargetSeconds = 1, TimestampOriginSeconds = 0, Capabilities = new(true) };
        internal Y4mFrame Frame(double time, int bytes = FrameBytes)
        {
            var data = Enumerable.Repeat((byte)((int)Math.Round(time * 60) % 251), bytes).ToArray();
            var owner = Scope.Create(Header, time, 1d / 60, data, Scope.NewRunId(), "TestReader");
            _producers.Add(owner);
            return owner.Frame;
        }
        internal PreviewSegmentSnapshot Store(string source, double[] seconds, bool reachedEnd = false)
            => StoreFrames(source, seconds.Select(time => Frame(time)).ToArray(), reachedEnd: reachedEnd);
        internal PreviewSegmentSnapshot StoreFrames(string source, IReadOnlyList<Y4mFrame> frames, bool trusted = true, bool reachedEnd = false)
            => Cache.StoreSegment(Request(source), frames, new(0, 0, trusted), frames[0].Xts!.Value,
                frames[^1].Xts!.Value + 1d / 60, reachedEnd)!;
        internal void ReleaseProducers()
        {
            foreach (var producer in _producers) producer.Dispose();
            _producers.Clear();
        }
        public void Dispose()
        {
            Cache.Clear();
            ReleaseProducers();
            Assert.True(Scope.Capture().IsEmpty);
            Assert.Equal(Scope.Capture().TotalRetains, Scope.Capture().TotalTokenReleases);
        }
    }
}

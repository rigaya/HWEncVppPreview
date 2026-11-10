using System.Reflection;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class DisplayedBeforeProtectionTests
{
    private const int FrameBytes = 6;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 60, FpsDen = 1 };
    private static PreviewRequest Request(string source = "after") => new()
    {
        EncoderPath = "encoder", InputFile = source, Fps = 60, TargetSeconds = 1,
        TimestampOriginSeconds = 0, Capabilities = new(true),
    };
    private static Y4mFrame Frame(double second) => new(Header, second, 1d / 60, new byte[FrameBytes]);
    private static PreviewSegmentSnapshot Store(FrameCache cache, string source, params double[] seconds)
        => Store(cache, Request(source), seconds.Select(Frame).ToArray());
    private static PreviewSegmentSnapshot Store(FrameCache cache, PreviewRequest request, params Y4mFrame[] frames)
        => cache.StoreSegment(request, frames, new(0, 0, true), frames[0].Xts!.Value, frames[^1].Xts!.Value + 1d / 60)!;
    private static object? Protection(FrameCache cache) => typeof(FrameCache)
        .GetField("_displayedBeforeSegmentId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache);

    [Fact]
    public void 通常候補を先に捨て次にcurrentの古いprefixを削ってBeforeを守る()
    {
        var cache = new FrameCache(7 * FrameBytes);
        var before = Store(cache, "before", 10, 11);
        var ordinary = Store(cache, "other", 20);
        var current = Store(cache, "after", 0, 1, 2, 3);
        cache.PinCurrent(current.SegmentId, 2);
        cache.ProtectDisplayedBeforeSegment(before.SegmentId);
        Assert.True(cache.CanAppend(current.SegmentId, 2 * FrameBytes));
        Assert.Null(cache.GetSegment(ordinary.SegmentId));
        Assert.Equal(before.Frames, cache.GetSegment(before.SegmentId)!.Frames);
        Assert.Equal(current.Frames.Skip(1), cache.GetSegment(current.SegmentId)!.Frames);
        // 選択画像と未来は削れないため、さらに大きい要求でだけ元画像の区間全体を最後に回収する。
        Assert.True(cache.CanAppend(current.SegmentId, 5 * FrameBytes));
        Assert.Null(cache.GetSegment(before.SegmentId));
        Assert.Null(Protection(cache));
        Assert.Equal(current.Frames.Skip(2), cache.GetSegment(current.SegmentId)!.Frames);
    }

    [Fact]
    public void currentの選択画像と未来tailを残してBeforeを最後に回収する()
    {
        var cache = new FrameCache(3 * FrameBytes);
        var before = Store(cache, "before", 10);
        var current = Store(cache, "after", 0, 1);
        cache.PinCurrent(current.SegmentId, 0);
        cache.ProtectDisplayedBeforeSegment(before.SegmentId);
        var next = Frame(2);
        Assert.True(cache.Append(current.SegmentId, next));
        Assert.Null(cache.GetSegment(before.SegmentId));
        var retained = cache.GetSegment(current.SegmentId)!.Frames;
        Assert.Equal(3, retained.Count);
        Assert.Same(current.Frames[0], retained[0]);
        Assert.Same(current.Frames[1], retained[1]);
        Assert.Same(next, retained[2]);
        Assert.Equal(3 * FrameBytes, cache.SizeBytes);
    }

    [Fact]
    public void 保護するBeforeを交換すると旧Beforeだけが通常回収へ戻る()
    {
        var cache = new FrameCache(3 * FrameBytes);
        var first = Store(cache, "before1", 10);
        var second = Store(cache, "before2", 20);
        var current = Store(cache, "after", 0);
        cache.PinCurrent(current.SegmentId, 0);
        cache.ProtectDisplayedBeforeSegment(first.SegmentId);
        cache.ProtectDisplayedBeforeSegment(second.SegmentId);
        Assert.True(cache.Append(current.SegmentId, Frame(1)));
        Assert.Null(cache.GetSegment(first.SegmentId));
        Assert.NotNull(cache.GetSegment(second.SegmentId));
        cache.ProtectDisplayedBeforeSegment(null);
        Assert.Null(Protection(cache));
    }

    [Fact]
    public void 消失IDとRemoveとClearはBefore保護枠を残さない()
    {
        var cache = new FrameCache(4 * FrameBytes);
        var before = Store(cache, "before", 10);
        cache.ProtectDisplayedBeforeSegment(before.SegmentId);
        cache.ProtectDisplayedBeforeSegment(long.MaxValue);
        Assert.Null(Protection(cache));
        cache.ProtectDisplayedBeforeSegment(before.SegmentId);
        cache.RemoveSegment(before.SegmentId);
        Assert.Null(Protection(cache));
        var next = Store(cache, "before", 20);
        cache.ProtectDisplayedBeforeSegment(next.SegmentId);
        cache.Clear();
        Assert.Null(Protection(cache));
        Assert.Equal(0, cache.SizeBytes);
    }

    [Fact]
    public void Beforeとcurrentが同じ区間なら既存prefix回収の結果を変えない()
    {
        foreach (bool protect in new[] { false, true })
        {
            var cache = new FrameCache(2 * FrameBytes);
            var current = Store(cache, "after", 0, 1);
            cache.PinCurrent(current.SegmentId, 0);
            if (protect) cache.ProtectDisplayedBeforeSegment(current.SegmentId);
            Assert.False(cache.Append(current.SegmentId, Frame(2)));
            Assert.Equal(current.Frames, cache.GetSegment(current.SegmentId)!.Frames);
            cache.PinCurrent(current.SegmentId, 1);
            Assert.True(cache.Append(current.SegmentId, Frame(2)));
            Assert.Equal(new[] { 1d, 2d }, cache.GetSegment(current.SegmentId)!.Frames.Select(frame => frame.Xts!.Value));
        }
    }

    [Fact]
    public void activeのBeforeは最後の手段でも捨てず完了後だけ回収できる()
    {
        var cache = new FrameCache(2 * FrameBytes);
        long before = cache.BeginSegment(Request("before"), new(0, 0, true), 10);
        Assert.True(cache.Append(before, Frame(10)));
        var current = Store(cache, "after", 0);
        cache.PinCurrent(current.SegmentId, 0);
        cache.ProtectDisplayedBeforeSegment(before);
        Assert.False(cache.Append(current.SegmentId, Frame(1)));
        Assert.NotNull(cache.GetSegment(before));
        cache.CompleteSegment(before, false);
        Assert.True(cache.Append(current.SegmentId, Frame(1)));
        Assert.Null(cache.GetSegment(before));
    }

    [Fact]
    public void 照合済みdirect接続のBeforeは保護が重なっても最後に捨てない()
    {
        var cache = new FrameCache(6 * FrameBytes);
        var peer = Store(cache, "after", 0, 1, 2);
        var current = Store(cache, "after", 1, 2, 3);
        Assert.True(cache.AreDirectlyConnected(current.SegmentId, peer.SegmentId));
        cache.PinCurrent(current.SegmentId, 1);
        cache.ProtectDisplayedBeforeSegment(peer.SegmentId);
        Assert.False(cache.Append(current.SegmentId, Frame(4)));
        Assert.Equal(peer.Frames, cache.GetSegment(peer.SegmentId)!.Frames);
        Assert.Equal(current.Frames, cache.GetSegment(current.SegmentId)!.Frames);
        Assert.Equal(FrameBytes, cache.ReverseWindowRoom(Request(), 1, 1, 1));
        Assert.False(cache.TryTrimReverseWindow(Request(), 1, 1, 1, 2 * FrameBytes));
        Assert.Equal(current.Frames, cache.GetSegment(current.SegmentId)!.Frames);
    }

    [Fact]
    public void 追加先そのもののBeforeと容量より大きい要求は既存失敗を維持する()
    {
        var cache = new FrameCache(2 * FrameBytes);
        var before = Store(cache, "before", 10);
        var current = Store(cache, "after", 0);
        cache.PinCurrent(current.SegmentId, 0);
        cache.ProtectDisplayedBeforeSegment(before.SegmentId);
        Assert.False(cache.Append(before.SegmentId, Frame(11)));
        Assert.False(cache.CanAppend(current.SegmentId, 3 * FrameBytes));
        Assert.NotNull(cache.GetSegment(before.SegmentId));
        Assert.Equal(2 * FrameBytes, cache.SizeBytes);
    }

    [Fact]
    public void legacy保存も通常候補を先に回収し従来どおりcurrentprefixを削らない()
    {
        var cache = new FrameCache(4 * FrameBytes);
        var before = Store(cache, "before", 10);
        var current = Store(cache, "after", 0, 1);
        cache.Store(Request("legacy1"), new[] { Frame(20) });
        cache.PinCurrent(current.SegmentId, 1);
        cache.ProtectDisplayedBeforeSegment(before.SegmentId);
        cache.Store(Request("legacy2"), new[] { Frame(30), Frame(31) });
        Assert.False(cache.TryGet(Request("legacy1"), out _));
        Assert.True(cache.TryGet(Request("legacy2"), out var saved));
        Assert.Equal(2, saved.Count);
        Assert.Null(cache.GetSegment(before.SegmentId));
        Assert.Equal(current.Frames, cache.GetSegment(current.SegmentId)!.Frames);
    }

    [Fact]
    public void 逆窓の見積りと回収はbridgeを先に縮めBeforeを最後にする()
    {
        const int FrameCapacity = 86;
        const int WindowFrames = 43;
        var cache = new FrameCache(FrameCapacity * FrameBytes);
        var request = Request() with { TargetSeconds = 10.1 };
        var before = Store(cache, "before", Enumerable.Range(0, WindowFrames).Select(index => 20 + index / 60d).ToArray());
        var current = Store(cache, request, Enumerable.Range(0, WindowFrames).Select(index => Frame(10 + index / 60d)).ToArray());
        cache.PinCurrent(current.SegmentId, 10.1);
        cache.ProtectDisplayedBeforeSegment(before.SegmentId);
        Assert.Equal(78 * FrameBytes, cache.ReverseWindowRoom(request, 10, 10.1, 4d / 60));
        Assert.True(cache.TryTrimReverseWindow(request, 10, 10.1, 4d / 60, 20 * FrameBytes));
        Assert.NotNull(cache.GetSegment(before.SegmentId));
        Assert.Equal(8, cache.GetSegment(current.SegmentId)!.Frames.Count);
        // 接続に必要な保持範囲は既に最小なので、見積りに含めた元画像区間を最後に返して大きな逆方向窓を受け入れる。
        Assert.True(cache.TryTrimReverseWindow(request, 10, 10.1, 4d / 60, 60 * FrameBytes));
        Assert.Null(cache.GetSegment(before.SegmentId));
        Assert.Equal(8, cache.GetSegment(current.SegmentId)!.Frames.Count);
        Assert.True(cache.TryGetAt(request, 10.1, out _));
    }

    [Fact]
    public void 保護枠はraw所有を追加せず回収とClearで全て返す()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 3 * FrameBytes);
        using var beforeOwner = scope.Create(Header, 10, 1d / 60, new byte[FrameBytes], scope.NewRunId(), "TestReader");
        using var afterOwner0 = scope.Create(Header, 0, 1d / 60, new byte[FrameBytes], scope.NewRunId(), "TestReader");
        using var afterOwner1 = scope.Create(Header, 1, 1d / 60, new byte[FrameBytes], scope.NewRunId(), "TestReader");
        var before = Store(cache, Request("before"), beforeOwner.Frame);
        var current = Store(cache, Request(), afterOwner0.Frame, afterOwner1.Frame);
        beforeOwner.Dispose(); afterOwner0.Dispose(); afterOwner1.Dispose();
        try
        {
            cache.PinCurrent(current.SegmentId, 0);
            var initial = scope.Capture();
            cache.ProtectDisplayedBeforeSegment(before.SegmentId);
            Assert.Equal(initial.TotalRetains, scope.Capture().TotalRetains);
            Assert.Equal(initial.LiveTokens, scope.Capture().LiveTokens);
            Assert.True(cache.CanAppend(current.SegmentId, FrameBytes));
            Assert.Throws<ObjectDisposedException>(() => before.Frames[0].RawPixels);
        }
        finally { cache.Clear(); }
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().TotalRetains, scope.Capture().TotalTokenReleases);
    }
}

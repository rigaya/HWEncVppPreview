using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public class FrameCacheTests
{
    private static PreviewRequest Request(string input = "in.ts") => new()
    {
        EncoderPath = "encoder",
        InputFile = input,
        Fps = 30,
        TargetSeconds = 5,
        InputArgs = new[] { "--avhw", "--input-analyze", "2" },
        VppArgs = new[] { "--vpp-nlmeans", "sigma=0.005,h=0.05" },
    };

    private static Y4mFrame Frame(int bytes = 6) => new(
        Y4mHeader.Parse("YUV4MPEG2 W2 H2 F30:1 C420"), 0, 1.0 / 30, new byte[bytes]);

    [Fact]
    public void ClearDropsFramesAndResetsMemoryAccounting()
    {
        var cache = new FrameCache();
        var request = new PreviewRequest { EncoderPath = "encoder", InputFile = "input", Fps = 30 };
        cache.Store(request, new[] { new Y4mFrame(new Y4mHeader { Width = 2, Height = 2 }, 0, 1.0 / 30, new byte[6]) });
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.SizeBytes);
        Assert.False(cache.TryGet(request, out _));
    }

    [Fact]
    public void 同じ値の引数配列なら同じ区間を取得する()
    {
        var cache = new FrameCache();
        var frame = Frame();
        cache.Store(Request(), new[] { frame });
        Assert.True(cache.TryGet(Request(), out var frames));
        Assert.Same(frame, Assert.Single(frames));
        Assert.Equal(6, cache.SizeBytes);
    }

    [Fact]
    public void 全キー要素と引数の区切りを区別する()
    {
        var cache = new FrameCache();
        var request = Request();
        cache.Store(request, new[] { Frame() });
        var variants = new[]
        {
            request with { EncoderPath = "other" },
            request with { InputFile = "other.ts" },
            request with { InputArgs = new[] { "--avsw" } },
            request with { VppArgs = new[] { "--vpp-nlmeans sigma=0.005,h=0.05" } },
            request with { TargetSeconds = 6 },
            request with { PostMarginSeconds = 1 },
        };
        foreach (var variant in variants) Assert.False(cache.TryGet(variant, out _));
    }

    [Fact]
    public void 計画のseekと枚数が同じなら目標時刻が違っても再利用する()
    {
        var cache = new FrameCache();
        var first = Request() with { TargetSeconds = 0.1, PreMarginSeconds = 2 };
        var second = first with { TargetSeconds = 0.099 };
        Assert.Equal(CommandBuilder.Plan(first).FrameCount, CommandBuilder.Plan(second).FrameCount);
        cache.Store(first, new[] { Frame() });
        Assert.True(cache.TryGet(second, out _));
    }

    [Fact]
    public void 保存後に元の引数とリストを変更してもキーと区間は維持する()
    {
        var cache = new FrameCache();
        var args = new[] { "--avhw" };
        var vpp = new[] { "--vpp-nlmeans" };
        var request = Request() with { InputArgs = args, VppArgs = vpp };
        var lookup = request with { InputArgs = args.ToArray(), VppArgs = vpp.ToArray() };
        var frames = new List<Y4mFrame> { Frame() };
        cache.Store(request, frames);
        args[0] = "--avsw";
        vpp[0] = "--vpp-smooth";
        frames.Clear();
        Assert.True(cache.TryGet(lookup, out var result));
        Assert.Single(result);
        Assert.False(cache.TryGet(request, out _));
    }

    [Fact]
    public void 参照した区間を残し最も古い区間から捨てる()
    {
        var cache = new FrameCache(12);
        cache.Store(Request("a"), new[] { Frame() });
        cache.Store(Request("b"), new[] { Frame() });
        Assert.True(cache.TryGet(Request("a"), out _));
        cache.Store(Request("c"), new[] { Frame() });
        Assert.True(cache.TryGet(Request("a"), out _));
        Assert.False(cache.TryGet(Request("b"), out _));
        Assert.True(cache.TryGet(Request("c"), out _));
        Assert.Equal(2, cache.Count);
        Assert.Equal(12, cache.SizeBytes);
    }

    [Fact]
    public void 置き換え時に古い容量を減算する()
    {
        var cache = new FrameCache(12);
        cache.Store(Request(), new[] { Frame(), Frame() });
        cache.Store(Request(), new[] { Frame() });
        Assert.Equal(1, cache.Count);
        Assert.Equal(6, cache.SizeBytes);
        cache.Store(Request("other"), new[] { Frame() });
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void 上限を超える一区間は無関係な区間を追い出さない()
    {
        var cache = new FrameCache(6);
        cache.Store(Request("a"), new[] { Frame() });
        cache.Store(Request("b"), new[] { Frame(), Frame() });
        Assert.True(cache.TryGet(Request("a"), out _));
        Assert.False(cache.TryGet(Request("b"), out _));
    }

    [Fact]
    public void 容量ゼロは保存しない()
    {
        var cache = new FrameCache(0);
        cache.Store(Request(), new[] { Frame() });
        cache.Store(Request("empty"), Array.Empty<Y4mFrame>());
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.SizeBytes);
    }

    [Fact]
    public void 負の容量は拒否する()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameCache(-1));
    }
}

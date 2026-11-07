using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.Tests;

public sealed class BgraBufferPoolTests
{
    private static Y4mHeader Header(int width = 2, int depth = 8) => new() { Width = width, Height = 2, BitDepth = depth };
    [Fact]
    public void 同じ形式は再利用し貸出中を重ねず二重Disposeしない()
    {
        using var pool = new BgraBufferPool();
        var first = pool.Rent(Header()); byte[] a = first.Buffer;
        var second = pool.Rent(Header()); byte[] b = second.Buffer;
        Assert.NotSame(a, b); Assert.Equal(2, pool.Capture().Leased);
        var extra = pool.Rent(Header());
        Assert.False(extra.IsReusable); Assert.Equal(2, pool.Capture().Buffers);
        extra.Dispose(); Assert.Throws<ObjectDisposedException>(() => extra.Buffer);
        first.Dispose(); first.Dispose();
        using var reused = pool.Rent(Header()); Assert.Same(a, reused.Buffer);
        second.Dispose();
        Assert.Equal(2, pool.Capture().Buffers); Assert.Equal(1, pool.Capture().Leased);
    }
    [Fact]
    public void 退役貸出もbyteと枠へ課金し返却時に破棄する()
    {
        using var pool = new BgraBufferPool(2, 48);
        var old = pool.Rent(Header()); byte[] bytes = old.Buffer;
        pool.Retire();
        using var current = pool.Rent(Header(4));
        Assert.Equal(48, pool.Capture().Bytes); Assert.Equal(2, pool.Capture().Leased);
        using var extra = pool.Rent(Header(4));
        Assert.False(extra.IsReusable); Assert.Equal(48, pool.Capture().Bytes);
        old.Dispose(); Assert.Equal(32, pool.Capture().Bytes);
        Assert.Equal(1, pool.Capture().Buffers);
        GC.KeepAlive(bytes);
    }
    [Fact]
    public void 入力形式変更とDisposeはidleを捨て旧貸出返却も残さない()
    {
        var pool = new BgraBufferPool();
        var old = pool.Rent(Header()); byte[] a = old.Buffer; old.Dispose();
        var current = pool.Rent(Header(depth: 10)); Assert.NotSame(a, current.Buffer);
        pool.Dispose(); pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pool.Rent(Header()));
        current.Dispose(); Assert.Equal(0, pool.Capture().Buffers); Assert.Equal(0, pool.Capture().Bytes);
        Assert.Throws<ObjectDisposedException>(() => current.Buffer);
    }
    [Fact]
    public void 入力変更前に予定したworkerは初回確保をしない()
    {
        using var pool = new BgraBufferPool();
        long epoch = pool.RetirementEpoch;
        pool.Retire();
        Assert.Throws<OperationCanceledException>(() => pool.Rent(Header(), epoch));
        Assert.Equal(0, pool.Capture().Buffers);
        using var current = pool.Rent(Header(), pool.RetirementEpoch);
        Assert.Equal(1, pool.Capture().Leased);
    }

    [Fact]
    public void 有効な大画像は小さな再利用予算を超えても一回限りで処理する()
    {
        using var pool = new BgraBufferPool(2, 16);
        var lease = pool.Rent(Header(4));
        Assert.Equal(32, lease.Buffer.Length); Assert.False(lease.IsReusable);
        Assert.Equal(0, pool.Capture().Buffers); Assert.Equal(0, pool.Capture().Bytes);
        lease.Dispose(); lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.Buffer);
        Assert.Equal(0, pool.Capture().Leased); Assert.Equal(0, pool.Capture().Idle);
        Assert.Equal(0, pool.Capture().Bytes);
        Assert.Throws<ArgumentException>(() => pool.Rent(new Y4mHeader { Width = int.MaxValue, Height = int.MaxValue }));
    }
}

using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class RawBufferPoolTests
{
    private const int FrameBytes = 6;
    private const int ParallelReaders = 16;
    private const byte Pixel = 0x2a;
    private const byte Poison = 0xa5;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
    private static readonly Y4mHeader OtherShape = new() { Width = 3, Height = 2, Chroma = Y4mChroma.Mono, FpsNum = 30, FpsDen = 1 };
    private static readonly Y4mHeader Larger = new() { Width = 4, Height = 2, FpsNum = 30, FpsDen = 1 };
    private static RawFrameLease Rent(RawOwnershipScope scope, Y4mHeader? header = null)
        => scope.RentReader(header ?? Header, 0, null, scope.NewRunId());
    private static void AssertZero(RawOwnershipScope scope)
    {
        Assert.Equal(0, scope.Capture().LiveOwners);
        Assert.Equal(0, scope.Capture().LiveTokens);
        Assert.Equal(0, scope.CapturePool().LoanedBytes);
    }

    [Fact]
    public void 同長は形式が異なっても最新の返却配列から借りる()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        var first = Rent(scope);
        var second = Rent(scope);
        byte[] older = first.Frame.RawPixels, newer = second.Frame.RawPixels;
        first.Dispose(); second.Dispose();
        Assert.Equal(Header.FrameBytes, OtherShape.FrameBytes);
        using var next = Rent(scope, OtherShape);
        Assert.Same(newer, next.Frame.RawPixels);
        Assert.Equal(OtherShape.Width, next.Frame.Header.Width);
        using var remaining = Rent(scope);
        Assert.Same(older, remaining.Frame.RawPixels);
        next.Dispose(); remaining.Dispose();
        AssertZero(scope);
    }

    [Fact]
    public void 異長配列は併存し上限で最古の返却から追い出す()
    {
        long capacity = Header.FrameBytes + Larger.FrameBytes;
        var scope = new RawOwnershipScope(new RawBufferPool(capacity, Larger.FrameBytes));
        var oldest = Rent(scope);
        var middle = Rent(scope, Larger);
        var newest = Rent(scope);
        byte[] dropped = oldest.Frame.RawPixels, large = middle.Frame.RawPixels, kept = newest.Frame.RawPixels;
        oldest.Dispose(); middle.Dispose();
        Assert.Equal(capacity, scope.CapturePool().FreeBytes);
        newest.Dispose();
        Assert.Equal(capacity, scope.CapturePool().FreeBytes);
        Assert.Equal(1, scope.CapturePool().DiscardedFreeBuffers);
        Assert.Equal(Header.FrameBytes, scope.CapturePool().DiscardedFreeBytes);
        using var largeAgain = Rent(scope, Larger);
        using var smallAgain = Rent(scope);
        Assert.Same(large, largeAgain.Frame.RawPixels);
        Assert.Same(kept, smallAgain.Frame.RawPixels);
        Assert.NotSame(dropped, smallAgain.Frame.RawPixels);
        largeAgain.Dispose(); smallAgain.Dispose();
        AssertZero(scope);
    }

    [Fact]
    public void 一枚上限と合計上限の境界を含み収まらない今回だけを捨てる()
    {
        var scope = new RawOwnershipScope(new RawBufferPool(FrameBytes, FrameBytes));
        using (var exact = Rent(scope)) { }
        Assert.Equal(FrameBytes, scope.CapturePool().FreeBytes);
        using (var oversized = Rent(scope, Larger)) { }
        Assert.Equal(1, scope.CapturePool().DropOversize);
        Assert.Equal(FrameBytes, scope.CapturePool().FreeBytes);
        var tooSmall = new RawOwnershipScope(new RawBufferPool(FrameBytes - 1, FrameBytes));
        using (var impossible = Rent(tooSmall)) { }
        Assert.Equal(1, tooSmall.CapturePool().DropCapacity);
        Assert.Equal(0, tooSmall.CapturePool().FreeBytes);
        AssertZero(scope); AssertZero(tooSmall);
    }

    [Fact]
    public void 入力変更のclearは貸出を壊さず遅い返却を再利用できる()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        scope.SetReturnedBufferPoisonForTesting(Poison);
        var oldInput = Rent(scope);
        byte[] oldBuffer = oldInput.Frame.RawPixels;
        Array.Fill(oldBuffer, Pixel);
        using (var free = Rent(scope)) { }
        scope.ClearFreeBuffers(); scope.ClearFreeBuffers();
        Assert.Equal(0, scope.CapturePool().FreeBytes);
        Assert.All(oldBuffer, value => Assert.Equal(Pixel, value));
        using var newInput = Rent(scope, OtherShape);
        Assert.NotSame(oldBuffer, newInput.Frame.RawPixels);
        oldInput.Dispose();
        Assert.All(oldBuffer, value => Assert.Equal(Poison, value));
        using var reused = Rent(scope, OtherShape);
        Assert.Same(oldBuffer, reused.Frame.RawPixels);
        reused.Dispose(); newInput.Dispose();
        AssertZero(scope);
    }

    [Fact]
    public void 外部提供配列は再利用へ取り込まず番兵も書かない()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        scope.SetReturnedBufferPoisonForTesting(Poison);
        var bytes = Enumerable.Repeat(Pixel, FrameBytes).ToArray();
        using (var external = scope.Create(Header, 0, null, bytes, scope.NewRunId(), "Reader")) { }
        Assert.All(bytes, value => Assert.Equal(Pixel, value));
        Assert.Equal(0, scope.CapturePool().Returned);
        Assert.Equal(0, scope.CapturePool().NewArrays);
        Assert.False(new RawOwnershipScope().CapturePool().Enabled);
        AssertZero(scope);
    }

    [Fact]
    public void 終了は未使用配列を捨て遅い返却まで所有を保つ()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        var borrowed = Rent(scope);
        Array.Fill(borrowed.Frame.RawPixels, Pixel);
        using (var other = Rent(scope)) { }
        scope.ClosePool(); scope.ClosePool();
        Assert.True(scope.CapturePool().Enabled);
        Assert.True(scope.CapturePool().Closed);
        Assert.Equal(0, scope.CapturePool().FreeBytes);
        Assert.All(borrowed.Frame.RawPixels, value => Assert.Equal(Pixel, value));
        borrowed.Dispose();
        Assert.Equal(1, scope.CapturePool().DropClosed);
        AssertZero(scope);
    }

    [Fact]
    public void 最後の所有だけが返却し再貸出では新しい識別子を作る()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        scope.SetReturnedBufferPoisonForTesting(Poison);
        var reader = Rent(scope);
        var frame = reader.Frame;
        var buffer = frame.RawPixels;
        Array.Fill(buffer, Pixel);
        string[] kinds = { "Cache", "Finite", "Notification", "VmAnchor", "VmBeforeDisplay", "FrameWork", "PublicExport" };
        var aliases = kinds.Select(kind => RawFrameLease.Capture(frame, kind)).ToArray();
        reader.Dispose();
        foreach (var alias in aliases.SkipLast(1)) alias.Dispose();
        Assert.Equal(0, scope.CapturePool().FreeBytes);
        Assert.All(aliases[^1].Frame.RawPixels, value => Assert.Equal(Pixel, value));
        using (var concurrent = Rent(scope)) Assert.NotSame(buffer, concurrent.Frame.RawPixels);
        aliases[^1].Dispose();
        Assert.All(buffer, value => Assert.Equal(Poison, value));
        using var next = Rent(scope);
        Assert.Same(buffer, next.Frame.RawPixels);
        Assert.NotEqual(frame.FrameId, next.Frame.FrameId);
        Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
        Assert.False(RawFrameLease.TryCapture(frame, "Old", out var resurrected));
        Assert.Null(resurrected);
        Assert.Equal(1, scope.CapturePool().Hits);
        next.Dispose();
        AssertZero(scope);
    }

    [Fact]
    public void 実照合の遅延解放と外部snapshotが残る間は番兵も再貸出も起こらない()
    {
        const int OverlapFrameCount = 2;
        const byte ChangedPixel = 0x44;
        const long CapacityBytes = FrameBytes * OverlapFrameCount * 4;
        var scope = new RawOwnershipScope(new RawBufferPool());
        scope.SetReturnedBufferPoisonForTesting(Poison);
        var cache = new FrameCache(scope, CapacityBytes);
        var request = new PreviewRequest { EncoderPath = "fake", InputFile = "input", Fps = 30,
            TargetSeconds = 0, PreMarginSeconds = 0, PostMarginSeconds = 1 };
        RawFrameLease[] Frames(byte value)
        {
            var result = Enumerable.Range(0, OverlapFrameCount)
                .Select(index => scope.RentReader(Header, index / Header.Fps, 1 / Header.Fps, scope.NewRunId())).ToArray();
            foreach (var item in result) Array.Fill(item.Frame.RawPixels, value);
            return result;
        }
        var readers = Frames(Pixel);
        var oldFrames = readers.Select(reader => reader.Frame).ToArray();
        var oldBuffers = oldFrames.Select(frame => frame.RawPixels).ToArray();
        var segment = cache.StoreSegment(request, oldFrames, new(0, 0, true), 0, OverlapFrameCount / Header.Fps)!;
        using var snapshot = cache.GetSegmentOwned(segment.SegmentId, "Snapshot")!;
        foreach (var reader in readers) reader.Dispose();
        var changed = Frames(ChangedPixel);
        var changedBuffers = changed.Select(reader => reader.Frame.RawPixels).ToArray();
        object gate = typeof(FrameCache).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cache)!;
        // 実際の重なり照合の解放は最外の排他を抜けてからなので、その境界で所有と画素を確認する。
        lock (gate)
        {
            cache.StoreSegment(request, changed.Select(reader => reader.Frame).ToArray(), new(0, 0, true), 0, OverlapFrameCount / Header.Fps);
            Assert.Null(cache.GetSegment(segment.SegmentId));
            Assert.True(scope.Capture().OwnerKinds["StitchComparison"] >= OverlapFrameCount * 2);
            cache.Clear();
            foreach (var reader in changed) reader.Dispose();
            Assert.True(cache.PendingRawReleaseCount > 0);
            Assert.Equal(0, scope.CapturePool().FreeBytes);
            foreach (byte[] buffer in oldBuffers) Assert.All(buffer, value => Assert.Equal(Pixel, value));
            foreach (byte[] buffer in changedBuffers) Assert.All(buffer, value => Assert.Equal(ChangedPixel, value));
        }
        cache.Clear();
        Assert.Equal(0, cache.PendingRawReleaseCount);
        Assert.False(scope.Capture().OwnerKinds.ContainsKey("StitchComparison"));
        foreach (byte[] buffer in changedBuffers) Assert.All(buffer, value => Assert.Equal(Poison, value));
        Assert.Equal(OverlapFrameCount * FrameBytes, scope.CapturePool().FreeBytes);
        using (var selected = snapshot.RetainOne(0, "VmAnchor")) Assert.Equal(Pixel, selected.Frame.RawPixels[0]);
        foreach (byte[] buffer in oldBuffers) Assert.All(buffer, value => Assert.Equal(Pixel, value));
        snapshot.Dispose();
        foreach (byte[] buffer in oldBuffers) Assert.All(buffer, value => Assert.Equal(Poison, value));
        Assert.Throws<ObjectDisposedException>(() => oldFrames[0].RawPixels);
        using var reused = Rent(scope);
        Assert.Contains(oldBuffers, buffer => ReferenceEquals(buffer, reused.Frame.RawPixels));
        reused.Dispose();
        AssertZero(scope);
    }

    [Fact]
    public async Task 同時貸出は同じ配列を渡さず容量は返却時だけ制限する()
    {
        var scope = new RawOwnershipScope(new RawBufferPool(FrameBytes, FrameBytes));
        var owners = await Task.WhenAll(Enumerable.Range(0, ParallelReaders).Select(_ => Task.Run(() => Rent(scope))));
        var buffers = owners.Select(owner => owner.Frame.RawPixels).ToArray();
        Assert.Equal(ParallelReaders, buffers.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(ParallelReaders * FrameBytes, scope.CapturePool().LoanedBytes);
        await Task.WhenAll(owners.Select(owner => Task.Run(owner.Dispose)));
        Assert.Equal(FrameBytes, scope.CapturePool().FreeBytes);
        Assert.Equal(ParallelReaders - 1, scope.CapturePool().DiscardedFreeBuffers);
        AssertZero(scope);
    }
}

using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class RawPooledReaderTests
{
    private const int FrameBytes = 6;
    private const byte Pixel = 0x2a;
    private const byte Poison = 0xa5;
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
    private static long Run(RawOwnershipScope scope) => scope.NewRunId();

    [Fact]
    public void 読取は返却済み配列を全量上書きし途中終了なら所有を返す()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        scope.SetReturnedBufferPoisonForTesting(Poison);
        using var body = new MemoryStream(Enumerable.Repeat(Pixel, FrameBytes).ToArray());
        var first = new Y4mReader(body, Header).ReadOwnedFrameBody("FRAME Xts=1", scope, Run(scope));
        byte[] recycled = first.Frame.RawPixels;
        var old = first.Frame;
        first.Dispose();
        Assert.All(recycled, value => Assert.Equal(Poison, value));
        using var shortBody = new MemoryStream(new byte[] { Pixel, Pixel });
        Assert.Throws<Y4mTruncatedException>(() => new Y4mReader(shortBody, Header).ReadOwnedFrameBody("FRAME", scope, Run(scope)));
        Assert.Equal(0, scope.Capture().LiveOwners);
        Assert.Equal(0, scope.CapturePool().LoanedBytes);
        using var completeBody = new MemoryStream(Enumerable.Repeat(Pixel, FrameBytes).ToArray());
        using var completed = new Y4mReader(completeBody, Header).ReadOwnedFrameBody("FRAME Xts=2", scope, Run(scope));
        Assert.Same(recycled, completed.Frame.RawPixels);
        Assert.All(completed.Frame.RawPixels, value => Assert.Equal(Pixel, value));
        Assert.Throws<ObjectDisposedException>(() => old.RawPixels);
        Assert.Equal(2, scope.CapturePool().Hits);
        completed.Dispose();
        Assert.Equal(0, scope.Capture().LiveTokens);
    }

    [Fact]
    public async Task 待機を取り消しても実際の読取が終わるまで配列を貸し直さない()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        scope.SetReturnedBufferPoisonForTesting(Poison);
        var run = Run(scope);
        using var body = new BlockingBody();
        Task<RawFrameLease> reading = Task.Run(() => new Y4mReader(body, Header).ReadOwnedFrameBody("FRAME", scope, run));
        try
        {
            await body.Started.Task.WaitWithDiagnosticsAsync(Safety);
            Assert.Equal(FrameBytes, scope.CapturePool().LoanedBytes);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(cancellation.Token));
            Assert.False(reading.IsCompleted);
            Assert.Equal(0, scope.CapturePool().FreeBytes);
            using var concurrent = scope.RentReader(Header, 0, null, Run(scope));
            Assert.Equal(0, scope.CapturePool().Hits);
            Assert.Equal(FrameBytes * 2, scope.CapturePool().LoanedBytes);
            // 入力変更で読取中の配列を回収せず、実際に使い終わったときだけ返す。
            scope.ClearFreeBuffers();
            body.Release.TrySetResult();
            using var completed = await reading.WaitWithDiagnosticsAsync(Safety);
            Assert.NotSame(concurrent.Frame.RawPixels, completed.Frame.RawPixels);
            Assert.All(completed.Frame.RawPixels, value => Assert.Equal(Pixel, value));
            completed.Dispose(); concurrent.Dispose();
            Assert.Equal(FrameBytes * 2, scope.CapturePool().FreeBytes);
            Assert.Equal(2, scope.CapturePool().Returned);
            Assert.Equal(0, scope.Capture().LiveTokens);
        }
        finally
        {
            body.Release.TrySetResult();
            try { (await reading.WaitWithDiagnosticsAsync(Safety)).Dispose(); }
            catch (Y4mFormatException) { }
        }
    }

    [Fact]
    public async Task 時計の期限が過ぎても実読取と最後の所有が終わるまで貸し直さない()
    {
        var scope = new RawOwnershipScope(new RawBufferPool());
        scope.SetReturnedBufferPoisonForTesting(Poison);
        var clock = new FakeTimeProvider();
        // 共通の停止検知でも仮想時計を使い、実時間を待たずに両方のTask型の診断を確かめる。
        var diagnosticClock = new FakeTimeProvider();
        var pendingDiagnostic = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task diagnostic = ((Task)pendingDiagnostic.Task).WaitWithDiagnosticsAsync(TestTimeouts.Hang, diagnosticClock);
        Task<int> genericDiagnostic = pendingDiagnostic.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang, diagnosticClock);
        diagnosticClock.Advance(TestTimeouts.Hang);
        foreach (Task waiting in new Task[] { diagnostic, genericDiagnostic })
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => waiting);
            Assert.Contains("ThreadPool.ThreadCount=", exception.Message);
            Assert.Contains("ThreadPool.PendingWorkItemCount=", exception.Message);
            Assert.Contains("ThreadPool.CompletedWorkItemCount=", exception.Message);
            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        }
        pendingDiagnostic.SetResult(1);
        Assert.Equal(1, await pendingDiagnostic.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang));
        var deadline = TimeSpan.FromSeconds(1);
        using var body = new BlockingBody();
        var run = Run(scope);
        Task<RawFrameLease> reading = Task.Run(() => new Y4mReader(body, Header).ReadOwnedFrameBody("FRAME", scope, run));
        try
        {
            await body.Started.Task.WaitWithDiagnosticsAsync(Safety);
            Task<RawFrameLease> timed = reading.WaitAsync(deadline, clock);
            clock.Advance(deadline);
            await Assert.ThrowsAsync<TimeoutException>(() => timed);
            Assert.False(reading.IsCompleted);
            Assert.Equal(FrameBytes, scope.CapturePool().LoanedBytes);
            Assert.Equal(0, scope.CapturePool().FreeBytes);
            using var concurrent = scope.RentReader(Header, 0, null, Run(scope));
            Assert.Equal(0, scope.CapturePool().Hits);
            body.Release.TrySetResult();
            using var completed = await reading.WaitWithDiagnosticsAsync(Safety);
            byte[] original = completed.Frame.RawPixels;
            Assert.NotSame(concurrent.Frame.RawPixels, original);
            using var conversion = completed.Retain("FrameWork");
            completed.Dispose();
            // 読取が終わっても変換が画素を使っている間は、期限切れを理由に返却しない。
            Assert.Equal(0, scope.CapturePool().FreeBytes);
            Assert.All(original, value => Assert.Equal(Pixel, value));
            conversion.Dispose();
            Assert.Equal(FrameBytes, scope.CapturePool().FreeBytes);
            Assert.All(original, value => Assert.Equal(Poison, value));
            using var next = scope.RentReader(Header, 0, null, Run(scope));
            Assert.Same(original, next.Frame.RawPixels);
            Assert.Equal(1, scope.CapturePool().Hits);
            next.Dispose(); concurrent.Dispose();
            Assert.Equal(0, scope.CapturePool().LoanedBytes);
            Assert.Equal(0, scope.Capture().LiveTokens);
        }
        finally
        {
            body.Release.TrySetResult();
            (await reading.WaitWithDiagnosticsAsync(Safety)).Dispose();
        }
    }

    [Fact]
    public void 旧入力の遅い返却も新しい形式の全量読取後にだけ公開できる()
    {
        const byte NewPixel = 0x77;
        var scope = new RawOwnershipScope(new RawBufferPool());
        scope.SetReturnedBufferPoisonForTesting(Poison);
        using var oldBody = new MemoryStream(Enumerable.Repeat(Pixel, FrameBytes).ToArray());
        var old = new Y4mReader(oldBody, Header).ReadOwnedFrameBody("FRAME Xts=1", scope, Run(scope));
        var oldFrame = old.Frame;
        byte[] buffer = oldFrame.RawPixels;
        scope.ClearFreeBuffers();
        var newHeader = new Y4mHeader { Width = 3, Height = 2, Chroma = Y4mChroma.Mono, FpsNum = 60, FpsDen = 1 };
        using var currentBody = new MemoryStream(Enumerable.Repeat(NewPixel, FrameBytes).ToArray());
        using var current = new Y4mReader(currentBody, newHeader).ReadOwnedFrameBody("FRAME Xts=2", scope, Run(scope));
        Assert.NotSame(buffer, current.Frame.RawPixels);
        Assert.All(buffer, value => Assert.Equal(Pixel, value));
        old.Dispose();
        Assert.All(buffer, value => Assert.Equal(Poison, value));
        using var nextBody = new MemoryStream(Enumerable.Repeat(NewPixel, FrameBytes).ToArray());
        using var next = new Y4mReader(nextBody, newHeader).ReadOwnedFrameBody("FRAME Xts=3", scope, Run(scope));
        Assert.Same(buffer, next.Frame.RawPixels);
        Assert.NotEqual(oldFrame.FrameId, next.Frame.FrameId);
        Assert.Equal(newHeader.Width, next.Frame.Header.Width);
        Assert.Equal(newHeader.Chroma, next.Frame.Header.Chroma);
        Assert.All(next.Frame.RawPixels, value => Assert.Equal(NewPixel, value));
        Assert.All(current.Frame.RawPixels, value => Assert.Equal(NewPixel, value));
        Assert.Throws<ObjectDisposedException>(() => oldFrame.RawPixels);
        Assert.Equal(1, scope.CapturePool().Hits);
        next.Dispose(); current.Dispose();
        Assert.Equal(0, scope.Capture().LiveTokens);
    }

    [Fact]
    public void 公開読取とpoolなしのowned読取は再利用から独立した画素を保つ()
    {
        var scope = new RawOwnershipScope();
        scope.SetReturnedBufferPoisonForTesting(Poison);
        using var publicBody = new MemoryStream(Enumerable.Repeat(Pixel, FrameBytes).ToArray());
        var publicFrame = new Y4mReader(publicBody, Header).ReadFrameBody("FRAME");
        using var ownedBody = new MemoryStream(Enumerable.Repeat(Pixel, FrameBytes).ToArray());
        var owned = new Y4mReader(ownedBody, Header).ReadOwnedFrameBody("FRAME", scope, scope.NewRunId());
        byte[] external = owned.Frame.RawPixels;
        owned.Dispose();
        Assert.All(publicFrame.RawPixels, value => Assert.Equal(Pixel, value));
        Assert.All(external, value => Assert.Equal(Pixel, value));
        Assert.Equal(0, scope.CapturePool().NewArrays);
        Assert.Equal(0, scope.CapturePool().Returned);
    }

    private sealed class BlockingBody : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            Started.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            if (Interlocked.Exchange(ref _read, 1) != 0) return 0;
            Array.Fill(buffer, Pixel, offset, count);
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

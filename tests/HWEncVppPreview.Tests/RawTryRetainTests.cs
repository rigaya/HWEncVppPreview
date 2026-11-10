using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class RawTryRetainTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
    private static RawFrameLease Frame(RawOwnershipScope scope, byte pixel = 17)
        => scope.Create(Header, 10, 1d / 30, Enumerable.Repeat(pixel, 6).ToArray(), scope.NewRunId(), "Reader");

    [Fact]
    public void 成功取得は一度だけ参照を増やしproducer返却後も画素を保つ()
    {
        var scope = new RawOwnershipScope();
        var producer = Frame(scope);
        var descriptor = producer.Frame;
        Assert.True(RawFrameLease.TryCapture(descriptor, "選択", out var selected));
        Assert.NotNull(selected);
        Assert.Same(descriptor, selected!.Frame);
        Assert.True(selected.Owned);
        producer.Dispose();
        Assert.Equal(17, selected.Pixels.Span[0]);
        var state = scope.Capture();
        Assert.Equal(2, state.TotalRetains);
        Assert.Equal(1, state.TotalTokenReleases);
        Assert.Equal(1, state.LiveTokens);
        Assert.Equal(1, state.TryRetainAttempts);
        Assert.Equal(1, state.TryRetainSuccesses);
        Assert.Equal(0, state.TryRetainFailures);
        Assert.Equal(1, state.TryRetainsByCaller["選択"].Successes);
        selected.Dispose();
        selected.Dispose();
        AssertEnded(scope);
    }

    [Fact]
    public void 最終Release後は失敗してmetadataだけが読める()
    {
        var scope = new RawOwnershipScope();
        var producer = Frame(scope);
        var descriptor = producer.Frame;
        long frameId = descriptor.FrameId;
        long runId = descriptor.RawRunId;
        producer.Dispose();
        long retains = scope.Capture().TotalRetains;
        Assert.False(RawFrameLease.TryCapture(descriptor, "失効選択", out var selected));
        Assert.Null(selected);
        Assert.Equal(frameId, descriptor.FrameId);
        Assert.Equal(runId, descriptor.RawRunId);
        Assert.Equal(10d, descriptor.Xts!.Value);
        Assert.Equal(1d / 30, descriptor.Xdur!.Value);
        Assert.Equal(2, descriptor.Header.Width);
        Assert.Equal(2, descriptor.Header.Height);
        Assert.Throws<ObjectDisposedException>(() => descriptor.RawPixels);
        var state = scope.Capture();
        Assert.Equal(retains, state.TotalRetains);
        Assert.Equal(1, state.TryRetainAttempts);
        Assert.Equal(0, state.TryRetainSuccesses);
        Assert.Equal(1, state.TryRetainFailures);
        AssertEnded(scope);
    }

    [Fact]
    public void 失敗は別owner作成後も復活せずcaller別台帳を合算できる()
    {
        var scope = new RawOwnershipScope();
        var producer = Frame(scope);
        var old = producer.Frame;
        Assert.True(RawFrameLease.TryCapture(old, "同一caller", out var first));
        first!.Dispose();
        producer.Dispose();
        Assert.False(RawFrameLease.TryCapture(old, "同一caller", out var failed));
        Assert.Null(failed);
        using (var next = Frame(scope, 33))
        {
            Assert.NotEqual(old.FrameId, next.FrameId);
            Assert.False(RawFrameLease.TryCapture(old, "別caller", out var resurrected));
            Assert.Null(resurrected);
            Assert.True(RawFrameLease.TryCapture(next.Frame, "別caller", out var current));
            Assert.Equal(33, current!.Pixels.Span[0]);
            current.Dispose();
        }
        var state = scope.Capture();
        Assert.Equal(4, state.TryRetainAttempts);
        Assert.Equal(2, state.TryRetainSuccesses);
        Assert.Equal(2, state.TryRetainFailures);
        Assert.Equal(4, state.TotalRetains);
        foreach (string caller in new[] { "同一caller", "別caller" })
        {
            var item = state.TryRetainsByCaller[caller];
            Assert.Equal(2, item.Attempts);
            Assert.Equal(1, item.Successes);
            Assert.Equal(1, item.Failures);
        }
        AssertEnded(scope);
    }

    [Fact]
    public void 台帳Captureは後続取得と失敗で書き換わらない()
    {
        var scope = new RawOwnershipScope();
        var producer = Frame(scope);
        var descriptor = producer.Frame;
        Assert.True(RawFrameLease.TryCapture(descriptor, "既存caller", out var first));
        first!.Dispose();
        var captured = scope.Capture();
        Assert.True(RawFrameLease.TryCapture(descriptor, "既存caller", out var second));
        second!.Dispose();
        producer.Dispose();
        Assert.False(RawFrameLease.TryCapture(descriptor, "新caller", out var failed));
        Assert.Null(failed);
        Assert.Equal(1, captured.TryRetainAttempts);
        Assert.Equal(1, captured.TryRetainSuccesses);
        Assert.Equal(0, captured.TryRetainFailures);
        Assert.Single(captured.TryRetainsByCaller);
        Assert.Equal(1, captured.TryRetainsByCaller["既存caller"].Attempts);
        Assert.Equal(1, captured.TryRetainsByCaller["既存caller"].Successes);
        Assert.Equal(0, captured.TryRetainsByCaller["既存caller"].Failures);
        var dictionary = Assert.IsAssignableFrom<System.Collections.IDictionary>(captured.TryRetainsByCaller);
        Assert.Throws<NotSupportedException>(() => dictionary.Clear());
        var current = scope.Capture();
        Assert.Equal(3, current.TryRetainAttempts);
        Assert.Equal(2, current.TryRetainsByCaller["既存caller"].Successes);
        Assert.Equal(1, current.TryRetainsByCaller["新caller"].Failures);
        AssertEnded(scope);
    }

    [Fact]
    public void 通常CaptureとRetainは失効後のthrow契約を維持する()
    {
        var scope = new RawOwnershipScope();
        var producer = Frame(scope);
        var descriptor = producer.Frame;
        using var normal = producer.Retain("通常保持");
        producer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => producer.Retain("返却済みlease"));
        Assert.Equal(17, normal.Pixels.Span[0]);
        normal.Dispose();
        Assert.Throws<ObjectDisposedException>(() => normal.Retain("返却済みlease"));
        Assert.Throws<ObjectDisposedException>(() => RawFrameLease.Capture(descriptor, "通常再取得"));
        Assert.Equal(2, scope.Capture().TotalRetains);
        Assert.Equal(0, scope.Capture().TryRetainAttempts);
        Assert.Empty(scope.Capture().TryRetainsByCaller);
        AssertEnded(scope);
    }

    [Fact]
    public void 異なるscopeへの取得は所有不具合として拒否し試行台帳へ入れない()
    {
        var ownerScope = new RawOwnershipScope();
        var wrongScope = new RawOwnershipScope();
        using var producer = Frame(ownerScope);
        Assert.Throws<ArgumentException>(() => wrongScope.TryRetain(producer.Frame.RawOwner!, "誤scope", out _));
        Assert.Equal(0, wrongScope.Capture().TryRetainAttempts);
        Assert.Empty(wrongScope.Capture().TryRetainsByCaller);
        Assert.Equal(0, ownerScope.Capture().TryRetainAttempts);
        Assert.Equal(17, producer.Pixels.Span[0]);
        producer.Dispose();
        AssertEnded(ownerScope);
        AssertEnded(wrongScope);
    }

    [Fact]
    public void legacyは非追跡leaseを成功として返しowned台帳へ入れない()
    {
        var scope = new RawOwnershipScope();
        byte[] pixels = Enumerable.Repeat((byte)44, 6).ToArray();
        var descriptor = new Y4mFrame(Header, 10, 1d / 30, pixels);
        Assert.True(RawFrameLease.TryCapture(descriptor, "legacy選択", out var selected));
        Assert.NotNull(selected);
        Assert.False(selected!.Owned);
        Assert.Same(descriptor, selected.Frame);
        Assert.Equal(44, selected.Pixels.Span[0]);
        selected.Dispose();
        Assert.Same(pixels, descriptor.Data);
        Assert.True(RawFrameLease.TryCapture(descriptor, "legacy再取得", out var again));
        Assert.False(again!.Owned);
        again.Dispose();
        var state = scope.Capture();
        Assert.Equal(0, state.NewOwners);
        Assert.Equal(0, state.TotalRetains);
        Assert.Equal(0, state.TryRetainAttempts);
        Assert.Empty(state.TryRetainsByCaller);
        AssertEnded(scope);
    }

    [Fact]
    public async Task 取得と最終Releaseの競合は成功生存か失敗nullに原子的に分かれる()
    {
        for (int iteration = 0; iteration < 64; ++iteration)
        {
            var scope = new RawOwnershipScope();
            var producer = Frame(scope, 55);
            var descriptor = producer.Frame;
            using var start = new Barrier(2);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task acquire = Task.Run(async () =>
            {
                Assert.True(start.SignalAndWait(Safety));
                bool success = RawFrameLease.TryCapture(descriptor, "競合", out var selected);
                using (selected)
                {
                    await released.Task.WaitWithDiagnosticsAsync(Safety);
                    if (success)
                    {
                        Assert.NotNull(selected);
                        Assert.Equal(55, selected!.Pixels.Span[0]);
                    }
                    else Assert.Null(selected);
                }
            });
            Task release = Task.Run(() =>
            {
                Assert.True(start.SignalAndWait(Safety));
                producer.Dispose();
                released.TrySetResult();
            });
            await Task.WhenAll(acquire, release).WaitWithDiagnosticsAsync(Safety);
            var state = scope.Capture();
            Assert.Equal(1, state.TryRetainAttempts);
            Assert.Equal(1, state.TryRetainSuccesses + state.TryRetainFailures);
            Assert.Equal(1 + state.TryRetainSuccesses, state.TotalRetains);
            AssertEnded(scope);
        }
    }

    [Fact]
    public async Task 並行callerの成功失敗累計は欠落せずcallerを混同しない()
    {
        var scope = new RawOwnershipScope();
        using var producer = Frame(scope, 66);
        var expiredProducer = Frame(scope, 77);
        var expired = expiredProducer.Frame;
        expiredProducer.Dispose();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] callers = { "同時A", "同時a", "同時B", "同時C" };
        Task[] workers = callers.Select(caller => Task.Run(async () =>
        {
            await start.Task.WaitWithDiagnosticsAsync(Safety);
            for (int i = 0; i < 100; ++i)
            {
                Assert.True(RawFrameLease.TryCapture(producer.Frame, caller, out var selected));
                using (selected) Assert.Equal(66, selected!.Pixels.Span[0]);
                Assert.False(RawFrameLease.TryCapture(expired, caller, out var failed));
                Assert.Null(failed);
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(workers).WaitWithDiagnosticsAsync(Safety);
        producer.Dispose();
        var state = scope.Capture();
        Assert.Equal(800, state.TryRetainAttempts);
        Assert.Equal(400, state.TryRetainSuccesses);
        Assert.Equal(400, state.TryRetainFailures);
        Assert.Equal(402, state.TotalRetains);
        Assert.Equal(4, state.TryRetainsByCaller.Count);
        foreach (string caller in callers)
        {
            var item = state.TryRetainsByCaller[caller];
            Assert.Equal(200, item.Attempts);
            Assert.Equal(100, item.Successes);
            Assert.Equal(100, item.Failures);
        }
        AssertEnded(scope);
    }

    private static void AssertEnded(RawOwnershipScope scope)
    {
        var state = scope.Capture();
        Assert.True(state.IsEmpty);
        Assert.Equal(0, state.LiveBytes);
        Assert.Equal(state.NewOwners, state.FinalReleases);
        Assert.Equal(state.NewBytes, state.FinalReleasedBytes);
        Assert.Equal(state.TotalRetains, state.TotalTokenReleases);
        Assert.Equal(state.TryRetainAttempts, state.TryRetainSuccesses + state.TryRetainFailures);
        Assert.Equal(state.TryRetainAttempts, state.TryRetainsByCaller.Values.Sum(item => item.Attempts));
        Assert.Equal(state.TryRetainSuccesses, state.TryRetainsByCaller.Values.Sum(item => item.Successes));
        Assert.Equal(state.TryRetainFailures, state.TryRetainsByCaller.Values.Sum(item => item.Failures));
    }
}

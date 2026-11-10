using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed class RawOwnershipTests
{
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };
    private static PreviewRequest Request => new() { EncoderPath = "fake", InputFile = "input", TargetSeconds = 0, Fps = 30, PreMarginSeconds = 0, PostMarginSeconds = 1 };
    private static RawFrameLease Frame(RawOwnershipScope s, double time = 0, byte value = 0x11) => s.Create(Header, time, 1d / 30, Enumerable.Repeat(value, 6).ToArray(), s.NewRunId(), "Reader");
    [Fact]
    public void LastReleaseDetachesArrayAndNeverResurrects()
    {
        var scope = new RawOwnershipScope();
        var a = Frame(scope);
        var descriptor = a.Frame;
        var b = a.Retain("Transform");
        Assert.Same(descriptor, b.Frame);
        Assert.Throws<InvalidOperationException>(() => descriptor.Data);
        Assert.Throws<InvalidOperationException>(() => YuvToBgra.Convert(descriptor));
        Assert.Throws<InvalidOperationException>(() => YuvToBgra.ConvertInto(descriptor, new byte[16]));
        YuvToBgra.ConvertInto(a, new byte[16]);
        a.Dispose();
        a.Dispose();
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(0x11, b.Pixels.Span[0]);
        b.Dispose();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(1, scope.Capture().FinalReleases);
        Assert.Equal(3, scope.Capture().TotalRetains);
        Assert.Equal(3, scope.Capture().TotalTokenReleases);
        Assert.Throws<ObjectDisposedException>(() => b.Retain("Bad"));
        Assert.Throws<ObjectDisposedException>(() => RawFrameLease.Capture(descriptor, "Resurrect"));
        Assert.Throws<ObjectDisposedException>(() => descriptor.RawPixels);
        Assert.Null(typeof(Y4mFrame).GetField("_legacyData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(descriptor));
    }
    [Fact]
    public void MetadataIsFrozenButLegacyDataIdentityIsUnchanged()
    {
        var tags = new Dictionary<string, string> { { "XTRANSFER", "pq" } };
        var h = new Y4mHeader { Width = 2, Height = 2, ExtTags = tags };
        var bytes = new byte[6];
        var scope = new RawOwnershipScope();
        using var owned = scope.Create(h, 0, null, bytes, 1, "Reader");
        tags["XTRANSFER"] = "bt709";
        Assert.Equal("pq", owned.Header.Transfer);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)owned.Header.ExtTags)["XTRANSFER"] = "hlg");
        var legacy = new Y4mFrame(h, 0, null, bytes);
        Assert.Same(bytes, legacy.Data);
    }
    [Fact]
    public void SnapshotSurvivesCacheClearWithoutPinningTheCacheEntry()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 12);
        using var reader = Frame(scope);
        var segment = cache.StoreSegment(Request, new[] { reader.Frame }, new(0, 0, true), 0, 1d / 30)!;
        using (var held = cache.GetSegmentOwned(segment.SegmentId, "Snapshot")!)
        {
            cache.Clear();
            Assert.Equal(0, cache.PendingRawReleaseCount);
            Assert.Null(cache.GetSegment(segment.SegmentId));
            using var frame = held.RetainOne(0, "Selection");
            Assert.Equal(0x11, frame.Pixels.Span[0]);
        }
        reader.Dispose();
        Assert.True(scope.Capture().IsEmpty);
    }
    [Fact]
    public void NavigationOwnsTheWholeReachableGuardSegment()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 64);
        using var a = Frame(scope, 0);
        using var b = Frame(scope, 1d / 30);
        using var c = Frame(scope, 2d / 30);
        cache.StoreSegment(Request, new[] { a.Frame, b.Frame, c.Frame }, new(0, 0, true), 1d / 30, 2d / 30);
        using (var navigation = cache.GetNavigationFramesOwned(Request, "Navigation"))
        {
            Assert.NotEmpty(navigation.References);
            cache.Clear();
            Assert.Equal(0, cache.PendingRawReleaseCount);
            a.Dispose();
            b.Dispose();
            c.Dispose();
            foreach (var reference in navigation.References)
            {
                using var all = RawFrameSetLease.Capture(reference.Segment, "Interval");
                Assert.Equal(3, all.Frames.Count);
                for (int i = 0; i < all.Frames.Count; i++)
                {
                    using var pixel = all.RetainOne(i, "Read");
                    Assert.Equal(0x11, pixel.Pixels.Span[0]);
                }
            }
        }
        Assert.True(scope.Capture().IsEmpty);
    }
    [Fact]
    public void StitchRejectReleasesCacheButKeepsTheOldSnapshot()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 128);
        using var a = Frame(scope, 0, 0x11);
        using var b = Frame(scope, 1d / 30, 0x11);
        var old = cache.StoreSegment(Request, new[] { a.Frame, b.Frame }, new(0, 0, true), 0, 2d / 30)!;
        using (var snapshot = cache.GetSegmentOwned(old.SegmentId, "Snapshot")!)
        {
            a.Dispose();
            b.Dispose();
            using var c = Frame(scope, 0, 0x22);
            using var d = Frame(scope, 1d / 30, 0x22);
            cache.StoreSegment(Request, new[] { c.Frame, d.Frame }, new(0, 0, true), 0, 2d / 30);
            Assert.Null(cache.GetSegment(old.SegmentId));
            Assert.False(scope.Capture().OwnerKinds.ContainsKey("StitchComparison"));
            using (var pixel = snapshot.RetainOne(0, "Anchor"))
                Assert.Equal(0x11, pixel.Pixels.Span[0]);
            cache.Clear();
            Assert.Equal(0, cache.PendingRawReleaseCount);
            c.Dispose();
            d.Dispose();
        }
        Assert.True(scope.Capture().IsEmpty);
    }
    [Fact]
    public async Task RetainDisposeRaceAndCallbackFailureReleaseExactlyOnce()
    {
        var scope = new RawOwnershipScope();
        var frame = Frame(scope);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task retain = Task.Run(async () =>
        {
            await start.Task;
            RawFrameLease? copy = null;
            try
            {
                copy = frame.Retain("Concurrent");
            }
            catch (ObjectDisposedException)
            {
            }

            using (copy)
            {
                if (copy != null)
                    Assert.Equal(0x11, copy.Pixels.Span[0]);
            }
        });
        Task dispose = Task.Run(async () =>
        {
            await start.Task;
            frame.Dispose();
        });
        start.SetResult();
        await Task.WhenAll(retain, dispose);
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(1, scope.Capture().FinalReleases);
        using (var second = Frame(scope))
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var notification = new OwnedPreviewEnvelope(frame: new(Request, second.Frame, false));
                throw new InvalidOperationException("callback例外");
            }));
            Assert.Equal(1, scope.Capture().LiveTokens);
        }
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(2, scope.Capture().FinalReleases);
    }

    [Fact]
    public void RejectedStoreReleasesOnlyThePreparedCacheOwner()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 0);
        using var frame = Frame(scope);
        Assert.Null(cache.StoreSegment(Request, new[] { frame.Frame }, new(0, 0, true), 0, 1d / 30));
        Assert.Equal(1, scope.Capture().LiveTokens);
        Assert.Equal(0x11, frame.Pixels.Span[0]);
        frame.Dispose();
        Assert.True(scope.Capture().IsEmpty);
    }
    [Fact]
    public void PartialCacheAndSetRetainFailureRollBackAllPreparedTokens()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 128);
        using var valid = Frame(scope);
        var dead = Frame(scope);
        var released = dead.Frame;
        dead.Dispose();
        var frames = new[] { valid.Frame, released };
        Assert.Throws<ObjectDisposedException>(() => cache.StoreSegment(Request, frames, new(0, 0, true), 0, 1));
        Assert.Equal(1, scope.Capture().LiveTokens);
        Assert.Equal(0, cache.SizeBytes);
        Assert.Equal(0, cache.PendingRawReleaseCount);
        Assert.Throws<ObjectDisposedException>(() => RawFrameSetLease.Capture(frames, "Snapshot"));
        Assert.Equal(1, scope.Capture().LiveTokens);
        Assert.Throws<InvalidOperationException>(() => new FrameCache().StoreSegment(Request, new[] { valid.Frame }, new(0, 0, true), 0, 1));
        Assert.Throws<InvalidOperationException>(() => new FrameCache(new RawOwnershipScope()).StoreSegment(Request, new[] { valid.Frame }, new(0, 0, true), 0, 1));
        valid.Dispose();
        Assert.True(scope.Capture().IsEmpty);
    }
    [Fact]
    public void CompletionMetadataIsStableAfterTheFiniteListIsClosed()
    {
        var scope = new RawOwnershipScope();
        using var reader = Frame(scope);
        var list = new RawFrameList("Finite");
        list.Add(reader.Frame);
        using (var notification = new OwnedPreviewEnvelope(completed: new(Request, new(0, ""), false, false)
        {
            Frames = list.AsReadOnly()
        }))
        using (var ui = notification.Retain("UiPost"))
        {
            var payload = ui.Completed!;
            list.Dispose();
            reader.Dispose();
            Assert.Single(payload.Frames);
            using var frame = RawFrameLease.Capture(payload.Frames[0], "Conversion");
            Assert.Equal(0x11, frame.Pixels.Span[0]);
        }
        Assert.True(scope.Capture().IsEmpty);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoredCancellationDoesNotReleaseTheReadBuffer(bool eof)
    {
        var scope = new RawOwnershipScope();
        using var input = new BlockedBody(eof);
        var reader = new Y4mReader(input, Header);
        Task<RawFrameLease?> reading = Task.Run(() => reader.ReadOwnedFrame(scope, 1));
        await input.Entered.Task.WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        var clock = new FakeTimeProvider();
        Task timeout = Task.Delay(TimeSpan.FromSeconds(1), clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Same(timeout, await Task.WhenAny(reading, timeout));
        Assert.False(reading.IsCompleted);
        Assert.Equal(1, scope.Capture().LiveOwners);
        Assert.Equal(1, scope.Capture().OwnerKinds["Reader"]);
        input.Release.TrySetResult();
        if (eof)
            await Assert.ThrowsAsync<Y4mTruncatedException>(async () => await reading);
        else
        {
            using var done = await reading;
            Assert.NotNull(done);
            Assert.Equal(0x11, done.Pixels.Span[0]);
        }
        Assert.True(scope.Capture().IsEmpty);
    }
    [Fact]
    public async Task OwnedControllerUsesOnlyInternalNotificationsAndKeepsQueuedCompletion()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 128);
        var backend = new OwnedBackend();
        await using var controller = new PreviewController(scope, backend, backend, cache, debounce: TimeSpan.Zero, backgroundBackend: backend, backgroundFallback: backend);
        int publicEvents = 0;
        OwnedPreviewEnvelope? queued = null;
#if !DEBUG
        // Debugではownedの公開購読をEmitが検出するため、非通知の検査はReleaseで行う。
        controller.Completed += _ => publicEvents++;
        controller.FrameReady += _ => publicEvents++;
        controller.SegmentUpdated += _ => publicEvents++;
#endif
        controller.OwnedCompleted += value =>
        {
            queued?.Dispose();
            queued = value.Retain("UiPost");
        };
        controller.Submit(Request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.NotNull(queued);
        Assert.Equal(2, queued.Completed!.Frames.Count);
        Assert.Equal(0, publicEvents);
        Assert.Equal(1, backend.Runs);
        controller.Submit(Request);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Equal(1, backend.Runs);
        cache.Clear();
        Assert.Equal(0, cache.PendingRawReleaseCount);
        await controller.DisposeAsync();
        using (var frame = RawFrameLease.Capture(queued!.Completed!.Frames[0], "Conversion"))
            Assert.Equal(0x11, frame.Pixels.Span[0]);
        queued.Dispose();
        Assert.True(scope.Capture().IsEmpty);
    }
    [Fact]
    public async Task OwnedFinitePrefixAndGuardKeepOnlyCandidateAndNeighbors()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 18);
        var backend = new OwnedBackend(burst: true);
        await using var controller = new PreviewController(scope, backend, backend, cache, debounce: TimeSpan.Zero);
        RawFrameLease? selected = null;
        int completedCount = 0;
        PreviewFailure? failure = null;
        controller.OwnedFrameReady += value =>
        {
            selected?.Dispose();
            selected = RawFrameLease.Capture(value.FrameReady!.Frame, "UiSelection");
        };
        controller.OwnedCompleted += value => completedCount = value.Completed!.Frames.Count;
        controller.Failed += value => failure = value;
        controller.Submit(Request with
        {
            TargetSeconds = 10,
            Fps = 2,
            PreMarginSeconds = 5,
            PostMarginSeconds = .5,
            Capabilities = new(true),
            TimestampOriginSeconds = 1000
        });
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.NotNull(selected);
        Assert.Equal(10, selected.Pixels.Span[0]);
        Assert.Equal(1010, selected.Xts);
        Assert.InRange(completedCount, 1, 3);
        Assert.Equal(12, scope.Capture().NewOwners);
        Assert.InRange(scope.Capture().LiveOwners, 1, 3);
        cache.Clear();
        Assert.Equal(0, cache.PendingRawReleaseCount);
        await controller.DisposeAsync();
        Assert.Equal(1, scope.Capture().LiveOwners);
        selected.Dispose();
        Assert.True(scope.Capture().IsEmpty);
    }
    [Fact]
    public async Task OwnedReplayDropsRejectedRunAndNeverSwitchesToLegacyRaw()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 128);
        var backend = new OwnedBackend(replayFirst: true);
        await using var controller = new PreviewController(scope, backend, backend, cache, debounce: TimeSpan.Zero);
        OwnedPreviewEnvelope? held = null;
        PreviewFailure? failure = null;
        controller.OwnedCompleted += value => held = value.Retain("UiPost");
        controller.Failed += value => failure = value;
        controller.Submit(Request with
        {
            Capabilities = new(true),
            TimestampOriginSeconds = 1000
        });
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.Equal(2, backend.Runs);
        Assert.NotNull(held);
        Assert.True(held.Completed!.IsReplay);
        Assert.All(held.Completed.Frames, frame =>
        {
            Assert.Equal(2, frame.RawRunId);
            Assert.Null(frame.Xts);
            Assert.Equal(0x22, frame.RawPixels[0]);
        });
        cache.Clear();
        Assert.Equal(0, cache.PendingRawReleaseCount);
        await controller.DisposeAsync();
        held.Dispose();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(3, scope.Capture().FinalReleases);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RealReaderBackendsRetainOnlyTheAcceptedSinkFrames(bool resident, bool failCallback)
    {
        var scope = new RawOwnershipScope();
        await using IPreviewBackend backend = resident ? new ServerBackend() : new OneShotBackend();
        using var sink = new OwnedRecordingSink(failCallback);
        var request = Request with
        {
            EncoderPath = BackendTests.FakeEncoderPath(),
            PostMarginSeconds = .1
        };
        var run = ((IRawOwnedBackend)backend).RunOwnedAsync(request, sink, scope);
        if (failCallback)
            await Assert.ThrowsAsync<PreviewBackendException>(async () => await run.WaitWithDiagnosticsAsync(TestTimeouts.Hang));
        else
        {
            Assert.Equal(0, (await run.WaitWithDiagnosticsAsync(TestTimeouts.Hang)).ExitCode);
            Assert.Equal(4, sink.Frames.Count);
            Assert.All(sink.Frames, lease => Assert.Equal(64 * 36 * 3 / 2, lease.Pixels.Length));
        }
        await backend.DisposeAsync();
        sink.Dispose();
        Assert.True(scope.Capture().IsEmpty);
        Assert.Equal(scope.Capture().NewOwners, scope.Capture().FinalReleases);
    }
    private sealed class OwnedRecordingSink(bool fail) : IRawOwnedSink, IDisposable
    {
        internal List<RawFrameLease> Frames { get; } = new();
        public void OnHeader(Y4mHeader header)
        {
        }
        public void OnFrame(Y4mFrame frame)
        {
            if (fail)
                throw new InvalidOperationException("通知先の例外");
            Frames.Add(RawFrameLease.Capture(frame, "Sink"));
        }
        public void OnOutput()
        {
        }
        public void Dispose()
        {
            foreach (var frame in Frames)
                frame.Dispose();
            Frames.Clear();
        }
    }
    [Fact]
    public async Task OwnedStreamKeepsItsUiFrameUntilTheRealReadAndUiOwnersClose()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 4096);
        var backend = new OwnedStreamingBackend();
        await using var controller = new PreviewController(scope, backend, backend, cache, debounce: TimeSpan.Zero);
        OwnedPreviewEnvelope? held = null;
        controller.OwnedFrameReady += value =>
        {
            held?.Dispose();
            held = value.Retain("UiPost");
        };
        controller.Submit(Request with
        {
            TargetSeconds = 10,
            Capabilities = new(true),
            TimestampOriginSeconds = 1000,
            Continuous = true
        });
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.NotNull(held);
        Assert.True(scope.Capture().LiveOwners > 0);
        await controller.DisposeAsync();
        cache.Clear();
        Assert.Equal(0, cache.PendingRawReleaseCount);
        using (var read = RawFrameLease.Capture(held!.FrameReady!.Frame, "Conversion"))
            Assert.Equal(0x33, read.Pixels.Span[0]);
        held.Dispose();
        Assert.True(scope.Capture().IsEmpty);
    }
    private sealed class OwnedStreamingBackend : IPreviewBackend, IRawOwnedBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default) => throw new InvalidOperationException();
        async Task<PreviewRunResult> IRawOwnedBackend.RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink, RawOwnershipScope scope, CancellationToken token)
        {
            long run = scope.NewRunId();
            double start = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            try
            {
                for (int i = 0; ; i++)
                {
                    var gate = sink.BeforeReadAsync(token);
                    if (!gate.IsCompleted)
                        sink.OnReadStateChanged(false);
                    await gate;
                    sink.OnReadStateChanged(true);
                    token.ThrowIfCancellationRequested();
                    using var frame = scope.Create(Header, 1000 + start + i / 30d, 1d / 30, Enumerable.Repeat((byte)0x33, 6).ToArray(), run, "Reader");
                    sink.OnFrame(frame.Frame);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new(-12, "", true);
            }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public void CacheReleaseQueueIsEmptyAfterEachMutationAndLookup()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 18);
        using var a = Frame(scope, 0);
        using var b = Frame(scope, 1d / 30);
        using var c = Frame(scope, 2d / 30);
        void Empty() => Assert.Equal(0, cache.PendingRawReleaseCount);
        long id = cache.BeginSegment(Request, new(0, 0, true), 0);
        Empty();
        Assert.True(cache.Append(id, a.Frame));
        Empty();
        Assert.True(cache.Append(id, b.Frame));
        Empty();
        Assert.True(cache.Append(id, c.Frame));
        Empty();
        cache.PinCurrent(id, 2d / 30);
        cache.CanAppend(id, 6);
        Empty();
        cache.CompleteSegment(id, true);
        Empty();
        cache.GetNavigationFrames(Request);
        Empty();
        using (var nav = cache.GetNavigationFramesOwned(Request, "Navigation"))
            Empty();
        cache.PinCurrent(null, 0);
        cache.RemoveSegment(id);
        Empty();
        cache.Store(Request, new[] { a.Frame, b.Frame });
        Empty();
        cache.Store(Request with
        {
            InputFile = "other"
        }, new[] { c.Frame, b.Frame });
        Empty();
        cache.InvalidateSource(Request with
        {
            InputFile = "other"
        });
        Empty();
        cache.StoreSegment(Request, new[] { a.Frame }, new(0, 0, true), 0, 1d / 30);
        Empty();
        cache.ResetSourceDiagnosis(Request, discard: true);
        Empty();
        cache.Clear();
        Assert.Equal(0, cache.PendingRawReleaseCount);
        Empty();
        a.Dispose();
        b.Dispose();
        c.Dispose();
        var state = scope.Capture();
        Assert.True(state.IsEmpty);
        Assert.Equal(state.TotalRetains, state.TotalTokenReleases);
    }
    [Fact]
    public void ReverseTrimReleasesAllDroppedTokensBeforeReturning()
    {
        var scope = new RawOwnershipScope();
        var cache = new FrameCache(scope, 86 * 6);
        var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = 60, FpsDen = 1 };
        var request = Request with
        {
            Fps = 60,
            TargetSeconds = 10.1,
            Capabilities = new(true),
            TimestampOriginSeconds = 1000
        };
        long id = cache.BeginSegment(request, new(1000, 7, true), 10);
        for (int i = 0; i < 86; i++)
        {
            using var frame = scope.Create(header, 1010 + i / 60d, 1d / 60, new byte[6], 1, "Reader");
            Assert.True(cache.Append(id, frame.Frame));
            Assert.Equal(0, cache.PendingRawReleaseCount);
        }
        cache.PinCurrent(id, 10.1);
        Assert.True(cache.ReverseWindowRoom(request, 10, 10.1, 4d / 60) >= 43 * 6);
        Assert.Equal(0, cache.PendingRawReleaseCount);
        Assert.True(cache.TryTrimReverseWindow(request, 10, 10.1, 4d / 60, 43 * 6));
        Assert.Equal(0, cache.PendingRawReleaseCount);
        Assert.Equal(8, scope.Capture().LiveOwners);
        cache.Clear();
        Assert.Equal(0, cache.PendingRawReleaseCount);
        Assert.Equal(0, cache.PendingRawReleaseCount);
        Assert.True(scope.Capture().IsEmpty);
    }
    private sealed class OwnedBackend(bool burst = false, bool replayFirst = false) : IPreviewBackend, IRawOwnedBackend
    {
        internal int Runs;
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
            => throw new InvalidOperationException("公開backend経路へownedを落としてはいけません。");
        Task<PreviewRunResult> IRawOwnedBackend.RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink, RawOwnershipScope scope, CancellationToken token)
        {
            Runs++;
            long run = scope.NewRunId();
            var header = burst ? new Y4mHeader { Width = 2, Height = 2, FpsNum = 2, FpsDen = 1 } : Header;
            sink.OnHeader(header);
            if (replayFirst && Runs == 1)
            {
                using var invalid = scope.Create(header, 0, null, Enumerable.Repeat((byte)0x77, 6).ToArray(), run, "Backend");
                sink.OnFrame(invalid.Frame);
                return Task.FromResult(new PreviewRunResult(-12, "", true));
            }
            for (int i = 0; i < (burst ? 12 : 2); i++)
            {
                double? time = burst ? 1005 + i / 2d : replayFirst ? null : i / 30d;
                byte value = burst ? (byte)i : replayFirst ? (byte)0x22 : (byte)0x11;
                using var frame = scope.Create(header, time, null, Enumerable.Repeat(value, 6).ToArray(), run, "Backend");
                sink.OnFrame(frame.Frame);
            }
            return Task.FromResult(new PreviewRunResult(0, ""));
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockedBody(bool eof) : Stream
    {
        private readonly byte[] _line = System.Text.Encoding.ASCII.GetBytes("FRAME Xts=0\n"); private int _offset;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < _line.Length)
            {
                int n = Math.Min(count, _line.Length - _offset);
                Array.Copy(_line, _offset, buffer, offset, n);
                _offset += n;
                return n;
            }
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            if (eof)
                return 0;
            Array.Fill(buffer, (byte)0x11, offset, count);
            return count;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override void Flush()
        {
        }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

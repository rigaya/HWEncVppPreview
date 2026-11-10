using System.Reflection;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class StreamMetadataReadTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int FrameBytes = 6;
    private const long DefaultCapacityBytes = 4096;
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(30);
    private const double Origin = 1000;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };

    [Fact]
    public async Task 読取予算の停止と需要移動後の再開は画素を取得しない()
    {
        await using var state = new StreamState(new[] { 0d, .5, 1, 2 });
        var initial = state.Scope.Capture();
        Task paused = state.Sink.BeforeReadAsync(state.Cancellation.Token).AsTask();
        Assert.False(paused.IsCompleted);
        Assert.Equal(initial.TotalRetains, state.Scope.Capture().TotalRetains);
        state.Set("LatestRequest", state.Request with { TargetSeconds = 1 });
        state.SignalRead();
        await paused.WaitWithDiagnosticsAsync(Safety);
        Assert.True(state.Sink.BeforeReadAsync(state.Cancellation.Token).IsCompletedSuccessfully);
        Assert.Equal(initial.TotalRetains, state.Scope.Capture().TotalRetains);
        Assert.Equal(initial.LiveTokens, state.Scope.Capture().LiveTokens);
        Assert.Equal(Origin + 2, state.Cache.GetSegment(state.SegmentId)!.Frames[^1].Xts);
    }

    [Fact]
    public async Task byte予算で停止中の読取は取消で完了し画素を固定しない()
    {
        const int FrameCount = 5;
        const long Capacity = FrameBytes * 16;
        await using var state = new StreamState(Enumerable.Range(0, FrameCount).Select(i => i / 30d).ToArray(), Capacity);
        var initial = state.Scope.Capture();
        Task paused = state.Sink.BeforeReadAsync(state.Cancellation.Token).AsTask();
        Assert.False(paused.IsCompleted);
        state.Cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await paused.WaitWithDiagnosticsAsync(Safety));
        Assert.Equal(initial.TotalRetains, state.Scope.Capture().TotalRetains);
        Assert.Equal(FrameCount, state.Cache.GetSegment(state.SegmentId)!.Frames.Count);
        Assert.True(state.Sink.BeforeReadAsync(state.Cancellation.Token).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task VFRの安全な候補がない端では永久停止せず失敗を返す()
    {
        await using var state = new StreamState(new[] { .5, 1d, 1.5, 2 });
        var initial = state.Scope.Capture();
        Assert.Throws<InvalidOperationException>(() => { _ = state.Sink.BeforeReadAsync(state.Cancellation.Token); });
        Assert.Equal(initial.TotalRetains, state.Scope.Capture().TotalRetains);
        Assert.Equal(initial.LiveTokens, state.Scope.Capture().LiveTokens);
        Assert.Equal(.5, state.Cache.GetSegment(state.SegmentId)!.SafeStartSeconds);
    }

    [Fact]
    public async Task 隣不足の需要は同じstreamを再利用し現在区間の全窓取得を増やさない()
    {
        const int FrameCount = 4;
        const int TargetIndex = 2;
        await using var state = new StreamState(new[] { 0d, .5, 1, 2 });
        Task paused = state.Sink.BeforeReadAsync(state.Cancellation.Token).AsTask();
        Assert.False(paused.IsCompleted);
        state.Set("IsPaused", true);
        state.Set("LastFrameBytes", (long)FrameBytes);
        var request = state.Request with { CorrelationId = 2, TargetSeconds = 1 };
        Y4mFrame? selected = null;
        state.Controller.OwnedFrameReady += envelope =>
        {
            selected = envelope.FrameReady!.Frame;
            Assert.False(state.Scope.Capture().OwnerKinds.ContainsKey("StreamCacheRead"));
            Assert.Equal(1, envelope.FrameReady!.Request.TargetSeconds);
        };
        var initial = state.Scope.Capture();
        // cache通知の所有とは分け、隣不足の明示的な再利用経路だけを測る。
        bool reused = (bool)typeof(PreviewController).GetMethod("TrySubmitStreaming", Flags)!
            .Invoke(state.Controller, new object[] { request, true, true, true, CancellationToken.None, true })!;
        Assert.True(reused);
        Assert.Same(state.Session, typeof(PreviewController).GetField("_stream", Flags)!.GetValue(state.Controller));
        Assert.Equal(request, state.Get<PreviewRequest>("LatestRequest"));
        Assert.False(state.Get<bool>("IsPaused"));
        Assert.False(state.Cancellation.IsCancellationRequested);
        Assert.Same(state.Cache.GetSegment(state.SegmentId)!.Frames[TargetIndex], selected);
        Assert.Equal(Origin + 1, selected!.Xts);
        // 通知で使う選択一枚だけを所有し、現在区間も公開用の親窓も固定しない。
        Assert.Equal(FrameCount, state.Cache.GetSegment(state.SegmentId)!.Frames.Count);
        Assert.Equal(initial.TotalRetains + 1, state.Scope.Capture().TotalRetains);
        Assert.Equal(initial.LiveTokens, state.Scope.Capture().LiveTokens);
        await paused.WaitWithDiagnosticsAsync(Safety);
        var after = state.Scope.Capture();
        Assert.True(state.Sink.BeforeReadAsync(state.Cancellation.Token).IsCompletedSuccessfully);
        Assert.Equal(after.TotalRetains, state.Scope.Capture().TotalRetains);
        Assert.True(state.Get<TaskCompletionSource>("Demand").Task.IsCompletedSuccessfully);
    }

    private sealed class StreamState : IAsyncDisposable
    {
        internal RawOwnershipScope Scope { get; } = new();
        internal FrameCache Cache { get; }
        internal PreviewController Controller { get; }
        internal PreviewRequest Request { get; } = new()
        {
            EncoderPath = "fake", InputFile = "input", Fps = 30, TargetSeconds = 0,
            Capabilities = new(TimestampPassthrough: true), TimestampOriginSeconds = Origin,
            Continuous = true, DependsOnHistory = false,
        };
        internal CancellationTokenSource Cancellation { get; } = new();
        internal long SegmentId { get; }
        internal object Session { get; }
        internal IRawOwnedSink Sink { get; }
        private readonly IDisposable _watchdog;
        internal StreamState(double[] seconds, long capacity = DefaultCapacityBytes)
        {
            Cache = new FrameCache(Scope, capacity);
            var backend = new UnusedBackend();
            Controller = new PreviewController(Scope, backend, backend, Cache, debounce: TimeSpan.Zero);
            SegmentId = Cache.BeginSegment(Request, new(Origin, 0, true), 0);
            long run = Scope.NewRunId();
            foreach (double second in seconds)
            {
                using var frame = Scope.Create(Header, Origin + second, 1d / 30, new byte[FrameBytes], run, "TestReader");
                Assert.True(Cache.Append(SegmentId, frame.Frame));
            }
            // 実時間やプロセスの進行に左右されず、cacheの予算と需要の遷移そのものを検証する。
            var sessionType = typeof(PreviewController).GetNestedType("StreamingSession", BindingFlags.NonPublic)!;
            Session = Activator.CreateInstance(sessionType, Flags, null,
                new object[] { Request, Request, 1L, true, Cancellation, CancellationToken.None }, null)!;
            Set("SegmentId", SegmentId);
            Set("Satisfied", true);
            typeof(PreviewController).GetField("_stream", Flags)!.SetValue(Controller, Session);
            typeof(PreviewController).GetField("_version", Flags)!.SetValue(Controller, 1L);
            var watchdogType = typeof(PreviewController).GetNestedType("OutputWatchdog", BindingFlags.NonPublic)!;
            _watchdog = (IDisposable)Activator.CreateInstance(watchdogType, Flags, null,
                new object[] { new HWEncVppPreview.Tests.Testing.FakeTimeProvider(), WatchdogTimeout }, null)!;
            var sinkType = typeof(PreviewController).GetNestedType("StreamSink", BindingFlags.NonPublic)!;
            Sink = (IRawOwnedSink)Activator.CreateInstance(sinkType, Flags, null,
                new object[] { Controller, Session, _watchdog }, null)!;
        }
        internal void Set(string name, object value) => Session.GetType().GetProperty(name)!.SetValue(Session, value);
        internal T Get<T>(string name) => (T)Session.GetType().GetProperty(name)!.GetValue(Session)!;
        internal void SignalRead() => Get<TaskCompletionSource>("ReadAllowed").TrySetResult();
        public async ValueTask DisposeAsync()
        {
            Cancellation.Cancel();
            SignalRead();
            typeof(PreviewController).GetField("_stream", Flags)!.SetValue(Controller, null);
            _watchdog.Dispose();
            foreach (var registration in Get<List<CancellationTokenRegistration>>("Registrations")) registration.Dispose();
            await Controller.DisposeAsync();
            Cache.Clear();
            Cancellation.Dispose();
            Assert.True(Scope.Capture().IsEmpty);
            Assert.Equal(Scope.Capture().TotalRetains, Scope.Capture().TotalTokenReleases);
        }
    }

    private sealed class UnusedBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("metadataの判定で新しい実行を開始してはいけない。");
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

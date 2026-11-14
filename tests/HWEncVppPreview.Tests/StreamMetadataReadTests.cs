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

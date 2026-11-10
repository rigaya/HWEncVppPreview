using System.Reflection;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class CoreNotificationSelectionTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int SelectedIndex = 1;
    private const int FrameCount = 4;
    private const int FrameBytes = 6;
    private const long CapacityBytes = 4096;
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(30);
    private const double Origin = 1000;
    private static readonly Y4mHeader Header = new() { Width = 2, Height = 2, FpsNum = 30, FpsDen = 1 };

    [Fact]
    public async Task 正IDのcache通知は全窓でなく採用一枚だけを取得しcallback中のevictにも耐える()
    {
        await using var state = new State();
        var initial = state.Scope.Capture();
        int frames = 0, segments = 0, completed = 0;
        state.Controller.OwnedFrameReady += envelope =>
        {
            ++frames;
            Assert.Same(state.Frames[SelectedIndex], envelope.FrameReady!.Frame);
            Assert.Equal(initial.TotalRetains + 1, state.Scope.Capture().TotalRetains);
            // 同期購読者が所属を消しても、通知に必要な一枚だけが残り、親全列は残らない。
            state.Cache.Clear();
            Assert.Equal(1, state.Scope.Capture().LiveOwners);
            Assert.Equal(1, state.Scope.Capture().LiveTokens);
            Assert.Equal(FrameBytes, envelope.FrameReady.Frame.RawPixels.Length);
            foreach (var frame in state.Frames.Where((_, index) => index != SelectedIndex))
                Assert.Throws<ObjectDisposedException>(() => frame.RawPixels);
        };
        state.Controller.OwnedSegmentUpdated += _ => ++segments;
        state.Controller.OwnedCompleted += _ => ++completed;
        Assert.True(state.Publish());
        Assert.Equal(1, frames);
        Assert.Equal(1, segments);
        Assert.Equal(1, completed);
        Assert.Equal(initial.TotalRetains + 1, state.Scope.Capture().TotalRetains);
        Assert.True(state.Scope.Capture().IsEmpty);
        Assert.Equal(1L, Field(state.Controller, "_lastNotifiedVersion"));
    }

    [Fact]
    public async Task 解放済みの正ID候補はcache通知を全て抑止して通知世代も確定しない()
    {
        await using var state = new State();
        int notifications = 0;
        state.Controller.OwnedFrameReady += _ => ++notifications;
        state.Controller.OwnedSegmentUpdated += _ => ++notifications;
        state.Controller.OwnedCompleted += _ => ++notifications;
        state.Cache.Clear();
        Assert.Throws<ObjectDisposedException>(() => state.Frames[SelectedIndex].RawPixels);
        var initial = state.Scope.Capture();
        object? version = Field(state.Controller, "_lastNotifiedVersion");
        Assert.False(state.Publish());
        Assert.Equal(0, notifications);
        Assert.Equal(version, Field(state.Controller, "_lastNotifiedVersion"));
        Assert.True(state.Scope.Capture().TryRetainAttempts > initial.TryRetainAttempts);
        Assert.Equal(initial.TotalRetains, state.Scope.Capture().TotalRetains);
        Assert.True(state.Scope.Capture().IsEmpty);
    }

    [Fact]
    public async Task stream需要の確定も通知一枚だけを取得してから行う()
    {
        await using var state = new State();
        object session = state.InstallSession();
        var demand = (TaskCompletionSource)session.GetType().GetProperty("Demand")!.GetValue(session)!;
        var initial = state.Scope.Capture();
        int notifications = 0;
        state.Controller.OwnedFrameReady += envelope =>
        {
            ++notifications;
            Assert.Same(state.Frames[SelectedIndex], envelope.FrameReady!.Frame);
            Assert.Equal(initial.TotalRetains + 1, state.Scope.Capture().TotalRetains);
            state.Cache.Clear();
            Assert.Equal(1, state.Scope.Capture().LiveOwners);
            Assert.Equal(FrameBytes, envelope.FrameReady.Frame.RawPixels.Length);
        };
        Invoke(state.Controller, "TryPublishDemand", session, false);
        Assert.Equal(1, notifications);
        Assert.True(demand.Task.IsCompletedSuccessfully);
        Assert.True((bool)session.GetType().GetProperty("Satisfied")!.GetValue(session)!);
        Assert.Equal(1L, session.GetType().GetProperty("LastNotifiedVersion")!.GetValue(session));
        Assert.Equal(initial.TotalRetains + 1, state.Scope.Capture().TotalRetains);
        Assert.True(state.Scope.Capture().IsEmpty);
    }

    [Fact]
    public async Task streamの選択取得失敗は通知世代や需要の成功を確定しない()
    {
        await using var state = new State();
        object session = state.InstallSession();
        var type = session.GetType();
        var demand = (TaskCompletionSource)type.GetProperty("Demand")!.GetValue(session)!;
        object? notified = type.GetProperty("LastNotifiedVersion")!.GetValue(session);
        object? globalNotified = Field(state.Controller, "_lastNotifiedVersion");
        Assert.False((bool)type.GetProperty("Satisfied")!.GetValue(session)!);
        Assert.False(demand.Task.IsCompleted);
        var frame = new PreviewFrameResult(state.Request, state.Frames[SelectedIndex], false) { Segment = state.Segment };
        int notifications = 0;
        state.Controller.OwnedFrameReady += _ => ++notifications;
        state.Cache.Clear();
        Assert.Throws<ObjectDisposedException>(() => frame.Frame.RawPixels);
        var initial = state.Scope.Capture();
        // 選択後のevictを直接入力で再現し、OSの待機や実行順の偶然に依存しない。
        bool published = (bool)Invoke(state.Controller, "TryPublishSelectedFrame", session, frame, true)!;
        Assert.False(published);
        Assert.Equal(0, notifications);
        Assert.Equal(globalNotified, Field(state.Controller, "_lastNotifiedVersion"));
        Assert.Equal(notified, type.GetProperty("LastNotifiedVersion")!.GetValue(session));
        Assert.False((bool)type.GetProperty("Satisfied")!.GetValue(session)!);
        Assert.False(demand.Task.IsCompleted);
        Assert.True(state.Scope.Capture().TryRetainAttempts > initial.TryRetainAttempts);
        Assert.Equal(initial.TotalRetains, state.Scope.Capture().TotalRetains);
        Assert.True(state.Scope.Capture().IsEmpty);
    }

    [Fact]
    public async Task 所属消失中の需要公開は失敗しpause通知から既存終了処理へ戻る()
    {
        await using var state = new State();
        object session = state.InstallSession();
        var type = session.GetType();
        var demand = (TaskCompletionSource)type.GetProperty("Demand")!.GetValue(session)!;
        object? notified = type.GetProperty("LastNotifiedVersion")!.GetValue(session);
        object? globalNotified = Field(state.Controller, "_lastNotifiedVersion");
        int notifications = 0;
        state.Controller.OwnedFrameReady += _ => ++notifications;
        state.Controller.OwnedSegmentUpdated += _ => ++notifications;
        state.Controller.OwnedCompleted += _ => ++notifications;
        state.Cache.Clear();
        Assert.False((bool)Invoke(state.Controller, "TryPublishDemand", session, false)!);
        Assert.False((bool)Invoke(state.Controller, "TryPublishDemand", session, true)!);
        var watchdogType = typeof(PreviewController).GetNestedType("OutputWatchdog", BindingFlags.NonPublic)!;
        using var watchdog = (IDisposable)Activator.CreateInstance(watchdogType, Flags, null,
            new object[] { new HWEncVppPreview.Tests.Testing.FakeTimeProvider(), WatchdogTimeout }, null)!;
        var sinkType = typeof(PreviewController).GetNestedType("StreamSink", BindingFlags.NonPublic)!;
        var sink = (IRawOwnedSink)Activator.CreateInstance(sinkType, Flags, null,
            new object[] { state.Controller, session, watchdog }, null)!;
        // pauseへ移ったまま待ち続けず、backendのfinallyへ戻せる例外として伝える。
        Assert.Throws<InvalidOperationException>(() => sink.OnReadStateChanged(false));
        Assert.Equal(0, notifications);
        Assert.Equal(globalNotified, Field(state.Controller, "_lastNotifiedVersion"));
        Assert.Equal(notified, type.GetProperty("LastNotifiedVersion")!.GetValue(session));
        Assert.False((bool)type.GetProperty("Satisfied")!.GetValue(session)!);
        Assert.False(demand.Task.IsCompleted);
        Assert.True(state.Scope.Capture().IsEmpty);
        // 意図的な取消後は所属が消えても新たな失敗にせず、既存finallyが需要を回収できるままにする。
        ((CancellationTokenSource)type.GetProperty("Cancellation")!.GetValue(session)!).Cancel();
        Assert.True((bool)Invoke(state.Controller, "TryPublishDemand", session, false)!);
        Assert.True((bool)Invoke(state.Controller, "TryPublishDemand", session, true)!);
        sink.OnReadStateChanged(false);
        Assert.Equal(0, notifications);
        Assert.Equal(globalNotified, Field(state.Controller, "_lastNotifiedVersion"));
        Assert.Equal(notified, type.GetProperty("LastNotifiedVersion")!.GetValue(session));
        Assert.False((bool)type.GetProperty("Satisfied")!.GetValue(session)!);
        Assert.False(demand.Task.IsCompleted);
        Assert.True(state.Scope.Capture().IsEmpty);
    }

    [Fact]
    public async Task 初出力前の未所属需要を所属消失と混同しない()
    {
        await using var state = new State();
        object session = state.InstallSession();
        session.GetType().GetProperty("SegmentId")!.SetValue(session, 0L);
        state.Cache.Clear();
        Assert.True((bool)Invoke(state.Controller, "TryPublishDemand", session, false)!);
        Assert.False(((TaskCompletionSource)session.GetType().GetProperty("Demand")!.GetValue(session)!).Task.IsCompleted);
        Assert.False((bool)session.GetType().GetProperty("Satisfied")!.GetValue(session)!);
        Assert.True(state.Scope.Capture().IsEmpty);
    }

    [Fact]
    public async Task pause中の旧需要確定で所属が消えても新streamを一度起動して需要を取り残さない()
    {
        var backend = new ControlledBackend();
        await using var state = new State(backend, activeSegment: true);
        object session = state.InstallSession();
        var type = session.GetType();
        type.GetProperty("IsPaused")!.SetValue(session, true);
        type.GetProperty("LastFrameBytes")!.SetValue(session, (long)FrameBytes);
        type.GetProperty("Satisfied")!.SetValue(session, true);
        var oldDemand = new TaskCompletionSource();
        type.GetProperty("Demand")!.SetValue(session, oldDemand);
        // compatible判定後の旧需要確定に同期して消すため、OSの待機なしでpauseの競合を再現できる。
        Task removed = oldDemand.Task.ContinueWith(_ => state.Cache.Clear(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var request = state.Request with { CorrelationId = 2 };
        Assert.True((bool)Invoke(state.Controller, "TrySubmitStreaming", request, true, true, true,
            CancellationToken.None, true)!);
        await removed.WaitWithDiagnosticsAsync(Safety);
        await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        Assert.True(((CancellationTokenSource)type.GetProperty("Cancellation")!.GetValue(session)!).IsCancellationRequested);
        Assert.True(((TaskCompletionSource)type.GetProperty("ReadAllowed")!.GetValue(session)!).Task.IsCompletedSuccessfully);
        Assert.True(((TaskCompletionSource)type.GetProperty("Demand")!.GetValue(session)!).Task.IsCompletedSuccessfully);
        Assert.NotSame(session, Field(state.Controller, "_stream"));
        Assert.Equal(1, backend.RunCount);
    }

    private sealed class State : IAsyncDisposable
    {
        internal RawOwnershipScope Scope { get; } = new();
        internal FrameCache Cache { get; }
        internal PreviewController Controller { get; }
        internal PreviewSegmentSnapshot Segment { get; }
        internal Y4mFrame[] Frames { get; }
        internal PreviewRequest Request { get; } = new()
        {
            EncoderPath = "fake", InputFile = "input", TargetSeconds = 1, Fps = 30, Continuous = true,
            Capabilities = new(TimestampPassthrough: true), TimestampOriginSeconds = Origin, DependsOnHistory = false,
        };
        private readonly CancellationTokenSource _cancellation = new();
        private object? _session;
        internal State(IPreviewBackend? suppliedBackend = null, bool activeSegment = false)
        {
            Cache = new FrameCache(Scope, CapacityBytes);
            var backend = suppliedBackend ?? new UnusedBackend();
            Controller = new PreviewController(Scope, backend, backend, Cache, debounce: TimeSpan.Zero);
            long run = Scope.NewRunId();
            var producers = Enumerable.Range(0, FrameCount).Select(index => Scope.Create(Header, Origin + index,
                1d / 30, new byte[FrameBytes], run, "TestReader")).ToArray();
            try
            {
                Frames = producers.Select(producer => producer.Frame).ToArray();
                if (activeSegment)
                {
                    long id = Cache.BeginSegment(Request, new(Origin, 0, true), 0);
                    foreach (var frame in Frames) Assert.True(Cache.Append(id, frame));
                    Segment = Cache.GetSegment(id)!;
                }
                else Segment = Cache.StoreSegment(Request, Frames, new(Origin, 0, true), 0, FrameCount)!;
                Assert.NotNull(Segment);
            }
            finally { foreach (var producer in producers) producer.Dispose(); }
            SetField(Controller, "_version", 1L);
        }
        internal bool Publish() => (bool)Invoke(Controller, "PublishSegmentSelection", Request, Segment, 1L,
            true, false, 0L, CancellationToken.None, true)!;
        internal object InstallSession()
        {
            var type = typeof(PreviewController).GetNestedType("StreamingSession", BindingFlags.NonPublic)!;
            _session = Activator.CreateInstance(type, Flags, null,
                new object[] { Request, Request, 1L, true, _cancellation, CancellationToken.None }, null)!;
            type.GetProperty("SegmentId")!.SetValue(_session, Segment.SegmentId);
            SetField(Controller, "_stream", _session);
            return _session;
        }
        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            if (ReferenceEquals(Field(Controller, "_stream"), _session)) SetField(Controller, "_stream", null);
            if (_session != null)
            {
                foreach (var registration in (List<CancellationTokenRegistration>)_session.GetType()
                    .GetProperty("Registrations")!.GetValue(_session)!) registration.Dispose();
            }
            await Controller.DisposeAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
            Cache.Clear();
            _cancellation.Dispose();
            Assert.True(Scope.Capture().IsEmpty);
            Assert.Equal(Scope.Capture().TotalRetains, Scope.Capture().TotalTokenReleases);
        }
    }
    private sealed class ControlledBackend : IPreviewBackend, IRawOwnedBackend
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;
        internal int RunCount => Volatile.Read(ref _runs);
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("この回帰ではownedのstreamだけを起動する。");
        public async Task<PreviewRunResult> RunOwnedAsync(PreviewRequest request, IRawOwnedSink sink,
            RawOwnershipScope scope, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runs);
            Started.TrySetResult();
            try { await _release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            return new(0, "", IsCancelled: true);
        }
        public ValueTask AbortAsync() { _release.TrySetResult(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { _release.TrySetResult(); return ValueTask.CompletedTask; }
    }
    private sealed class UnusedBackend : IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("この試験は通知だけを同期実行するためbackendを起動しない。");
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static object? Field(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target);
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
}

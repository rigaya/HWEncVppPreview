using System.Collections.Concurrent;
using System.Threading.Channels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class ForwardPrefetchTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;
    private static PreviewRequest Request(double target = 10, long id = 1) => new()
    {
        EncoderPath = "forward-stub", InputFile = "input", TargetSeconds = target, CorrelationId = id,
        Fps = 10, Continuous = true, TimestampOriginSeconds = 1000, Capabilities = new(true), InputDurationSeconds = 100,
    };
    private static Y4mFrame Frame(double seconds) => new(new Y4mHeader { Width = 2, Height = 2 },
        1000 + seconds, 0.1, Enumerable.Repeat((byte)((int)Math.Round(seconds * 10) % 251), 6).ToArray());
    private static long Seed(FrameCache cache, PreviewRequest request) => cache.StoreSegment(request,
        Enumerable.Range(90, 40).Select(i => Frame(i / 10d)).ToArray(), new(1000, 9, true), 9, 13)!.SegmentId;

    [Fact]
    public async Task 終端手前で通常順streamを一度だけ開始し表示とpinを動かさない()
    {
        var cache = new FrameCache(); long current = Seed(cache, Request());
        var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        int images = 0, completed = 0, failed = 0;
        controller.FrameReady += _ => ++images; controller.Completed += _ => ++completed; controller.Failed += _ => ++failed;
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1);
        await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(13, backend.Requests.Single().TargetSeconds);
        Assert.True(backend.Requests.Single().Continuous);
        Assert.Equal(12, CommandBuilder.Plan(backend.Requests.Single()).SeekSeconds);
        Assert.Equal(1, images); Assert.Equal(1, completed); Assert.Equal(0, failed);
        for (int i = 0; i < 3; i++) controller.NotifyNavigation(Request(11.6 + i / 10d), 1);
        Assert.Single(backend.Requests);
        // 現表示区間は先読み開始ではなく、実際のcache移動でのみ切り替わる。
        Assert.NotNull(cache.GetSegment(current));
        Assert.True(cache.GetConnectedSafeRange(Request(10))!.Value.End > 13);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task 接続範囲内の微小順逆移動はstreamを保持する(int direction)
    {
        var cache = new FrameCache(); Seed(cache, Request()); var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1); await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        var logical = Request(11.5 + direction * 0.1, 2);
        controller.Submit(logical, navigation: true); controller.NotifyNavigation(logical, direction);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Single(backend.Requests); Assert.Equal(0, backend.Cancelled);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("reverse")]
    [InlineData("content")]
    [InlineData("legacy")]
    [InlineData("eof")]
    [InlineData("unstitch")]
    public async Task 起動条件外では実行しない(string reason)
    {
        var request = reason == "legacy" ? Request() with { TimestampOriginTrusted = false } : Request();
        if (reason == "eof") request = request with { InputDurationSeconds = 13 };
        var cache = new FrameCache(); Seed(cache, request); var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        // legacyは通常Submitすると既存の有限取り直しへ進むため、foregroundには正常sourceを登録する。
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        if (reason == "unstitch") cache.InvalidateSource(request);
        var move = reason switch { "outside" => request with { TargetSeconds = 11.4 },
            "content" => request with { TargetSeconds = 11.5, InputFile = "other" }, _ => request with { TargetSeconds = 11.5 } };
        controller.NotifyNavigation(move, reason == "reverse" ? -1 : 1);
        Assert.Empty(backend.Requests);
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("content")]
    [InlineData("jump")]
    [InlineData("near_jump")]
    [InlineData("island")]
    public async Task 明示再取得と内容変更と範囲外jumpは取消する(string reason)
    {
        var cache = new FrameCache(); Seed(cache, Request()); var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1); await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        if (reason == "island") cache.StoreSegment(Request(17), new[] { Frame(17), Frame(17.1) }, new(1000, 17, true), 17, 17.2);
        var request = reason switch { "content" => Request(11.6, 2) with { InputFile = "other" },
            "jump" => Request(30, 2), "near_jump" => Request(16, 2), "island" => Request(17, 2), _ => Request(11.6, 2) };
        controller.Submit(request, bypassCache: reason == "refresh", navigation: true);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(1, backend.Cancelled);
        Assert.Equal(reason == "island" ? 1 : 2, backend.Requests.Count);
        Assert.Equal(1, backend.MaxConcurrent);
    }

    [Fact]
    public async Task 接続後の境界隣需要は同じstreamへ合流する()
    {
        var cache = new FrameCache(); Seed(cache, Request()); var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        var image = new TaskCompletionSource<PreviewFrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.FrameReady += frame => { if (frame.Request.CorrelationId == 2) image.TrySetResult(frame); };
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1); await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.Submit(Request(13.1, 2), navigation: true);
        var frame = await image.Task.WaitWithDiagnosticsAsync(Safety); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(1013.1, frame.Frame.Xts); Assert.Single(backend.Requests); Assert.Equal(0, backend.Cancelled);
    }

    [Fact]
    public async Task 実逆先読みが枠を要求して初めて順先読みを取消する()
    {
        var cache = new FrameCache(); Seed(cache, Request()); var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1); await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.Submit(Request(9.4, 2), navigation: true); controller.NotifyNavigation(Request(9.4, 2), -1);
        Assert.Equal(0, backend.Cancelled);
        controller.Submit(Request(9.3, 3), navigation: true); controller.NotifyNavigation(Request(9.3, 3), -1);
        var pending = controller.GetPendingReversePrefetch(Request(9.3, 3)); Assert.NotNull(pending);
        await pending!.WaitWithDiagnosticsAsync(Safety); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(1, backend.Cancelled); Assert.Equal(2, backend.Requests.Count);
        Assert.False(backend.Requests.Last().Continuous); Assert.Equal(1, backend.MaxConcurrent);
    }

    [Fact]
    public async Task 容量不足の先読みpauseでも表示pinとWaitForIdleを保つ()
    {
        var cache = new FrameCache(300); long current = Seed(cache, Request()); var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1); await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.NotNull(cache.GetSegment(current)); Assert.InRange(cache.SizeBytes, 240, 300);
        Assert.Single(backend.Requests); Assert.Equal(0, backend.Cancelled);
    }

    [Fact]
    public async Task 順先読み中のBeforeは別backendで通常完了する()
    {
        var cache = new FrameCache(); Seed(cache, Request()); var backend = new Backend(); var before = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero, backgroundBackend: before);
        var completed = new ConcurrentQueue<PreviewCompletion>(); controller.Completed += completed.Enqueue;
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1); await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        var request = Request(11.5, -2) with { Continuous = false, VppArgs = new[] { "before" } };
        controller.Submit(request, background: true, bypassCache: true);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Single(before.Requests); Assert.Single(backend.Requests);
        Assert.Contains(completed, x => x.Request.CorrelationId == -2);
        Assert.DoesNotContain(completed, x => x.Request.TargetSeconds == 13);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("clock")]
    [InlineData("eof")]
    public async Task 先読み失敗や自然終了は既存表示と完了通知を上書きしない(string result)
    {
        var cache = new FrameCache(); long current = Seed(cache, Request()); var backend = new Backend(result);
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        int images = 0, completed = 0, failed = 0;
        controller.FrameReady += _ => ++images; controller.Completed += _ => ++completed; controller.Failed += _ => ++failed;
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1); await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(1, images); Assert.Equal(1, completed); Assert.Equal(0, failed);
        Assert.NotNull(cache.GetSegment(current));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 逆有限窓由来の余白と固定seekは通常順windowへ戻す(bool history)
    {
        var request = Request() with { Continuous = false, PreMarginSeconds = 5, PostMarginSeconds = 4, SeekSecondsOverride = 5 };
        var cache = new FrameCache(); var backend = new Backend(history: history);
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        // 有限の逆方向窓を実行・保存してから、同じ窓内を順方向へ戻る。
        controller.Submit(request, navigation: true); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.False(Assert.Single(backend.Requests).Continuous);
        var range = cache.GetConnectedSafeRange(request); Assert.NotNull(range);
        double boundary = range!.Value.End;
        controller.NotifyNavigation(request with { TargetSeconds = boundary - 1 }, 1);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, backend.Requests.Count); var physical = backend.Requests.Last();
        Assert.True(physical.Continuous); Assert.Equal(boundary, physical.TargetSeconds);
        Assert.Equal(boundary - (history ? 3 : 1), CommandBuilder.Plan(physical).SeekSeconds!.Value, 7);
        Assert.Equal(0.5, physical.PostMarginSeconds);
        Assert.True(FrameCache.SameContent(request, physical));
        var normalBackend = new Backend(history: history);
        await using var normal = new PreviewController(normalBackend, debounce: TimeSpan.Zero);
        normal.Submit(Request(boundary)); await normalBackend.Started.Task.WaitWithDiagnosticsAsync(Safety); await normal.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        var regular = Assert.Single(normalBackend.Requests);
        Assert.Equal(CommandBuilder.Plan(regular), CommandBuilder.Plan(physical));
    }

    [Fact]
    public async Task activeがあれば順先読みを追加しない()
    {
        var backend = new Backend();
        await using var controller = new PreviewController(backend, debounce: TimeSpan.Zero);
        controller.Submit(Request()); await backend.Started.Task.WaitWithDiagnosticsAsync(Safety); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1);
        Assert.Single(backend.Requests); Assert.Equal(0, backend.Cancelled);
    }

    [Fact]
    public async Task 旧終端の中心需要は隣待ち合流し通常stream状態へ移る()
    {
        var cache = new FrameCache(); Seed(cache, Request()); var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        var images = new ConcurrentQueue<PreviewFrameResult>(); controller.FrameReady += images.Enqueue;
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1); await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        // 旧区間の最後の中心を使い、未知の隣時刻はCoreへ予測して渡さない。
        controller.Submit(Request(12.9, 2), bypassCache: true, navigation: true, reuseActiveStream: true);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Contains(images, x => x.Request.CorrelationId == 2 && x.Frame.Xts == 1012.9);
        var view = cache.GetNavigationFrames(Request(12.9));
        Assert.Contains(view, x => Math.Abs(x.InputSeconds - 13) < 1e-7);
        controller.Submit(Request(13.1, 3), navigation: true); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Contains(images, x => x.Request.CorrelationId == 3 && x.Frame.Xts == 1013.1);
        // 合流後は先読み専用guardを外し、既存の通常小jump許可へ戻っていることも確認する。
        controller.Submit(Request(16, 4), navigation: true); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Contains(images, x => x.Request.CorrelationId == 4 && x.Frame.Xts == 1016);
        Assert.Single(backend.Requests); Assert.Equal(0, backend.Cancelled);
    }

    [Fact]
    public async Task 未解決の有限前景があれば先読みは追加しない()
    {
        var cache = new FrameCache(); Seed(cache, Request()); var backend = new Backend("hold");
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request() with { Continuous = false }, bypassCache: true, navigation: true);
        await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1);
        Assert.Single(backend.Requests); Assert.False(backend.Requests.Single().Continuous);
        backend.Release.TrySetResult(); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task 逆先読みで取消した順先読みは同じ境界で再起動する()
    {
        // byte窓のための未来tail削減とは分け、同じ境界の取消markerだけを検証する。
        var cache = new FrameCache(); Seed(cache, Request()); var backend = new Backend(holdAtSeedEnd: true, holdFinite: true);
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1);
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        double boundary = cache.GetConnectedSafeRange(Request(11.5))!.Value.End;
        Assert.Equal(13, boundary);
        Assert.Single(backend.Requests);

        controller.Submit(Request(10.4, 2), navigation: true); controller.NotifyNavigation(Request(10.4, 2), -1);
        controller.Submit(Request(10.3, 3), navigation: true); controller.NotifyNavigation(Request(10.3, 3), -1);
        var pending = controller.GetPendingReversePrefetch(Request(10.3, 3)); Assert.NotNull(pending);
        // 完了した先読みはpending APIに現れないため、待機taskを得てから有限backendを進める。
        backend.Release.TrySetResult();
        await pending!.WaitWithDiagnosticsAsync(Safety); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(1, backend.Cancelled); Assert.Equal(2, backend.Requests.Count);
        Assert.Equal(boundary, cache.GetConnectedSafeRange(Request(11.5))!.Value.End);

        controller.NotifyNavigation(Request(boundary - 1, 4), 1);
        // 再起動した順streamの読取pauseが実際に届くまで待つ。修正前はここでredになる。
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(3, backend.Requests.Count); Assert.True(backend.Requests.Last().Continuous);
        Assert.Equal(boundary, backend.Requests.Last().TargetSeconds); Assert.Equal(1, backend.MaxConcurrent);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("clock")]
    [InlineData("empty_eof")]
    public async Task 失敗または自然終了した順先読みは同じ境界で再発行しない(string result)
    {
        // Backendへempty_eofなら出力前にnew(0, "")を返す1分岐を加える。
        var cache = new FrameCache(); Seed(cache, Request()); var backend = new Backend(result);
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1); await backend.Started.Task.WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        for (int i = 0; i < 3; i++) controller.NotifyNavigation(Request(11.6 + i / 10d), 1);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Single(backend.Requests); Assert.Equal(13, cache.GetConnectedSafeRange(Request(11.5))!.Value.End);
    }

    [Fact]
    public async Task 取消回収中の同境界通知は重複起動せず回収後だけ再起動する()
    {
        var cache = new FrameCache(); Seed(cache, Request()); var backend = new Backend(holdCancellation: true, holdAtSeedEnd: true);
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1);
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        double boundary = cache.GetConnectedSafeRange(Request(11.5))!.Value.End;
        Assert.Equal(13, boundary);
        Task? pending = null;
        try
        {
            controller.Submit(Request(10.4, 2), navigation: true); controller.NotifyNavigation(Request(10.4, 2), -1);
            controller.Submit(Request(10.3, 3), navigation: true); controller.NotifyNavigation(Request(10.3, 3), -1);
            pending = controller.GetPendingReversePrefetch(Request(10.3, 3)); Assert.NotNull(pending);
            await backend.CancellationObserved.Task.WaitWithDiagnosticsAsync(Safety);
            for (int i = 0; i < 3; i++) controller.NotifyNavigation(Request(boundary - 1, 4), 1);
            Assert.Single(backend.Requests);
        }
        finally { backend.FinishCancellation.TrySetResult(); }
        await pending!.WaitWithDiagnosticsAsync(Safety); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(2, backend.Requests.Count);
        controller.NotifyNavigation(Request(boundary - 1, 5), 1);
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Equal(3, backend.Requests.Count); Assert.Equal(1, backend.MaxConcurrent);
    }

    [Fact]
    public async Task 別起動の同値markerを古いstreamの取消で消さない()
    {
        var cache = new FrameCache(240); Seed(cache, Request()); var backend = new Backend();
        await using var controller = new PreviewController(backend, cache: cache, debounce: TimeSpan.Zero);
        controller.Submit(Request()); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        controller.NotifyNavigation(Request(11.5), 1);
        await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(Safety);
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        // 同値だが別参照の新markerを注入し、値比較や無条件clearでは保護できないケースを作る。
        var field = typeof(PreviewController).GetField("_lastForwardPrefetch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var previous = Assert.IsType<PreviewRequest>(field.GetValue(controller));
        var newer = previous with { };
        field.SetValue(controller, newer);
        controller.Submit(Request(10.4, 2), navigation: true); controller.NotifyNavigation(Request(10.4, 2), -1);
        controller.Submit(Request(10.3, 3), navigation: true); controller.NotifyNavigation(Request(10.3, 3), -1);
        var pending = controller.GetPendingReversePrefetch(Request(10.3, 3)); Assert.NotNull(pending);
        await pending!.WaitWithDiagnosticsAsync(Safety); await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(Safety);
        Assert.Same(newer, field.GetValue(controller));
        Assert.Equal(2, backend.Requests.Count); Assert.Equal(1, backend.Cancelled);
    }

    private sealed class Backend(string result = "stream", bool history = false, bool holdCancellation = false, bool holdAtSeedEnd = false, bool holdFinite = false) : IPreviewBackend
    {
        public ConcurrentQueue<PreviewRequest> Requests { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<double> Pauses { get; } = Channel.CreateUnbounded<double>();
        private int _cancelled, _active, _max;
        public int Cancelled => Volatile.Read(ref _cancelled);
        public int MaxConcurrent => Volatile.Read(ref _max);
        public Task<bool?> GetHistoryRequirementAsync(PreviewRequest request, CancellationToken token = default) => Task.FromResult<bool?>(history);
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken token = default)
        {
            Requests.Enqueue(request); int active = Interlocked.Increment(ref _active); _max = Math.Max(_max, active); Started.TrySetResult();
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0, last = seek;
            try
            {
                if (result == "hold" || holdFinite && !request.Continuous) await Release.Task.WaitAsync(token);
                if (result == "error") return new(1, "stub failure");
                if (result == "empty_eof") return new(0, "");
                for (int i = 0; request.Continuous || i < CommandBuilder.Plan(request).FrameCount; i++)
                {
                    var permission = sink.BeforeReadAsync(token);
                    if (!permission.IsCompleted) { sink.OnReadStateChanged(false); Pauses.Writer.TryWrite(last); }
                    await permission; sink.OnReadStateChanged(true); token.ThrowIfCancellationRequested();
                    last = seek + i / 10d; sink.OnOutput(); sink.OnFrame(Frame(result == "clock" ? 0 : last));
                    if (holdAtSeedEnd && request.Continuous && last >= 12.9 - 1e-8)
                    {
                        // byte容量とは独立に同じ境界でpauseさせ、取消markerの寿命だけを再現する。
                        sink.OnReadStateChanged(false); Pauses.Writer.TryWrite(last);
                        await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);
                    }
                    if (result == "eof" && i >= 30) return new(0, "");
                }
                return new(0, "");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                if (holdCancellation)
                {
                    CancellationObserved.TrySetResult();
                    await FinishCancellation.Task.WaitWithDiagnosticsAsync(Safety);
                }
                return new(-12, "", true);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

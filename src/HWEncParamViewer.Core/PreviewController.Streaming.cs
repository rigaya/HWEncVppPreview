namespace HWEncParamViewer.Core;

public sealed partial class PreviewController
{
    private StreamingSession? _stream;
    private readonly List<PreviewRequest> _untrustedClocks = new();
    private readonly List<(PreviewRequest Request, string Reason)> _legacyDiagnostics = new();
    private PreviewRequest? _foregroundSource;
    private TaskCompletionSource _streamStateChanged = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>範囲の更新。生のXtsは保持し、最新の論理要求へ通知する。</summary>
    /// <remarks>ハンドラはすぐに戻ること。UIスレッドを同期的に待たず、Postで非同期にディスパッチすること。</remarks>
    public event Action<PreviewSegmentUpdate>? SegmentUpdated;

    /// <summary>同じ論理要求をpassthroughなしの有限取得で取り直す直前に通知する。</summary>
    /// <remarks>ハンドラはすぐに戻ること。UIスレッドを同期的に待たず、Postで非同期にディスパッチすること。</remarks>
    public event Action<PreviewRequest>? ReplayStarted;

    /// <summary>長寿命の実行終了ではなく、現在の表示目標が確定するまで待つ。</summary>
    public Task WaitForCurrentDemandAsync()
    {
        lock (_sync) return _stream is { } stream && !stream.Cancellation.IsCancellationRequested
            ? stream.Demand.Task : Task.WhenAll(_tasks.ToArray());
    }

    private static PreviewClockMap ClockFor(PreviewRequest request, Y4mFrame first)
    {
        double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
        bool trusted = request.Capabilities.TimestampPassthrough
            && request.TimestampOriginSeconds is double origin && double.IsFinite(origin)
            && first.Xts is double timestamp && double.IsFinite(timestamp)
            && timestamp >= origin - 1e-6 && timestamp - origin >= seek - 0.5;
        // 原点0でも、seekより明らかに前へ戻るclockは他要求へ結合しない。
        return new(request.TimestampOriginSeconds, seek, trusted);
    }

    private bool KnownUntrusted(PreviewRequest request)
    {
        lock (_sync) return _untrustedClocks.Any(x => FrameCache.SameContent(x, request));
    }

    /// <summary>原点や要求内の異常により、このsourceをpassthroughなしの有限要求へ戻すか。</summary>
    public bool IsLegacySource(PreviewRequest request) => request.Capabilities.TimestampPassthrough
        && (!request.TimestampOriginTrusted
            || request.TimestampOriginSeconds is double origin && PreviewClockMap.IsNearTimestampWrap(origin)
            || KnownUntrusted(request));

    private void PrepareForegroundSource(PreviewRequest request)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_foregroundSource != null && FrameCache.SameContent(_foregroundSource, request)) return;
            CancelReversePrefetchCore();
            foreach (var source in _untrustedClocks) _cache.ResetSourceDiagnosis(source, discard: true);
            _untrustedClocks.Clear();
            _legacyDiagnostics.Clear();
            if (_foregroundSource != null) _cache.ResetSourceDiagnosis(_foregroundSource);
            _cache.ResetSourceDiagnosis(request);
            _foregroundSource = request;
        }
    }

    private string LegacyReason(PreviewRequest request)
    {
        lock (_sync)
        {
            if (request.TimestampOriginSeconds is double origin && PreviewClockMap.IsNearTimestampWrap(origin))
                return "入力原点が33bit/90k境界の前後10秒以内です";
            if (!request.TimestampOriginTrusted) return "入力原点の信頼性を確認できませんでした";
            return _legacyDiagnostics.FirstOrDefault(entry => FrameCache.SameContent(entry.Request, request)).Reason
                ?? "absolute時刻の信頼性を確認できませんでした";
        }
    }

    private PreviewRunResult WithLegacyDiagnostic(PreviewRequest request, PreviewRunResult result)
    {
        const string prefix = "[preview] timestamp fallback: ";
        if (!IsLegacySource(request) || result.StandardError.Contains(prefix, StringComparison.Ordinal)) return result;
        string key = $"{Path.GetFileName(request.EncoderPath)}; {Path.GetFileName(request.InputFile)}; vpp={string.Join(" ", request.VppArgs)}";
        string diagnostic = (prefix + LegacyReason(request) + "; " + key).Replace('\r', ' ').Replace('\n', ' ');
        return result with { StandardError = result.StandardError.TrimEnd('\r', '\n') + "\n" + diagnostic + "\n" };
    }

    private string? LegacyFailureDiagnostic(PreviewRequest request, Exception? exception = null, PreviewRunResult? stoppedResult = null)
    {
        if (!IsLegacySource(request)) return null;
        // 公開Resultを付けるとCLIの失敗区分が変わるため、stderrだけを追加診断として返す。
        var result = stoppedResult ?? new PreviewRunResult(-1, (exception as PreviewBackendException)?.StandardError ?? "");
        return WithLegacyDiagnostic(request, result).StandardError;
    }

    private void MarkLegacySource(PreviewRequest request, string? reason = null)
    {
        lock (_sync)
        {
            if (!_untrustedClocks.Any(source => FrameCache.SameContent(source, request)))
            { _untrustedClocks.Add(request); _legacyDiagnostics.Add((request, reason ?? "absolute時刻の信頼性を確認できませんでした")); }
            _cache.InvalidateSource(request);
        }
    }

    private Task? QueueLegacyReplay(PreviewRequest request, long version, bool navigation, bool background,
        long editVersion, CancellationToken token)
    {
        lock (_sync)
        {
            if (!IsRequestedCurrent(version, background, editVersion) || token.IsCancellationRequested) return null;
            MarkLegacySource(request);
            if (PerfTrace.Enabled) PerfTrace.Write("core.clock_replay", new { request.CorrelationId, version, background, reason = LegacyReason(request),
                encoder = request.EncoderPath, input = request.InputFile, vpp = request.VppArgs });
            var scheduled = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (background) _backgroundScheduled = scheduled;
            else
            {
                _scheduled = scheduled;
                if (_foregroundPending++ == 0) _foregroundIdle = NewSignal();
            }
            var completion = NewSignal();
            _tasks.Add(completion.Task);
            try
            {
                if (background) NotifyBackground(version, editVersion, () => ReplayStarted?.Invoke(request));
                else Notify(version, navigation, () => ReplayStarted?.Invoke(request), currentOnly: true);
            }
            finally
            {
                // ハンドラ例外でも、登録済みpromiseを置き去りにしない。
                _ = TrackAsync(ScheduleAsync(request, version, scheduled, bypassCache: true, bypassDebounce: true,
                    navigation, background, editVersion, token, isReplay: true), completion);
            }
            return completion.Task;
        }
    }

    private void PulseStreamState()
    {
        _streamStateChanged.TrySetResult();
        _streamStateChanged = NewSignal();
    }

    private void CancelStreamCore()
    {
        if (_stream is not { } stream) return;
        stream.Cancellation.Cancel();
        stream.ReadAllowed.TrySetResult();
        stream.Demand.TrySetResult();
        PulseStreamState();
    }

    private bool TrySubmitStreaming(PreviewRequest request, bool bypassCache, bool bypassDebounce,
        bool navigation, CancellationToken externalToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool capable = request.Continuous && request.Capabilities.TimestampPassthrough
                && request.TimestampOriginSeconds is double origin && double.IsFinite(origin) && !IsLegacySource(request);
            var active = _stream is { } existing && !existing.Cancellation.IsCancellationRequested ? existing : null;
            PreviewSegmentSnapshot? cached = null;
            if (!bypassCache && !IsLegacySource(request) && _cache.TryGetAt(request, request.TargetSeconds, out var available)) cached = available;

            bool compatible = active != null && navigation && !bypassCache
                && FrameCache.SameContent(active.PhysicalRequest, request);
            var current = active == null || active.SegmentId == 0 ? null : _cache.GetSegment(active.SegmentId);
            double frontier = current?.SafeEndSeconds ?? active?.LatestRequest.TargetSeconds ?? request.TargetSeconds;
            bool withinJump = active != null && request.TargetSeconds <= Math.Max(frontier, active.LatestRequest.TargetSeconds) + 3;
            bool notBeforeRetained = current == null ? active == null || request.TargetSeconds >= active.LatestRequest.TargetSeconds
                : request.TargetSeconds >= current.SafeStartSeconds - 1e-7;
            if (compatible && withinJump && notBeforeRetained && (capable || cached != null))
            {
                active!.Demand.TrySetResult();
                active.Demand = NewSignal();
                active.LatestRequest = request;
                active.Satisfied = false;
                active.Version = ++_version;
                active.Navigation = true;
                active.ExternalToken = externalToken;
                if (externalToken.CanBeCanceled) active.Registrations.Add(externalToken.Register(() => active.Cancellation.Cancel()));
                if (active.SegmentId != 0) _cache.PinCurrent(active.SegmentId, request.TargetSeconds);
                double received = current?.Frames.Count > 0
                    ? current.Clock.ToInputSeconds(current.Frames[^1], current.Frames.Count - 1, request.Fps) : double.NegativeInfinity;
                if (received < request.TargetSeconds + 2 - 1e-8)
                {
                    // 前回のpause状態を残したままgateを開くと、WaitIdleが再読取の前に戻ってしまう。
                    active.IsPaused = false;
                    PulseStreamState();
                    active.ReadAllowed.TrySetResult();
                }
                TryPublishDemand(active);
                return true;
            }

            if (active != null) CancelStreamCore();
            // 不一致後も今の物理stream内では動けるが、新しい要求へ旧区間を接続しない。
            if (!capable || !_cache.CanStitch(request)) return false;
            _scheduled?.Cancel();
            long version = ++_version;
            if (!navigation) { _lastEditVersion = version; _active?.Cancel(); _backgroundActive?.Cancel(); }
            if (cached != null && !bypassCache)
            {
                _cache.PinCurrent(cached.SegmentId, request.TargetSeconds);
                PublishSegmentSelection(request, cached, version, navigation, false, _lastEditVersion, externalToken, true);
                return true;
            }
            var physical = PhysicalAfterRequest(request, false) with { Continuous = true };
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            var scheduled = new CancellationTokenSource();
            _scheduled = scheduled;
            var session = new StreamingSession(physical, request, version, navigation, cancellation, externalToken);
            _stream = session;
            PulseStreamState();
            var completion = NewSignal();
            session.PhysicalCompletion = completion.Task;
            _tasks.Add(completion.Task);
            _ = TrackAsync(RunStreamAsync(session, scheduled, bypassDebounce || navigation), completion);
            return true;
        }
    }

    private void PublishSegmentSelection(PreviewRequest request, PreviewSegmentSnapshot segment, long version,
        bool navigation, bool background, long editVersion, CancellationToken token, bool fromCache)
    {
        if (token.IsCancellationRequested || segment.Frames.Count == 0) return;
        int selected = FrameSelector.Select(segment, request.TargetSeconds, request.Fps);
        if (selected < 0) return;
        void Emit()
        {
            FrameReady?.Invoke(new(request, segment.Frames[selected], false) { Segment = segment });
            SegmentUpdated?.Invoke(new(request, segment, true, true));
            Completed?.Invoke(new(request, new(0, ""), fromCache, false) { Frames = segment.Frames, Segment = segment });
        }
        if (background) NotifyBackground(version, editVersion, Emit);
        else Notify(version, navigation, Emit, currentOnly: true);
    }

    private async Task RunStreamAsync(StreamingSession session, CancellationTokenSource scheduled, bool bypassDebounce)
    {
        bool entered = false;
        IPreviewBackend backend;
        lock (_sync) backend = _unsupported.Contains(session.PhysicalRequest.EncoderPath) ? _fallback : _preferred;
        try
        {
            await Task.Delay(bypassDebounce ? TimeSpan.Zero : _debounce, _clock, scheduled.Token).ConfigureAwait(false);
            using var startToken = CancellationTokenSource.CreateLinkedTokenSource(scheduled.Token, session.Cancellation.Token);
            await _execution.WaitAsync(startToken.Token).ConfigureAwait(false);
            entered = true;
            session.Cancellation.Token.ThrowIfCancellationRequested();
            var physical = session.PhysicalRequest;
            double seek = CommandBuilder.Plan(physical).SeekSeconds ?? 0;
            var clock = new PreviewClockMap(physical.TimestampOriginSeconds, seek, true);
            // keyframe丸めで実暖機が短くなっても、計画seek+2を使い目標Tを表示可能にする近似。
            session.SegmentId = _cache.BeginSegment(physical, clock, seek > 0 ? seek + 2 : 0);
            _cache.PinCurrent(session.SegmentId, session.LatestRequest.TargetSeconds);
            using var watchdog = new OutputWatchdog(_clock, _watchdog);
            var sink = new StreamSink(this, session, watchdog);
            if (PerfTrace.Enabled) PerfTrace.Write("core.backend_start", new { physical.CorrelationId, version = session.Version,
                backend = backend.GetType().Name, continuous = physical.Continuous,
                timestampPassthrough = physical.Capabilities.TimestampPassthrough, isReplay = false });
            Task<PreviewRunResult> run = RunAsync();
            if (await Task.WhenAny(run, watchdog.Expired).ConfigureAwait(false) == watchdog.Expired)
            {
                session.Cancellation.Cancel();
                session.ReadAllowed.TrySetResult();
                await backend.AbortAsync().ConfigureAwait(false);
                PreviewRunResult? stoppedResult = null;
                Exception? stoppedException = null;
                try { stoppedResult = await run.ConfigureAwait(false); } catch (Exception error) { stoppedException = error; }
                _cache.RemoveSegment(session.SegmentId);
                lock (_sync)
                {
                    if (ReferenceEquals(_stream, session)) Notify(session.Version, session.Navigation,
                        () => Failed?.Invoke(new(session.LatestRequest, CoreText.OutputTimedOut)
                            { TimedOut = true, Diagnostic = LegacyFailureDiagnostic(session.LatestRequest, stoppedException, stoppedResult) }), true);
                }
                return;
            }
            PreviewRunResult result;
            try { result = await run.ConfigureAwait(false); }
            catch (OperationCanceledException) when (session.NeedsLegacyReplay)
            { result = new(-12, "", true); }
            if (session.NeedsLegacyReplay && !scheduled.IsCancellationRequested && !session.ExternalToken.IsCancellationRequested)
            {
                Task? replay = QueueLegacyReplay(session.LatestRequest, session.Version, session.Navigation,
                    false, _lastEditVersion, session.ExternalToken);
                if (entered) { _execution.Release(); entered = false; }
                if (replay != null) await replay.ConfigureAwait(false);
                return;
            }
            bool naturalEnd = result.ExitCode == 0 && !result.IsCancelled && !session.Cancellation.IsCancellationRequested;
            if (naturalEnd)
            {
                _cache.CompleteSegment(session.SegmentId, naturalEnd);
                lock (_sync)
                {
                    session.Ended = naturalEnd;
                    TryPublishDemand(session, finish: true);
                    if (ReferenceEquals(_stream, session) && session.LastNotifiedVersion != session.Version)
                        Notify(session.Version, session.Navigation, () => Failed?.Invoke(new(session.LatestRequest,
                            CoreText.FrameUnavailable, result)), true);
                    else if (ReferenceEquals(_stream, session) && _cache.GetSegment(session.SegmentId) is { } segment)
                        Notify(session.Version, session.Navigation, () => Completed?.Invoke(new(session.LatestRequest,
                            result, false, !session.Satisfied)
                            { Frames = segment.Frames, Segment = segment }), true);
                }
            }
            else if (!session.Cancellation.IsCancellationRequested && !result.IsCancelled)
            {
                _cache.RemoveSegment(session.SegmentId);
                Notify(session.Version, session.Navigation, () => Failed?.Invoke(new(session.LatestRequest,
                    CoreText.EncoderExited(result.ExitCode), result)), true);
            }

            async Task<PreviewRunResult> RunAsync()
            {
                try { return await RunTrackedBackendAsync(backend, session.PhysicalRequest, sink, session.Cancellation.Token).ConfigureAwait(false); }
                catch (PreviewServerNotSupportedException) when (_enableFallback && ReferenceEquals(backend, _preferred))
                {
                    lock (_sync) _unsupported.Add(session.PhysicalRequest.EncoderPath);
                    backend = _fallback;
                    watchdog.Touch();
                    return await RunTrackedBackendAsync(backend, session.PhysicalRequest, sink, session.Cancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (scheduled.IsCancellationRequested || session.Cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // 失敗したprefixの末尾を完成済み範囲として再利用しない。
            _cache.RemoveSegment(session.SegmentId);
            Notify(session.Version, session.Navigation, () => Failed?.Invoke(new(session.LatestRequest, exception.Message,
                Exception: exception) { Diagnostic = LegacyFailureDiagnostic(session.LatestRequest, exception) }), true);
        }
        finally
        {
            _cache.CompleteSegment(session.SegmentId, session.Ended);
            if (_cache.GetSegment(session.SegmentId) is { Clock.TrustedAbsolute: false })
            {
                _cache.RemoveSegment(session.SegmentId);
            }
            lock (_sync)
            {
                session.Demand.TrySetResult();
                if (ReferenceEquals(_stream, session)) _stream = null;
                if (ReferenceEquals(_scheduled, scheduled)) _scheduled = null;
                PulseStreamState();
            }
            foreach (var registration in session.Registrations) registration.Dispose();
            session.Cancellation.Dispose();
            scheduled.Dispose();
            if (entered) _execution.Release();
        }
    }

    private void TryPublishDemand(StreamingSession session, bool finish = false)
    {
        if (!ReferenceEquals(_stream, session) || _disposed || session.ExternalToken.IsCancellationRequested) return;
        var segment = _cache.GetSegment(session.SegmentId);
        if (segment == null || segment.Frames.Count == 0) return;
        var request = session.LatestRequest;
        int selected = FrameSelector.Select(segment, request.TargetSeconds, request.Fps);
        double last = segment.Clock.ToInputSeconds(segment.Frames[^1], segment.Frames.Count - 1, request.Fps);
        double tolerance = FrameSelector.Duration(segment.Frames[^1], request.Fps) / 2;
        bool covered = request.TargetSeconds >= segment.SafeStartSeconds - 1e-7
            && request.TargetSeconds <= segment.SafeEndSeconds + 1e-7;
        bool confirmed = covered && selected >= 0 && (last > request.TargetSeconds + tolerance
            || Math.Abs(segment.Clock.ToInputSeconds(segment.Frames[selected], selected, request.Fps) - request.TargetSeconds) < 1e-8
            || (session.IsPaused && request.TargetSeconds < last + tolerance - 1e-8)
            || (finish && request.TargetSeconds <= last + tolerance));

        bool notifyFrame = (confirmed || finish) && selected >= 0 && session.LastNotifiedVersion != session.Version;
        if (notifyFrame)
        {
            session.LastNotifiedVersion = session.Version;
            session.Satisfied = confirmed;
            Notify(session.Version, session.Navigation, () => FrameReady?.Invoke(new(request, segment.Frames[selected], !confirmed)
                { Segment = segment }), true);
            session.Demand.TrySetResult();
        }
        if (notifyFrame || session.IsPaused || finish)
            Notify(session.Version, session.Navigation, () => SegmentUpdated?.Invoke(new(request, segment,
                session.IsPaused, confirmed)), true);
    }

    private sealed class StreamingSession(PreviewRequest physical, PreviewRequest logical, long version, bool navigation,
        CancellationTokenSource cancellation, CancellationToken externalToken)
    {
        public PreviewRequest PhysicalRequest { get; } = physical;
        public PreviewRequest LatestRequest { get; set; } = logical;
        public long Version { get; set; } = version;
        public bool Navigation { get; set; } = navigation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken ExternalToken { get; set; } = externalToken;
        public Task PhysicalCompletion { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Demand { get; set; } = NewSignal();
        public TaskCompletionSource ReadAllowed { get; set; } = NewSignal();
        public List<CancellationTokenRegistration> Registrations { get; } = new();
        public long SegmentId { get; set; }
        public long LastNotifiedVersion { get; set; } = -1;
        public bool IsPaused { get; set; }
        public bool Satisfied { get; set; }
        public bool NeedsLegacyReplay { get; set; }
        public bool Ended { get; set; }
        public bool ClockChecked { get; set; }
        public long LastFrameBytes { get; set; }
    }

    private sealed class StreamSink(PreviewController owner, StreamingSession session, OutputWatchdog watchdog) : IPreviewSink
    {
        private readonly PreviewTimestampGuard _timestampGuard = new(session.PhysicalRequest.Fps);
        private bool? _lastTraceReading;
        public void OnHeader(Y4mHeader header) { }
        public void OnOutput() => watchdog.Touch();
        public void OnReadStateChanged(bool reading)
        {
            if (reading) watchdog.Resume(); else watchdog.Suspend();
            lock (owner._sync)
            {
                // IsPausedは需要更新側でも先に変わるため、計測上の読取状態は別に保持する。
                if (PerfTrace.Enabled && _lastTraceReading != reading)
                {
                    _lastTraceReading = reading;
                    PerfTrace.Write("core.read_state", new { correlationId = session.PhysicalRequest.CorrelationId,
                        logicalCorrelationId = session.LatestRequest.CorrelationId, session.SegmentId, reading,
                        session.LatestRequest.TargetSeconds });
                }
                session.IsPaused = !reading;
                owner.PulseStreamState();
                if (!reading)
                {
                    owner.TryPublishDemand(session);
                    owner.TryStartReversePrefetchCore();
                }
            }
        }
        public ValueTask BeforeReadAsync(CancellationToken token)
        {
            lock (owner._sync)
            {
                if (token.IsCancellationRequested || !ReferenceEquals(owner._stream, session)) return ValueTask.CompletedTask;
                var segment = owner._cache.GetSegment(session.SegmentId);
                bool trusted = segment?.Clock.TrustedAbsolute != false;
                double last = segment?.Frames.Count > 0
                    ? segment.Clock.ToInputSeconds(segment.Frames[^1], segment.Frames.Count - 1, session.LatestRequest.Fps)
                    : double.NegativeInfinity;
                bool needs = !trusted || last < session.LatestRequest.TargetSeconds + 2;
                // 暖機境界とVFRの位相が重なると、安全な候補がないまま先読みに到達する。永久pauseにしない。
                if (!needs && segment != null && FrameSelector.Select(segment, session.LatestRequest.TargetSeconds, session.LatestRequest.Fps) < 0)
                    throw new InvalidOperationException(CoreText.FrameUnavailable);
                bool budget = !needs || session.LastFrameBytes == 0 || owner._cache.CanAppend(session.SegmentId, session.LastFrameBytes);
                if (needs && budget) return ValueTask.CompletedTask;
                // 需要を満たせない容量では、永久pauseではなく失敗として回復可能にする。
                if (!budget && !session.Satisfied && (segment == null
                    || session.LatestRequest.TargetSeconds > segment.SafeEndSeconds + 1e-7
                    || session.LatestRequest.TargetSeconds >= last + FrameSelector.Duration(segment.Frames[^1], session.LatestRequest.Fps) / 2 - 1e-8
                    || FrameSelector.Select(segment, session.LatestRequest.TargetSeconds, session.LatestRequest.Fps) < 0))
                    throw new InvalidOperationException(CoreText.TargetCacheFull);
                if (session.ReadAllowed.Task.IsCompleted) session.ReadAllowed = NewSignal();
                return new ValueTask(session.ReadAllowed.Task.WaitAsync(token));
            }
        }
        private void RequestReplay(string reason)
        {
            session.NeedsLegacyReplay = true;
            owner.MarkLegacySource(session.LatestRequest, reason);
            session.Cancellation.Cancel();
            session.ReadAllowed.TrySetResult();
        }

        public void OnFrame(Y4mFrame frame)
        {
            lock (owner._sync)
            {
                if (!ReferenceEquals(owner._stream, session) || session.Cancellation.IsCancellationRequested) return;
                if (!session.ClockChecked)
                {
                    session.ClockChecked = true;
                    var clock = ClockFor(session.PhysicalRequest, frame);
                    if (!clock.TrustedAbsolute) { RequestReplay("要求先頭のabsolute時刻が入力原点と一致しません"); return; }
                    owner._cache.SetClock(session.SegmentId, clock);
                }
                if (_timestampGuard.IsInvalid(frame)) { RequestReplay(_timestampGuard.Reason); return; }
                if (!owner._cache.Append(session.SegmentId, frame))
                    throw new InvalidOperationException(CoreText.ReadAheadCacheFull);
                session.LastFrameBytes = frame.Data.LongLength;
                owner.TryPublishDemand(session);

            }
        }
    }
}

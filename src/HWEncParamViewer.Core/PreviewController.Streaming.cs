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

    private void CancelStreamCore(string reason = "unspecified")
    {
        if (_stream is not { } stream) return;
        if (PerfTrace.Enabled) PerfTrace.Write("core.stream.cancel", new { reason, stream.PhysicalRequest.CorrelationId, stream.SegmentId, stream.IsPaused });
        // 意図的に取り消す自分の順先読みだけ、同じ境界での再起動を許す。
        if (ReferenceEquals(_lastForwardPrefetch, stream.InitialRequest))
            _lastForwardPrefetch = null;
        stream.Cancellation.Cancel();
        stream.ReadAllowed.TrySetResult();
        stream.Demand.TrySetResult();
        PulseStreamState();
    }

    private bool TrySubmitStreaming(PreviewRequest request, bool bypassCache, bool bypassDebounce,
        bool navigation, CancellationToken externalToken, bool reuseActiveStream)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool capable = request.Continuous && request.Capabilities.TimestampPassthrough
                && request.TimestampOriginSeconds is double origin && double.IsFinite(origin) && !IsLegacySource(request);
            var active = _stream is { } existing && !existing.Cancellation.IsCancellationRequested ? existing : null;
            PreviewSegmentSnapshot? cached = null;
            if (!bypassCache && !IsLegacySource(request) && _cache.TryGetAt(request, request.TargetSeconds, out var available)) cached = available;

            var current = active == null || active.SegmentId == 0 ? null : _cache.GetSegment(active.SegmentId);
            if (active != null && TryKeepForwardPrefetchCore(active, request, cached, current, bypassCache, navigation, externalToken)) return true;
            if (active is { IsForwardPrefetch: true })
            {
                var connected = _cache.GetConnectedSafeRange(active.LatestRequest);
                bool mayAdopt = navigation && (!bypassCache || reuseActiveStream)
                    && FrameCache.SameContent(active.PhysicalRequest, request) && connected != null
                    && PreviewSafeRange.Contains(request.TargetSeconds, connected.Value.Start, connected.Value.End);
                if (!mayAdopt)
                {
                    // 先読みは同じ接続範囲内のcache移動なら微小逆移動も保持する。
                    // 内容変更・明示更新・範囲外移動は通常streamの小jump許可へ流さず再取得へ戻す。
                    CancelStreamCore("forward_prefetch_left_range");
                    active = null;
                    current = null;
                }
            }
            double frontier = current?.SafeEndSeconds ?? active?.LatestRequest.TargetSeconds ?? request.TargetSeconds;
            bool withinJump = active != null && request.TargetSeconds <= Math.Max(frontier, active.LatestRequest.TargetSeconds) + 3;
            double retainedStart = current?.SafeStartSeconds ?? (active == null ? request.TargetSeconds
                : PreviewWarmupPolicy.SafeStartSeconds(active.PhysicalRequest, false));
            if (current != null && current.Frames.Count > 0)
                retainedStart = FrameCache.SafeStartIncludingFirst(retainedStart, current.Clock, current.Frames[0], request.Fps);
            // 別segmentのcacheが目標を持っていても、このstreamに存在しないprefixへ需要を再結合しない。
            bool notBeforeRetained = active != null && PreviewSafeRange.Contains(request.TargetSeconds, retainedStart, double.PositiveInfinity);
            // 正方向の隣不足を明示した需要だけが、信頼済みの中心を持つ同じ生存streamへ合流できる。
            // 未知の隣時刻は予測せず、通常のcache bypassや明示更新は新しい実取得を維持する。
            bool neighborReuse = reuseActiveStream && bypassCache && navigation && capable && request.TimestampOriginTrusted
                && active is { Ended: false } && current is { IsActive: true, Clock.TrustedAbsolute: true }
                && withinJump && notBeforeRetained && FrameCache.SameContent(active.PhysicalRequest, request)
                && PreviewSafeRange.Contains(request.TargetSeconds, double.NegativeInfinity, current.SafeEndSeconds);
            double centerSeconds = double.NaN;
            if (neighborReuse)
            {
                int center = FrameSelector.Select(current!, request.TargetSeconds, request.Fps);
                if (center >= 0)
                {
                    centerSeconds = current!.Clock.ToInputSeconds(current.Frames[center], center, request.Fps);
                    neighborReuse = Math.Abs(centerSeconds - request.TargetSeconds)
                        <= FrameSelector.Duration(current.Frames[center], request.Fps) / 2 + 1e-7;
                }
                else neighborReuse = false;
                // 次の1枚分の予算を確保できないpauseには合流せず、従来の再取得へ戻す。
                if (neighborReuse && active!.IsPaused && active.LastFrameBytes > 0)
                    neighborReuse = _cache.CanAppend(active.SegmentId, active.LastFrameBytes);
            }
            bool compatible = active != null && navigation && (!bypassCache || neighborReuse)
                && FrameCache.SameContent(active.PhysicalRequest, request);
            if (compatible && withinJump && notBeforeRetained && (capable || cached != null))
            {
                // 照合済み区間に実需要が合流した後は、通常の表示・隣待ち・読取需要へ戻す。
                active!.IsForwardPrefetch = false;
                active.Demand.TrySetResult();
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

            if (active != null) CancelStreamCore("submit_incompatible_or_before_retained");
            // 照合失敗は物理区間の混在だけを禁じる。新しい1本のstreamは継続できる。
            // 区間の破棄と接続禁止はFrameCache側で維持し、逆・順の先読みも再開しない。
            if (!capable) return false;
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
            bool? history;
            (backend, history) = await PrepareBackendAsync(backend, session.InitialRequest, false, session.Cancellation.Token).ConfigureAwait(false);
            var physical = PhysicalAfterRequest(session.InitialRequest with { DependsOnHistory = history }, false) with { Continuous = true };
            session.PhysicalRequest = physical;
            double seek = CommandBuilder.Plan(physical).SeekSeconds ?? 0;
            var clock = new PreviewClockMap(physical.TimestampOriginSeconds, seek, true);
            // 物理seekと同じ計画の暖機境界を使い、暖機0の区間はseek直後から検証対象にする。
            session.SegmentId = _cache.BeginSegment(physical, clock, PreviewWarmupPolicy.SafeStartSeconds(physical, false));
            if (!session.IsForwardPrefetch) _cache.PinCurrent(session.SegmentId, session.LatestRequest.TargetSeconds);
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
                    if (!session.IsForwardPrefetch && ReferenceEquals(_stream, session)) Notify(session.Version, session.Navigation,
                        () => Failed?.Invoke(new(session.LatestRequest, CoreText.OutputTimedOut)
                            { TimedOut = true, Diagnostic = LegacyFailureDiagnostic(session.LatestRequest, stoppedException, stoppedResult) }), true);
                }
                return;
            }
            PreviewRunResult result;
            try { result = await run.ConfigureAwait(false); }
            catch (OperationCanceledException) when (session.NeedsLegacyReplay)
            { result = new(-12, "", true); }
            if (!session.IsForwardPrefetch && session.NeedsLegacyReplay && !scheduled.IsCancellationRequested && !session.ExternalToken.IsCancellationRequested)
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
                    if (!session.IsForwardPrefetch && ReferenceEquals(_stream, session) && session.LastNotifiedVersion != session.Version)
                        Notify(session.Version, session.Navigation, () => Failed?.Invoke(new(session.LatestRequest,
                            CoreText.FrameUnavailable, result)), true);
                    else if (!session.IsForwardPrefetch && ReferenceEquals(_stream, session) && _cache.GetSegment(session.SegmentId) is { } segment)
                        Notify(session.Version, session.Navigation, () => Completed?.Invoke(new(session.LatestRequest,
                            result, false, !session.Satisfied)
                            { Frames = segment.Frames, Segment = segment }), true);
                }
            }
            else if (!session.Cancellation.IsCancellationRequested && !result.IsCancelled)
            {
                _cache.RemoveSegment(session.SegmentId);
                if (!session.IsForwardPrefetch) Notify(session.Version, session.Navigation, () => Failed?.Invoke(new(session.LatestRequest,
                    CoreText.EncoderExited(result.ExitCode), result)), true);
            }

            async Task<PreviewRunResult> RunAsync()
            {
                try { return await RunTrackedBackendAsync(backend, session.PhysicalRequest, sink, session.Cancellation.Token).ConfigureAwait(false); }
                catch (PreviewServerNotSupportedException) when (_enableFallback && ReferenceEquals(backend, _preferred))
                {
                    lock (_sync) _unsupported.Add(session.PhysicalRequest.EncoderPath);
                    backend = _fallback;
                    _cache.RemoveSegment(session.SegmentId);
                    physical = PhysicalAfterRequest(session.InitialRequest with { DependsOnHistory = null }, false) with { Continuous = true };
                    session.PhysicalRequest = physical;
                    seek = CommandBuilder.Plan(physical).SeekSeconds ?? 0;
                    clock = new PreviewClockMap(physical.TimestampOriginSeconds, seek, true);
                    session.SegmentId = _cache.BeginSegment(physical, clock, PreviewWarmupPolicy.SafeStartSeconds(physical, false));
                    if (!session.IsForwardPrefetch) _cache.PinCurrent(session.SegmentId, session.LatestRequest.TargetSeconds);
                    sink = new StreamSink(this, session, watchdog);
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
            if (!session.IsForwardPrefetch) Notify(session.Version, session.Navigation, () => Failed?.Invoke(new(session.LatestRequest, exception.Message,
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
        // 先読みは受理済み表示を置き換えない。画像・完了・Before用の通知は実需要への合流後だけ送る。
        if (session.IsForwardPrefetch) return;
        var request = session.LatestRequest;
        int selected = FrameSelector.Select(segment, request.TargetSeconds, request.Fps);
        double last = segment.Clock.ToInputSeconds(segment.Frames[^1], segment.Frames.Count - 1, request.Fps);
        double tolerance = FrameSelector.Duration(segment.Frames[^1], request.Fps) / 2;
        bool covered = PreviewSafeRange.Contains(request.TargetSeconds, segment.SafeStartSeconds, segment.SafeEndSeconds);
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
        // 中心確定後も隣の1枚を待つVMへ区間を知らせ、先読み2秒のpauseまで表示を止めない。
        // 隣需要の状態はVMが持つため、中心確定後も区間更新を送り、VMの最新値集約に任せる。
        if (notifyFrame || session.IsPaused || finish || confirmed)
            Notify(session.Version, session.Navigation, () => SegmentUpdated?.Invoke(new(request, segment,
                session.IsPaused, confirmed)), true);
    }

    private sealed class StreamingSession(PreviewRequest physical, PreviewRequest logical, long version, bool navigation,
        CancellationTokenSource cancellation, CancellationToken externalToken)
    {
        public PreviewRequest InitialRequest { get; } = logical;
        public PreviewRequest PhysicalRequest { get; set; } = physical;
        public PreviewRequest LatestRequest { get; set; } = logical;
        public long Version { get; set; } = version;
        public bool Navigation { get; set; } = navigation;
        // 先読み中は物理境界と論理位置を分け、表示pin・画像・完了通知を変更しない。
        public bool IsForwardPrefetch { get; set; }
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
                double demand = session.IsForwardPrefetch ? session.InitialRequest.TargetSeconds : session.LatestRequest.TargetSeconds;
                bool needs = !trusted || last < demand + 2;
                // 暖機境界とVFRの位相が重なると、安全な候補がないまま先読みに到達する。永久pauseにしない。
                if (!session.IsForwardPrefetch && !needs && segment != null && FrameSelector.Select(segment, session.LatestRequest.TargetSeconds, session.LatestRequest.Fps) < 0)
                    throw new InvalidOperationException(CoreText.FrameUnavailable);
                bool budget = !needs || session.LastFrameBytes == 0 || owner._cache.CanAppend(session.SegmentId, session.LastFrameBytes);
                if (needs && budget) return ValueTask.CompletedTask;
                // 需要を満たせない容量では、永久pauseではなく失敗として回復可能にする。
                if (!budget && !session.Satisfied && (segment == null
                    || PreviewSafeRange.IsAfter(session.LatestRequest.TargetSeconds, segment.SafeEndSeconds)
                    || session.LatestRequest.TargetSeconds >= last + FrameSelector.Duration(segment.Frames[^1], session.LatestRequest.Fps) / 2 - 1e-8
                    || FrameSelector.Select(segment, session.LatestRequest.TargetSeconds, session.LatestRequest.Fps) < 0))
                    throw new InvalidOperationException(CoreText.TargetCacheFull);
                if (session.ReadAllowed.Task.IsCompleted) session.ReadAllowed = NewSignal();
                return new ValueTask(session.ReadAllowed.Task.WaitAsync(token));
            }
        }
        private void RequestReplay(string reason)
        {
            if (session.IsForwardPrefetch)
            {
                // 先読みの不良時計だけで、表示済みの前景を取り直したり失敗通知へ変えない。
                owner._cache.RemoveSegment(session.SegmentId);
                session.Cancellation.Cancel();
                session.ReadAllowed.TrySetResult();
                return;
            }
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

namespace HWEncVppPreview.Core;

public sealed partial class EncoderPreviewSession
{
    internal void BeginSeekDrag() => _seekDragging = true;
    internal void EndSeekDrag(double seconds)
    {
        if (!_seekDragging) return;
        SetBeforeProtectionDirection(0);
        _seekDragging = false;
        if (seconds == CurrentSeconds)
        {
            // ドラッグ中のAfterを捨てず、確定した同じ目標のBeforeだけを同時取得へ進める。
            long generation = Volatile.Read(ref _generation);
            _concurrentComparisonGeneration = generation;
            if (_context is { Kind: RequestKind.After } context && IsCurrent(context))
                QueueBefore(context.Request with { AllowConcurrentBackground = true }, generation);
        }
        else SeekTo(seconds, allowConcurrentBefore: true);
    }

    internal Task Reload()
    {
        _cache.Clear();
        ClearInterval();
        ClearBeforeInterval();
        return QueuePreview(bypassCache: true, bypassDebounce: true);
    }

    internal Task FiltersChanged(bool bypassDebounce)
    {
        ClearInterval();
        return QueuePreview(bypassDebounce: bypassDebounce);
    }

    internal void DiscardInputRequests()
    {
        // 入力切替時の未使用配列は片付ける。貸出中の配列は最後の所有解放まで変更しない。
        _rawOwnershipScope?.ClearFreeBuffers();
        lock (_taskLock) _preparationCancellation?.Cancel();
        BeginIntent(navigation: false);
    }

    internal void SliderChanged(double seconds)
    {
        RecordNavigationIntent();
        seconds = ClampTime(seconds);
        if (_currentSeconds.Equals(seconds)) return;
        _currentSeconds = seconds;
        _host.StateChanged(nameof(CurrentSeconds));
        SetBeforeProtectionDirection(0);
        _host.ShowTime(_currentSeconds);
        _ = QueuePreview(navigation: true);
    }

    internal void ClearPendingNeighbor(long generation)
    {
        if (_pendingNeighbor?.Generation == generation) _pendingNeighbor = null;
    }

    internal void StepFrame(int direction)
    {
        if (direction != 0) RecordNavigationIntent();
        if (PerfTrace.Enabled) PerfTrace.Write("vm.step", new { direction, pending = _pendingNeighbor != null, reverseWait = _reversePrefetchWait != null, target = CurrentSeconds });
        LeaveReversePrefetchWaitForForwardStep(direction);
        if (direction == 0 || _pendingNeighbor != null)
        {
            if (PerfTrace.Enabled) PerfTrace.Write("vm.step.drop", new { direction, pending = _pendingNeighbor != null });
            return;
        }
        direction = Math.Sign(direction);
        if (IsInitialized && direction < 0 && CurrentSeconds <= 1e-8)
        {
            _completedAfterStatus = PreviewProgress.Start;
            UpdateNavigationStatus();
            return;
        }
        ValidateNavigationState();
        if (_intervalRequest is { } interval && _intervalFrames.Count > 0)
        {
            var navigation = NavigationFrames(interval, _intervalSegment);
            if (navigation.Count > 0)
            {
                int anchor = NavigationAnchorIndex(navigation, CurrentSeconds, interval.Fps);
                int next = anchor + direction;
                if (anchor >= 0 && next >= 0 && next < navigation.Count
                    && NavigationNeighborsConnected(navigation[anchor], navigation[next]))
                {
                    var reference = navigation[next];
                    if ((reference.InputSeconds - CurrentSeconds) * direction > 1e-9)
                    {
                        if (DisplayCached(reference.Frame, reference.InputSeconds, direction, reference)) return;
                    }
                }
            }
            // trustedな移動viewがあれば、warmを含む元配列へ戻って候補を増やさない。
            if (_intervalSegment?.Clock.TrustedAbsolute != true && _intervalFrames.Count > 0)
            {
                double target = IntervalClock(interval, _intervalFrames).ToSelectionSeconds(CurrentSeconds, _intervalFrames, interval.Fps);
                var first = _intervalFrames[0];
                var last = _intervalFrames[^1];
                double firstTime = first.Xts ?? 0;
                double lastTime = last.Xts ?? (_intervalFrames.Count - 1) / FrameSelector.FrameRate(last, interval.Fps);
                double tolerance = FrameSelector.Duration(last, interval.Fps) / 2;
                if (target >= firstTime - tolerance && target <= lastTime + tolerance)
                {
                    int current = NeighborAnchorIndex(_intervalFrames, target, interval.Fps);
                    int next = current + direction;
                    if (current >= 0 && next >= 0 && next < _intervalFrames.Count)
                    {
                        var frame = _intervalFrames[next];
                        double seconds = FrameSeconds(interval, frame, next);
                        if (FrameIsSafe(seconds) && (seconds - CurrentSeconds) * direction > 1e-9)
                        {
                            if (PerfTrace.Enabled) PerfTrace.Write("vm.step.cache", new { seconds, direction });
                            if (DisplayCached(frame, seconds, direction)) return;
                        }
                    }
                }
            }
        }
        if (TryWaitForReversePrefetch(direction)) return;
        // 取り直し中のキー反復は加算せず、実際の隣が届くまで目標を保つ。
        if (!IsInitialized)
        {
            // 入力を開く前の操作は、表示位置だけを仮に動かす。
            SeekTo(CurrentSeconds + direction / (_inputInfo?.Fps ?? 30));
            return;
        }
        if (PerfTrace.Enabled) PerfTrace.Write("vm.step.refetch", new { direction, target = CurrentSeconds });
        _ = QueuePreview(navigation: true, neighborDirection: direction,
            requirePhysicalRefetch: _intervalSegment?.Clock.TrustedAbsolute == true);
    }
    private bool DisplayCached(Y4mFrame frame, double seconds, int direction, PreviewFrameReference? reference = null)
    {
        if (_intervalRequest == null) return true;
        if (!TryRetainSelection(frame, "VmStepSelection", out var retained)) return false;
        using var selectedRaw = retained;
        // 取得失敗は呼出元の既存cache miss経路へ。anchor/世代/目標を先にcommitしない。
        if (reference != null) AdoptNavigationReference(reference);
        long generation = BeginIntent(navigation: true);
        if (PerfTrace.Enabled) PerfTrace.Write("vm.step.accepted", new { generation, seconds, direction, frame.Xts });
        SetTimeOnly(seconds);
        // 物理offの結果を移動に再利用しても、診断用sourceはhelp由来の論理設定を維持する。
        var logical = _intervalLogicalRequest ?? _intervalRequest;
        var request = RetargetInterval(_intervalRequest, seconds) with { CorrelationId = generation, AllowConcurrentBackground = false,
            InputArgs = logical.InputArgs, VppArgs = logical.VppArgs, Capabilities = _capabilities,
            TimestampOriginTrusted = _inputInfo?.TimestampOriginTrusted ?? _intervalRequest.TimestampOriginTrusted };
        CheckBeforeInterval(request);
        var context = new RequestContext(generation, request, _clock.GetTimestamp(), editEpoch: _editEpoch);
        context.SetSegmentSharing(_intervalSegment, _intervalRaw);
        _lastIntentTarget = seconds;
        RegisterContext(context);
        SetBeforeProtectionDirection(direction);
        _host.IsBusy = true;
        _host.ErrorText = null;
        _completedAfterStatus = PreviewProgress.DoneCache;
        DisplayFrame(frame, context);
        // 表示は即時に進め、Coreにも最新の移動意図を伝えて古い待ち要求を置き換える。
        Controller.Submit(request, navigation: true);
        NotifyAcceptedFrameStep(request, direction, seconds);
        Track(ReleaseContextAsync(context));
        return true;
    }
    internal void SeekTo(double seconds, bool force = false, bool allowConcurrentBefore = false, NavigationOperation operation = NavigationOperation.Seek)
    {
        RecordNavigationIntent();
        seconds = ClampTime(seconds);
        if (!force && seconds == CurrentSeconds) return;
        SetBeforeProtectionDirection(0);
        SetTimeOnly(seconds);
        _ = QueuePreview(navigation: true, allowConcurrentBefore: allowConcurrentBefore, operation: operation);
    }
    internal double ClampTime(double seconds) => double.IsFinite(seconds) ? Math.Clamp(seconds, 0, HasDuration ? DurationSeconds : double.MaxValue) : 0;
    internal long BeginIntent(bool navigation)
    {
        if (!navigation) SetBeforeProtectionDirection(0);
        long generation = Interlocked.Increment(ref _generation);
        ReleaseObsoleteRequestSnapshots(generation);
        _latestIntentNavigation = navigation;
        _pendingNeighbor = null;
        _reversePrefetchWait = null;
        Volatile.Write(ref _context, null);
        if (!navigation)
        {
            CancelBefore();
            Interlocked.Increment(ref _editEpoch);
            _afterRequests.Clear();
            lock (_taskLock) DropImage(ref _pendingAfterImage);
        }
        return generation;
    }

    private void RegisterContext(RequestContext context)
    {
        _afterRequests[context.Generation] = context;
        Volatile.Write(ref _context, context);
    }

    internal Task QueuePreview(bool bypassCache = false, bool navigation = false, int neighborDirection = 0, bool requirePhysicalRefetch = false, bool bypassDebounce = false, bool allowConcurrentBefore = false, NavigationOperation operation = NavigationOperation.Seek, bool validateRestoredTime = false)
    {
        if (!IsInitialized || IsSuspended || IsDisposed) return Task.CompletedTask;
        _host.CancelInputEdit();
        // シークは向きにかかわらず目標を早く確定する。逆の広い窓はコマ移動と接続範囲内の秒移動だけ。
        if (neighborDirection != 0) operation = NavigationOperation.Frame;
        bool reverse = navigation && (operation == NavigationOperation.Frame && neighborDirection < 0
            || operation == NavigationOperation.Page && CurrentSeconds < _lastIntentTarget - 1e-8
                && IsConnectedNavigationTarget(CurrentSeconds, _lastIntentTarget));
        _lastIntentTarget = CurrentSeconds;
        _latestIntentReverse = reverse;
        long generation = BeginIntent(navigation);
        if (!navigation || allowConcurrentBefore) _concurrentComparisonGeneration = generation;
        if (PerfTrace.Enabled) PerfTrace.Write("vm.queue.after", new { generation, navigation, neighborDirection, bypassCache, target = CurrentSeconds });
        long editEpoch = Volatile.Read(ref _editEpoch);
        if (neighborDirection != 0) _pendingNeighbor = new NeighborIntent(generation, neighborDirection);
        // 入力検査中の移動連打では検査そのものを止めず、取得後の最新位置だけを使う。
        if (navigation && _inputInfo == null && _preparationCancellation != null)
        { _host.IsBusy = true; _host.Progress = PreviewProgress.Processing; return _preparation; }
        CancellationTokenSource cancellation;
        lock (_taskLock)
        {
            _preparationCancellation?.Cancel();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _preparationCancellation = cancellation;
        }
        string encoder = _host.EncoderPath, input = _host.InputPath;
        if (!string.IsNullOrWhiteSpace(encoder) && _metadataEncoder != encoder)
        {
            _metadataEncoder = encoder;
            _metadataTask = LoadMetadataAsync(encoder, _lifetime.Token);
            Track(_metadataTask);
        }
        if (string.IsNullOrWhiteSpace(encoder) || string.IsNullOrWhiteSpace(input))
        {
            _pendingNeighbor = null;
            _host.IsBusy = false;
            _host.ErrorText = null;
            _host.Progress = PreviewProgress.SelectEncoderAndInput;
            _preparation = Task.CompletedTask;
            lock (_taskLock)
            {
                if (ReferenceEquals(_preparationCancellation, cancellation)) _preparationCancellation = null;
                cancellation.Dispose();
            }
            return _preparation;
        }
        _host.IsBusy = true;
        _host.ErrorText = null;
        _host.Progress = navigation ? PreviewProgress.Processing : PreviewProgress.ProbingInput;
        var configuration = _host.GetConfiguration(encoder, input);
        double seconds = CurrentSeconds;
        _preparation = PrepareAsync();
        Track(_preparation);
        return _preparation;

        async Task PrepareAsync()
        {
            try
            {
                await _metadataTask.WaitAsync(cancellation.Token).ConfigureAwait(false);
                PreviewCapabilities capabilities = _capabilities;
                var info = await PrepareInputAsync(configuration, capabilities,
                    _inputInfo, validateRestoredTime && seconds > 0, cancellation.Token).ConfigureAwait(false);
                await PostAsync(() =>
                {
                    if (IsDisposed || editEpoch != Volatile.Read(ref _editEpoch) || cancellation.IsCancellationRequested) return;
                    long actualGeneration = generation;
                    bool actualNavigation = navigation;
                    bool actualReverse = reverse;
                    double target = seconds;
                    if (!IsCurrent(generation))
                    {
                        if (!_latestIntentNavigation) return;
                        actualGeneration = Volatile.Read(ref _generation);
                        actualNavigation = true;
                        actualReverse = _latestIntentReverse;
                        target = CurrentSeconds;
                    }
                    ApplyInputInfo(info);
                    if (HasDuration)
                        target = validateRestoredTime && !actualNavigation && target > DurationSeconds
                            ? 0 : Math.Min(target, DurationSeconds);
                    // 古い移動の準備完了で目標時刻を過去へ戻さない。
                    if (actualGeneration == Volatile.Read(ref _generation)) SetTimeOnly(target);
                    var request = CreateAfterRequest(configuration, info, capabilities, target,
                        actualGeneration, actualReverse, _concurrentComparisonGeneration == actualGeneration,
                        HasDuration ? DurationSeconds : null);
                    _host.ShowInputInfo(info);
                    var neighbor = _pendingNeighbor?.Generation == actualGeneration ? _pendingNeighbor : null;
                    var context = new RequestContext(actualGeneration, request, _clock.GetTimestamp(),
                        editEpoch: editEpoch, neighborDirection: neighbor?.Direction ?? 0);
                    CheckBeforeInterval(request);
                    RegisterContext(context);
                    _host.Progress = PreviewProgress.Processing;
                    ReleaseIntervalSnapshotsForSubmission();
                    SubmitAfter(request, bypassCache, requirePhysicalRefetch, actualNavigation,
                        actualReverse, neighbor?.Direction ?? 0, bypassDebounce);
                    // 無フィルタではAfterの確定frameがそのままBeforeなので、別の有限取得を競合させない。
                    if (request.AllowConcurrentBackground && request.VppArgs.Count > 0) QueueConcurrentBefore(request, actualGeneration);
                    Track(ReleaseContextAsync(context));
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                string diagnostics = exception switch
                {
                    InputProbeException probe => probe.StandardError, InputProbeTimeoutException timeout => timeout.StandardError,
                    PreviewBackendException backend => backend.StandardError, _ => "",
                };
                await PostAsync(() =>
                {
                    if (IsDisposed || editEpoch != Volatile.Read(ref _editEpoch) || cancellation.IsCancellationRequested) return;
                    if (!IsCurrent(generation) && !_latestIntentNavigation) return;
                    _pendingNeighbor = null;
                    _lastFailedAfter = Volatile.Read(ref _generation);
                    _host.ShowExceptionError(exception.Message, diagnostics, exception);
                }).ConfigureAwait(false);
            }
            finally
            {
                lock (_taskLock)
                {
                    if (ReferenceEquals(_preparationCancellation, cancellation)) _preparationCancellation = null;
                    cancellation.Dispose();
                }
            }
        }
    }

    private async Task ReleaseContextAsync(RequestContext context)
    {
        try
        {
            if (context.Request.Continuous) await Controller.WaitForCurrentDemandAsync().ConfigureAwait(false);
            else await Controller.WaitForIdleAsync().ConfigureAwait(false);
        }
        finally
        {
            // Coreが実行を始める直前の要求も、タスク完了までは通知の対応表から外さない。
            if (!context.Request.Continuous) _afterRequests.TryRemove(context.Generation, out _);
        }
    }

    internal void UpdateNavigationStatus()
    {
        if (IsDisposed) return;
        long latest = Volatile.Read(ref _generation);
        if (_lastFailedAfter == latest) return;
        _host.IsBusy = _lastDisplayedAfter < latest || _lastCompletedAfter < latest;
        _host.Progress = _host.IsBusy ? PreviewProgress.Processing : _completedAfterStatus;
    }

    internal static PreviewRequest RetargetInterval(PreviewRequest request, double target)
    {
        var interval = CommandBuilder.Plan(request);
        double margin = target - (interval.SeekSeconds ?? 0);
        return request with
        {
            TargetSeconds = target, PreMarginSeconds = Math.Max(0, margin),
            PostMarginSeconds = Math.Max(0, (interval.FrameCount - 1) / request.Fps - margin),
        };
    }

    // 入力fpsから時刻を推測せず、取り直した実フレーム列の隣へ1回だけ動かす。
    internal PreviewRequest? CompleteNeighbor(RequestContext context, IReadOnlyList<Y4mFrame> frames)
    {
        var request = context.Request;
        if (context.NeighborResolved) return RetargetInterval(request, context.ResolvedTargetSeconds ?? request.TargetSeconds);
        var navigation = NavigationFrames(request, context.Segment);
        if (navigation.Count > 0)
        {
            int anchor = NavigationAnchorIndex(navigation, request.TargetSeconds, request.Fps);
            int next = anchor + context.NeighborDirection;
            if (anchor >= 0 && next >= 0 && next < navigation.Count
                && NavigationNeighborsConnected(navigation[anchor], navigation[next]))
            {
                var reference = navigation[next];
                return ResolveNeighbor(reference.Frame, reference.InputSeconds, reference);
            }
        }
        else if (context.Segment?.Clock.TrustedAbsolute == true)
        {
            // 検証barrierやpurgeで候補が消えた場合、old配列を境界扱いせず新しい物理窓へ戻す。
            if (_pendingNeighbor?.Generation == context.Generation) _pendingNeighbor = null;
            if (IsCurrent(context.Generation))
                _ = QueuePreview(navigation: true, neighborDirection: context.NeighborDirection, requirePhysicalRefetch: true);
            return null;
        }
        else
        {
            var clock = context.Segment?.Clock ?? IntervalClock(request, frames);
            int index = NeighborAnchorIndex(frames, clock.ToSelectionSeconds(request.TargetSeconds, frames, request.Fps), request.Fps);
            int next = index + context.NeighborDirection;
            if (index >= 0 && next >= 0 && next < frames.Count)
            {
                var frame = frames[next];
                double seconds = clock.ToInputSeconds(frame, next, request.Fps);
                if (context.Segment is { } segment && !PreviewSafeRange.Contains(seconds, segment.SafeStartSeconds, segment.SafeEndSeconds))
                {
                    // 範囲外は画素選択をしない確定判断。取得失敗と分け、従来どおり保留だけ解除する。
                    if (_pendingNeighbor?.Generation == context.Generation) _pendingNeighbor = null;
                    return null;
                }
                return ResolveNeighbor(frame, seconds);
            }
        }
        if (_pendingNeighbor?.Generation == context.Generation) _pendingNeighbor = null;
        context.NeighborResolved = true;
        if (IsCurrent(context.Generation))
        {
            if (context.Generation > _lastDisplayedAfter)
            {
                _lastDisplayedAfter = context.Generation;
                _lastDisplayedRevision = -1;
            }
            _completedAfterStatus = context.NeighborDirection < 0 ? PreviewProgress.Start : PreviewProgress.End;
        }
        return null;

        PreviewRequest? ResolveNeighbor(Y4mFrame frame, double seconds, PreviewFrameReference? reference = null)
        {
            if (!TryRetainSelection(frame, "VmNeighborSelection", out var retained))
            {
                // この通知内では再帰せず、既存の正ID失効と同じcache miss処理へ一度だけ戻す。
                if (IsCurrent(context.Generation))
                    _ = QueuePreview(navigation: true, neighborDirection: context.NeighborDirection,
                        requirePhysicalRefetch: context.Segment?.Clock.TrustedAbsolute == true);
                return null;
            }
            using var selectedRaw = retained;
            // 取得成功後だけ、区間/anchor/保留解除と目標をcommitする。
            if (reference != null)
            {
                AdoptNavigationReference(reference);
                context.Segment = reference.Segment;
            }
            if (_pendingNeighbor?.Generation == context.Generation) _pendingNeighbor = null;
            context.NeighborResolved = true;
            context.ResolvedTargetSeconds = seconds;
            // 明示的なシークが後から来た場合も、最新の目標を過去へ戻さない。
            bool current = IsCurrent(context.Generation);
            if (current)
            {
                SetTimeOnly(seconds);
                _lastIntentTarget = seconds;
                SetBeforeProtectionDirection(context.NeighborDirection);
            }
            DisplayFrame(frame, context);
            if (current) NotifyAcceptedFrameStep(request, context.NeighborDirection, seconds);
            return RetargetInterval(request, seconds);
        }
    }

    private static int NeighborAnchorIndex(IReadOnlyList<Y4mFrame> frames, double target, double requestedFps)
    {
        // 既知フレームの隣は同じXtsを優先する。表示時間の許容幅で細かい間隔を飛ばさない。
        // seekとF9形式のXtsを足し戻した丸め誤差だけを吸収する。
        for (int i = 0; i < frames.Count; ++i)
            if (frames[i].Xts is double ts && Math.Abs(ts - target) <= 1e-8) return i;
        return FrameSelector.Select(frames, target, requestedFps);
    }

    private double FrameSeconds(PreviewRequest request, Y4mFrame frame, int index) =>
        IntervalClock(request, _intervalFrames).ToInputSeconds(frame, index, request.Fps);

    private sealed record NeighborIntent(long Generation, int Direction);

}

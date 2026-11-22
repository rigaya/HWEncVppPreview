namespace HWEncVppPreview.Core;

public sealed partial class EncoderPreviewSession
{
    private void OnFrameReady(PreviewFrameResult value)
    {
        if (IsDisposed) return;
        var context = ContextFor(value.Request);
        if (context == null) return;
        if (value.IsReplay)
        {
            DropRawPosts(candidate => ReferenceEquals(candidate, context));
            BeginReplay(context);
        }
        long revision = Volatile.Read(ref context.DisplayRevision);
        var frame = value.Frame;
        var segment = RetainSelectedFrame(value.Segment, frame);
        bool isReplay = value.IsReplay;
        // 完了列で選ぶ元画像と隣不足は、callback中に選択1枚の所有slotへ移す。
        if (context.Kind == RequestKind.Before || context.NeighborDirection != 0)
        {
            try
            {
                if (IsCurrent(context) && segment != null) context.Segment = segment;
                if (context.Kind == RequestKind.Before) QueueEarlyBeforeFrame(context, value, segment);
            }
            finally
            {
                // callback開始後に現役contextが交換されても、遅い代入の所有をここで閉じる。
                ReleaseDetachedContext(context);
            }
            return;
        }
        PostRaw(context, new[] { frame }, () =>
        {
            if (!IsCurrent(context) || revision != Volatile.Read(ref context.DisplayRevision)) return;
            if (segment != null) context.Segment = segment;
            if (isReplay) PrepareReplayUi(context);
            DisplayFrame(frame, context);
        });
    }
    private void OnReplayStarted(PreviewRequest request)
    {
        var context = ContextFor(request);
        if (IsDisposed || context == null) return;
        // 再取得が1枚も返さず失敗しても、初回の変換が後から復活しないよう即時に失効する。
        DropRawPosts(candidate => ReferenceEquals(candidate, context));
        BeginReplay(context);
        long revision = Volatile.Read(ref context.DisplayRevision);
        Action apply = () =>
        {
            if (!IsCurrent(context) || revision != Volatile.Read(ref context.DisplayRevision)) return;
            if (context.Kind == RequestKind.Before) { _host.IsBeforeBusy = true; return; }
            PrepareReplayUi(context);
            if (context.Generation != Volatile.Read(ref _generation)) return;
            _lastCompletedAfter = Math.Min(_lastCompletedAfter, context.Generation - 1);
            _host.IsBusy = true;
            _host.Progress = PreviewProgress.Processing;
        };
        if (context.Kind == RequestKind.Before) Track(PostAsync(apply));
        else _post(apply);
    }
    private static void BeginReplay(RequestContext context)
    {
        if (Interlocked.CompareExchange(ref context.ReplayStarted, 1, 0) == 0)
            Interlocked.Increment(ref context.DisplayRevision);
    }
    private void PrepareReplayUi(RequestContext context)
    {
        if (context.ReplayUiStarted) return;
        context.ReplayUiStarted = true;
        context.NeighborResolved = false;
        context.ResolvedTargetSeconds = null;
        if (context.Generation != Volatile.Read(ref _generation)) return;
        ClearNavigationAnchor();
        if (_lastFailedAfter == context.Generation)
        {
            _lastFailedAfter = 0;
            _host.ErrorText = null;
        }
    }
    private void OnCompleted(PreviewCompletion value) => OnCompletedWithWindow(value, null);

    private void OnCompletedWithWindow(PreviewCompletion value, OwnedUncachedWindow? supplied)
    {
        var context = ContextFor(value.Request);
        if (IsDisposed || context == null) return;
        if (context.Kind == RequestKind.Before) Interlocked.Exchange(ref context.BeforeCompletionReceived, 1);
        if (value.IsReplay)
        {
            DropRawPosts(candidate => ReferenceEquals(candidate, context));
            BeginReplay(context);
        }
        IReadOnlyList<Y4mFrame> frames = value.Frames;
        // 正IDの完了はUIで最新区間か救済へ必ず置き換えるため、この時点で別の同一区間を取得しない。
        bool needCached = frames.Count == 0 && (!_usesOwnedRaw || value.Segment is not { SegmentId: > 0 });
        using var cachedCompletion = needCached ? ReadCacheExact(value.Request, "VmCompletion", out _) : null;
        if (cachedCompletion != null) frames = cachedCompletion.Frames;
        if (context.Kind == RequestKind.After) _afterRequests.TryRemove(context.Generation, out _);
        if (!_usesOwnedRaw)
        {
            PostRaw(context, frames, () => ApplyCompleted(context, value, frames, null));
            return;
        }
        OwnedUncachedWindow? notification = null, cached = null;
        try
        {
            PreviewSegmentSnapshot? rescue = null;
            bool rescueUnavailable = false;
            if (value.Segment is { SegmentId: > 0 } positive)
            {
                // 元の所属が消えても解放済み画素を復活させず、救済に使う少数だけを取得する。
                rescue = context.Kind == RequestKind.Before
                    ? RetainEvictedBeforeCompletion(positive, CurrentSeconds)
                    : RetainEvictedAfterCompletion(positive, context);
                if (!OwnedUncachedWindow.TryCapture(rescue.Frames, "VmCompletionRescue", out notification))
                {
                    // 部分取得は返却済みなので、失敗したdescriptorを後段の通常Captureへ渡さない。
                    rescueUnavailable = true;
                    rescue = rescue with { Frames = Array.Empty<Y4mFrame>() };
                }
            }
            else if (supplied != null) notification = supplied.ShareIfSame(supplied.Frames);
            else
            {
                // 直接呼出しの完了も、呼出元が画素を保つ間に一度だけ通知用の所有を確保する。
                var original = value.Frames.Concat(value.Segment?.Frames ?? Array.Empty<Y4mFrame>())
                    .Distinct<Y4mFrame>(ReferenceEqualityComparer.Instance).ToArray();
                if (original.Length > 0) notification = OwnedUncachedWindow.Capture(original, "VmCompletionWindow");
            }
            if (cachedCompletion != null) cached = cachedCompletion.TakeWindow();
            var sources = new CompletedWindowSources(notification, cached, rescue, rescueUnavailable);
            PostOwnership(context, sources, () => ApplyCompleted(context, value, frames, sources));
        }
        catch
        {
            notification?.Dispose();
            cached?.Dispose();
            throw;
        }
    }

    private void ApplyCompleted(RequestContext context, PreviewCompletion value, IReadOnlyList<Y4mFrame> frames,
        CompletedWindowSources? sources)
    {
        if (!IsCurrent(context)) return;
        // 途中画像はFrameReadyが1枚だけ渡す。旧世代の完了列で最新の保持区間を戻さない。
        if (context.Kind == RequestKind.After && context.Generation != Volatile.Read(ref _generation))
        { context.Segment = null; return; }
        RefreshRetainedSnapshots();
        var segment = value.Segment;
        bool evictedAfter = false;
        bool retryLimitedBeforeRescue = false;
        if (segment is { SegmentId: > 0 })
        {
            // 選択候補と時計はmetadataで解決し、表示する画素だけを後段のTry取得で守る。
            var latest = _cache.GetSegment(segment.SegmentId);
            if (latest == null)
            {
                if (context.Kind == RequestKind.After && sources?.RescueUnavailable == true)
                {
                    // 救済の画素も所属も失った場合は、表示や隣の確定前に既存の取り直しへ戻す。
                    if (PerfTrace.Enabled) PerfTrace.Write("vm.after.completion_rescue_miss", new {
                        generation = context.Generation, context.Request.CorrelationId, target = CurrentSeconds,
                        neighborDirection = context.NeighborDirection });
                    context.Segment = null;
                    _ = QueuePreview(navigation: true, neighborDirection: context.NeighborDirection,
                        requirePhysicalRefetch: segment.Clock.TrustedAbsolute);
                    return;
                }
                if (context.Kind == RequestKind.Before)
                {
                    // UI待ちの間にevictされてもBeforeの完了を捨てない。最新目標の1枚だけ残す。
                    var original = segment;
                    segment = sources?.RescueSegment ?? RetainEvictedBeforeCompletion(segment, CurrentSeconds);
                    retryLimitedBeforeRescue = sources?.RescueSegment != null
                        && ContainsBeforeFrameTarget(original.PhysicalRequest, original.Frames, CurrentSeconds, original)
                        && !ContainsBeforeFrameTarget(segment.PhysicalRequest, segment.Frames, CurrentSeconds, segment);
                }
                else
                {
                    segment = sources?.RescueSegment ?? RetainEvictedAfterCompletion(segment, context);
                    evictedAfter = true;
                }
                frames = segment.Frames;
            }
            else
            {
                segment = latest;
                frames = latest.Frames;
            }
        }
        if (segment != null)
        {
            using var selectedSource = sources?.ShareMatch(segment.Frames);
            context.SetSegmentSharing(segment, selectedSource);
        }
        if (context.Kind == RequestKind.Before)
        {
            context.BeforeEarlyFallbackReason = value.EarlyDisplayFallbackReason
                ?? (value.IsReplay ? "時刻条件の再取得に切り替わった" : context.BeforeEarlyFallbackReason);
            CaptureBeforeFallbackDiagnostic(value.Result.StandardError);
            CompleteBeforeWithWindows(context, frames, sources, retryLimitedBeforeRescue);
            return;
        }
        if (value.IsReplay) PrepareReplayUi(context);
        if (_lastFailedAfter == Volatile.Read(ref _generation)) return;
        _intervalRequest = segment?.PhysicalRequest ?? value.Request;
        _intervalLogicalRequest = value.Request;
        _intervalSegment = segment;
        using var intervalSource = sources?.ShareMatch(frames);
        if (intervalSource != null) SetIntervalFramesFromWindow(frames, intervalSource);
        else SetIntervalFrames(frames, context);
        if (evictedAfter)
        {
            if (frames.Count == 0)
            {
                ClearPendingNeighbor(context.Generation);
                _lastFailedAfter = context.Generation;
                _host.ShowPreviewFailure(PreviewFailureKind.NoDisplayableFrames, value.Result.StandardError, null);
                return;
            }
            if (context.NeighborDirection == 0 || context.NeighborResolved)
            {
                // 正ID付きの旧変換仕事を失効させ、少数保持の画像を再取得なしで反映する。
                Interlocked.Increment(ref context.DisplayRevision);
                DisplayFrame(frames[0], context);
            }
        }
        _lastCompletedAfter = Math.Max(_lastCompletedAfter, context.Generation);
        var afterRequest = context.NeighborDirection != 0 ? CompleteNeighbor(context, frames) : value.Request;
        if (frames.Count == 0 && context.Generation == Volatile.Read(ref _generation) && _lastQueuedAfter < context.Generation)
        { _lastFailedAfter = context.Generation; _host.ShowPreviewFailure(PreviewFailureKind.NoDisplayableFrames, value.Result.StandardError, null); return; }
        if (ApplyReachedInputEnd(context, value, segment))
        {
            afterRequest = RetargetInterval(afterRequest ?? value.Request, CurrentSeconds);
            _intervalLogicalRequest = afterRequest;
        }
        _host.ErrorText = null;
        _host.LogText = AppendBeforeFallbackDiagnostic(value.Result.StandardError);
        _host.ShowElapsed(_clock.GetElapsedTime(context.Started).TotalSeconds);
        if (context.Generation == Volatile.Read(ref _generation))
        {
            if (afterRequest != null && (context.NeighborDirection == 0 || context.ResolvedTargetSeconds.HasValue))
                _completedAfterStatus = value.TargetNotReached ? PreviewProgress.DoneTargetNotReached : value.FromCache ? PreviewProgress.DoneCache : PreviewProgress.Done;
            QueueBefore(afterRequest ?? value.Request, context.Generation);
        }
        UpdateNavigationStatus();
    }
    private void OnFailed(PreviewFailure value)
    {
        var context = ContextFor(value.Request);
        if (context == null) return;
        context.Segment = null;
        string diagnostics = value.Diagnostic ?? value.Result?.StandardError ?? (value.Exception as PreviewBackendException)?.StandardError ?? context.Diagnostics;
        if (context.Kind == RequestKind.After) _afterRequests.TryRemove(context.Generation, out _);
        Action apply = () =>
        {
            if (!IsCurrent(context)) return;
            context.Segment = null;
            if (context.Kind == RequestKind.Before)
            {
                BeforeUnavailableForDuration();
                CaptureBeforeFallbackDiagnostic(AppendExceptionDetails(diagnostics, value.Exception));
                _beforeFinished = true;
                _host.IsBeforeBusy = false;
                Interlocked.Increment(ref _beforeImageGeneration);
                ClearBeforeDisplayedRaw();
                ClearBeforeImage();
                return;
            }
            if (context.Generation != Volatile.Read(ref _generation)) return;
            ClearPendingNeighbor(context.Generation);
            _lastFailedAfter = context.Generation;
            _host.ShowElapsed(_clock.GetElapsedTime(context.Started).TotalSeconds);
            _host.ShowExceptionError(value.Reason, diagnostics, value.Exception);
        };
        if (context.Kind == RequestKind.Before) Track(PostAsync(apply));
        else _post(apply);
    }
    private void DisplayFrame(Y4mFrame frame, RequestContext context)
    {
        long revision = Volatile.Read(ref context.DisplayRevision);
        if (context.Generation < _lastQueuedAfter || context.Generation == _lastQueuedAfter && revision <= _lastQueuedRevision) return;
        _lastQueuedAfter = context.Generation;
        _lastQueuedRevision = revision;
        RememberNavigationAnchor(frame, context);
        var clock = context.Segment?.Clock ?? ClockFor(context.Request, new[] { frame });
        EnqueueImage(new ImageWork(frame, context.Generation, context.EditEpoch,
            DisplaySeconds: PerfTrace.Enabled && frame.Xts.HasValue ? clock.ToInputSeconds(frame, 0, context.Request.Fps) : null,
            SegmentId: context.Segment?.SegmentId, TrustedAbsolute: clock.TrustedAbsolute,
            RevisionContext: context, DisplayRevision: revision, Timing: GetFrameTiming(frame, context.Request, context.Segment)));
    }
    internal static string AppendExceptionDetails(string diagnostics, Exception? exception)
    {
        string[] lines = ExceptionDiagnostics.Lines(exception);
        if (lines.Length == 0) return diagnostics;
        string details = CoreText.ExceptionDetails + Environment.NewLine + string.Join(Environment.NewLine, lines);
        return string.IsNullOrWhiteSpace(diagnostics) ? details : diagnostics + Environment.NewLine + details;
    }

}

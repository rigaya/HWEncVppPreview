namespace HWEncVppPreview.Core;

public sealed partial class EncoderPreviewSession
{
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_taskLock)
        {
            if (_disposeTask != null) return new(_disposeTask);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = CompleteShutdownAsync(completion);
        return new(completion.Task);
    }
    private async Task CompleteShutdownAsync(TaskCompletionSource completion)
    {
        try
        {
            await DispatchOperationShutdown(DisposeCoreAsync).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        TraceDisposeStage("enter");
        IsDisposed = true;
        _rawOwnershipScope?.ClosePool();
        _host.BeginShutdown();
        DropRawPosts(_ => true);
        IsInitialized = false;
        Interlocked.Increment(ref _generation);
        using var deadline = new ShutdownDeadline(_clock, _timeouts.ViewModelDispose);
        Task cancellations = Task.Run(() =>
        {
            if (CancelEncoderInitialization() is { } initialization) Track(initialization);
            TraceDisposeStage("duration_cancel.begin");
            CancelDuration();
            TraceDisposeStage("duration_cancel.end");
            TraceDisposeStage("lifetime_cancel.begin");
            _lifetime.Cancel();
            TraceDisposeStage("lifetime_cancel.end");
            TraceDisposeStage("before_cancel.begin");
            CancelBefore(updatePresentation: false);
            TraceDisposeStage("before_cancel.end");
            TraceDisposeStage("preparation_cancel.begin");
            lock (_taskLock) _preparationCancellation?.Cancel();
            TraceDisposeStage("preparation_cancel.end");
        });
        Controller.OwnedSegmentUpdated -= OnOwnedSegmentUpdated;
        Controller.OwnedFrameReady -= OnOwnedFrameReady;
        Controller.OwnedCompleted -= OnOwnedCompleted;
        Controller.ReplayStarted -= OnReplayStarted;
        Controller.SegmentUpdated -= OnSegmentUpdated;
        Controller.FrameReady -= OnFrameReady;
        Controller.Completed -= OnCompleted;
        Controller.Failed -= OnFailed;
        // Coreが期限で残したlogical taskをIdle経由でもう一度待たない。
        // 同期取消・検査・変換・UI反映にも同じ総期限を適用し、残る処理は遅延観測する。
        TraceDisposeStage("controller_dispose.begin");
        Task controller = Task.Run(async () => await Controller.DisposeAsync().ConfigureAwait(false));
        await WaitForVmShutdownAsync(cancellations, deadline, "cancellations").ConfigureAwait(false);
        await WaitForVmShutdownAsync(controller, deadline, "controller").ConfigureAwait(false);
        if (Controller.ShutdownIncomplete) Interlocked.Exchange(ref _shutdownIncomplete, 1);
        TraceDisposeStage("controller_dispose.end");
        TraceDisposeStage("probe_dispose.begin");
        Task probe = Task.Run(async () => await DisposeProbeAsync().ConfigureAwait(false));
        await WaitForVmShutdownAsync(probe, deadline, "probe").ConfigureAwait(false);
        TraceDisposeStage("probe_dispose.end");
        TraceDisposeStage("wait_idle.begin");
        Task pending = Task.Run(async () =>
        {
            Task[] tasks;
            lock (_taskLock) tasks = _tasks.ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        });
        await WaitForVmShutdownAsync(pending, deadline, "vm_tasks").ConfigureAwait(false);
        TraceDisposeStage("wait_idle.end");
        TraceDisposeStage("cleanup.begin");
        Task cleanup = PostAsync(() =>
        {
            HasDuration = false;
            DurationSeconds = 0;
            _host.ShowDuration(null);
            _host.ClearAfterImage();
            ClearBeforeImage();
            _host.IsBeforeBusy = false;
            ReleaseImageWork();
            SetRawFrame(ref _displayedFrame, ref _displayedRaw, null, "VmDisplayed");
            ClearBeforeDisplayedRaw();
            _host.RefreshColorInfo();
            ClearInterval();
            ClearBeforeInterval();
            _cache.Clear();
            Interlocked.Exchange(ref _pendingSegmentUpdate, null)?.Dispose();
            // Dispose後もウィンドウ等がVMを参照し得るため、完了した要求の区間もここで手放す。
            if (_context != null) _context.Segment = null;
            foreach (var context in _afterRequests.Values) context.Segment = null;
            _afterRequests.Clear();
            Volatile.Write(ref _context, null);
            if (_beforeExecutionContext is { } beforeContext) beforeContext.Segment = null;
            _beforeExecutionContext = null;
            _beforePhysicalWindow = null;
        });
        await WaitForVmShutdownAsync(cleanup, deadline, "ui_cleanup").ConfigureAwait(false);
        Task resources = Task.WhenAll(cancellations, controller, probe, pending);
        ObserveVmShutdownTask(resources);
        if (resources.IsCompleted) _lifetime.Dispose();
        else
        {
            // 未回収の取消callbackや準備taskとCTSのDisposeを競合させない。
            Task release = resources.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            ObserveVmShutdownTask(release);
        }
        await StopOperationDispatcherAsync().ConfigureAwait(false);
        AssertOwnedRawReleased();
        TraceDisposeStage(Volatile.Read(ref _shutdownIncomplete) == 0 ? "complete" : "incomplete");
    }
    private async Task WaitForVmShutdownAsync(Task task, ShutdownDeadline deadline, string stage)
    {
        try { await deadline.WaitAsync(task).ConfigureAwait(false); }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _shutdownIncomplete, 1);
            System.Diagnostics.Trace.TraceWarning("Viewer shutdown incomplete ({0}): {1}", stage, exception.Message);
            TraceDisposeStage("incomplete." + stage);
            ObserveVmShutdownTask(task);
        }
    }
    private static void ObserveVmShutdownTask(Task task) => _ = task.ContinueWith(completed =>
    {
        System.Diagnostics.Trace.TraceWarning("Viewer shutdown late failure: {0}", completed.Exception);
    }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static void TraceDisposeStage(string stage)
    {
        if (!PerfTrace.Enabled) return;
        // 終了途中の停止でも最後に到達した段階を失わず、UI文脈への継続も識別する。
        PerfTrace.Write("vm.dispose.stage", new { stage, synchronizationContext = SynchronizationContext.Current?.GetType().FullName });
        PerfTrace.Flush();
    }

    private Task? _disposeTask;
    private int _shutdownIncomplete;
    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertOwnedRawReleased()
    {
        if (_rawOwnershipScope == null) return;
        var state = _rawOwnershipScope.Capture();
        string remaining = System.Text.Json.JsonSerializer.Serialize(state.OwnerKinds);
        string message = $"raw所有権: owners={state.LiveOwners}, tokens={state.LiveTokens}, bytes={state.LiveBytes}, kinds={remaining}";
        if (Volatile.Read(ref _shutdownIncomplete) != 0)
        {
            System.Diagnostics.Trace.TraceWarning("終了未完了のため台帳0判定を保留: {0}", message);
            return;
        }
        System.Diagnostics.Debug.Assert(state.IsEmpty, message);
    }
}

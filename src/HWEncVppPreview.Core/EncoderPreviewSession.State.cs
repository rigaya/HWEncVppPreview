using System.Collections.Concurrent;

namespace HWEncVppPreview.Core;

public sealed partial class EncoderPreviewSession
{
    internal void ClearInterval()
    {
        SetIntervalFrames(Array.Empty<Y4mFrame>());
        _intervalRequest = null;
        _intervalLogicalRequest = null;
        _intervalSegment = null;
        SetRawAnchor(ref _navigationAnchor, ref _anchorRaw, null);
    }

    internal bool IsCurrent(long generation) => !IsDisposed && generation == Volatile.Read(ref _generation);
    internal bool IsCurrent(RequestContext context) => context.Kind == RequestKind.Before
        ? !IsDisposed && context.EditEpoch == Volatile.Read(ref _editEpoch)
            && ReferenceEquals(Volatile.Read(ref _beforeExecutionContext), context)
            && context.BeforeCancellation?.IsCancellationRequested != true
        : !IsDisposed && context.EditEpoch == Volatile.Read(ref _editEpoch) && context.Generation >= _lastDisplayedAfter;
    internal RequestContext? ContextFor(PreviewRequest request)
    {
        if (_afterRequests.TryGetValue(request.CorrelationId, out var after) && SameRequest(after.Request, request)) return after;
        var before = Volatile.Read(ref _beforeExecutionContext);
        if (before != null && SameRequest(before.Request, request)) return before;
        var context = Volatile.Read(ref _context);
        return context != null && SameRequest(context.Request, request) ? context : null;
    }
    internal RequestContext? ContextForPhysical(PreviewRequest request)
    {
        // 物理コピーでは時刻flagを除去するため、対応付けは一意な論理CorIdを使う。
        RequestContext? context = _afterRequests.TryGetValue(request.CorrelationId, out var after) ? after
            : Volatile.Read(ref _beforeExecutionContext);
        if (context?.Request.CorrelationId != request.CorrelationId) context = Volatile.Read(ref _context);
        return context != null && context.Request.CorrelationId == request.CorrelationId
            && context.Request.EncoderPath == request.EncoderPath && context.Request.InputFile == request.InputFile
            && context.Request.Fps == request.Fps ? context : null;
    }
    private static bool SameRequest(PreviewRequest a, PreviewRequest b) => a.CorrelationId == b.CorrelationId && a.EncoderPath == b.EncoderPath && a.InputFile == b.InputFile
        && a.TargetSeconds == b.TargetSeconds && a.Fps == b.Fps && a.InputArgs.SequenceEqual(b.InputArgs) && a.VppArgs.SequenceEqual(b.VppArgs);

    internal void Track(Task task)
    {
        lock (_taskLock) _tasks.Add(task);
        _ = task.ContinueWith(completed => { lock (_taskLock) _tasks.Remove(completed); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    internal Task PostAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() => { try { action(); completion.TrySetResult(); } catch (Exception exception) { completion.TrySetException(exception); } });
        return completion.Task;
    }
    private long _generation;
    private RequestContext? _context;
    private InputInfo? _inputInfo;
    private string _metadataEncoder = "";
    private PreviewRequest? _intervalRequest, _intervalLogicalRequest;
    private IReadOnlyList<Y4mFrame> _intervalFrames = Array.Empty<Y4mFrame>();
    private CancellationTokenSource? _preparationCancellation;
    private Task _preparation = Task.CompletedTask;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _taskLock = new();
    private readonly HashSet<Task> _tasks = new();
    private readonly ConcurrentDictionary<long, RequestContext> _afterRequests = new();
    private long _editEpoch, _lastQueuedAfter, _lastDisplayedAfter, _lastCompletedAfter, _lastFailedAfter;
    private long _lastQueuedRevision, _lastDisplayedRevision;
    private bool _latestIntentNavigation;
    internal enum NavigationOperation { Seek, Page, Frame }
    private long _concurrentComparisonGeneration;
    private PreviewProgress _completedAfterStatus = PreviewProgress.Done;
    private NeighborIntent? _pendingNeighbor;
    private PreviewRequest? _beforeIntervalRequest;
    private IReadOnlyList<Y4mFrame> _beforeIntervalFrames = Array.Empty<Y4mFrame>();
    private RequestContext? _beforeExecutionContext;

    private bool _seekDragging;
    private readonly FrameCache _cache;
    private readonly RawOwnershipScope? _rawOwnershipScope;
    private readonly bool _usesOwnedRaw;
    private readonly Action<Action> _post;
    private readonly IEncoderPreviewHost _host;

    internal bool IsInitialized { get; set; }
    internal bool IsDisposed { get; private set; }
    internal bool IsSuspended { get; set; }
    private double _currentSeconds, _durationSeconds;
    private bool _hasDuration;
    internal double CurrentSeconds => _currentSeconds;
    internal bool HasDuration
    {
        get => _hasDuration;
        set
        {
            if (_hasDuration == value) return;
            _hasDuration = value;
            _host.StateChanged(nameof(HasDuration));
        }
    }
    internal double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (_durationSeconds.Equals(value)) return;
            _durationSeconds = value;
            _host.StateChanged(nameof(DurationSeconds));
        }
    }

    internal void RestoreTime(double seconds) => _currentSeconds = seconds;

    private void SetTimeOnly(double seconds)
    {
        _currentSeconds = seconds;
        _host.StateChanged(nameof(CurrentSeconds));
        _host.ShowTime(seconds);
    }

    internal void ClearInputInfo()
    {
        CancelDuration();
        _inputInfo = null;
        HasDuration = false;
        DurationSeconds = 0;
        _host.ShowDuration(null);
    }

    internal Task Preparation => _preparation;
    internal object TaskSync => _taskLock;
    internal CancellationToken LifetimeToken => _lifetime.Token;

    internal async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_taskLock) tasks = _tasks.ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            await WaitForBackendIdleAsync().ConfigureAwait(false);
            bool idle = false;
            await PostAsync(() =>
            {
                // 区間通知が集約中に次の通知を予約すると、1回の配送境界を越えてBeforeを始める。
                lock (_taskLock)
                    idle = _tasks.Count == 0 && (IsDisposed ||
                        Volatile.Read(ref _pendingSegmentUpdate) == null && Volatile.Read(ref _segmentPostQueued) == 0);
            }).ConfigureAwait(false);
            if (idle) return;
        }
    }

}

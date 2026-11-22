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

    internal bool IsCurrent(long generation) => !_host.IsDisposed && generation == Volatile.Read(ref _generation);
    internal bool IsCurrent(RequestContext context) => context.Kind == RequestKind.Before
        ? !_host.IsDisposed && context.EditEpoch == Volatile.Read(ref _editEpoch)
            && ReferenceEquals(Volatile.Read(ref _beforeExecutionContext), context)
            && context.BeforeCancellation?.IsCancellationRequested != true
        : !_host.IsDisposed && context.EditEpoch == Volatile.Read(ref _editEpoch) && context.Generation >= _lastDisplayedAfter;
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
    internal long _generation;
    internal RequestContext? _context;
    internal InputInfo? _inputInfo;
    internal string _metadataEncoder = "";
    internal PreviewRequest? _intervalRequest, _intervalLogicalRequest;
    internal IReadOnlyList<Y4mFrame> _intervalFrames = Array.Empty<Y4mFrame>();
    internal CancellationTokenSource? _preparationCancellation;
    internal Task _preparation = Task.CompletedTask;
    internal readonly CancellationTokenSource _lifetime = new();
    internal readonly object _taskLock = new();
    internal readonly HashSet<Task> _tasks = new();
    internal readonly ConcurrentDictionary<long, RequestContext> _afterRequests = new();
    internal long _editEpoch, _lastQueuedAfter, _lastDisplayedAfter, _lastCompletedAfter, _lastFailedAfter;
    internal long _lastQueuedRevision, _lastDisplayedRevision;
    private bool _latestIntentNavigation;
    internal enum NavigationOperation { Seek, Page, Frame }
    private long _concurrentComparisonGeneration;
    internal PreviewProgress _completedAfterStatus = PreviewProgress.Done;
    private NeighborIntent? _pendingNeighbor;
    internal PreviewRequest? _beforeIntervalRequest;
    internal IReadOnlyList<Y4mFrame> _beforeIntervalFrames = Array.Empty<Y4mFrame>();
    internal RequestContext? _beforeExecutionContext;

    private bool _seekDragging;
    private readonly FrameCache _cache;
    private readonly bool _usesOwnedRaw;
    private readonly Action<Action> _post;
    private readonly IEncoderPreviewHost _host;
}

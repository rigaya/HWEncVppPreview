namespace HWEncVppPreview.Core;

public sealed partial class PreviewController
{
    private readonly RawOwnershipScope? _rawScope;
    internal event Action<OwnedPreviewEnvelope>? OwnedFrameReady;
    internal event Action<OwnedPreviewEnvelope>? OwnedCompleted;
    internal event Action<OwnedPreviewEnvelope>? OwnedSegmentUpdated;
    internal PreviewController(RawOwnershipScope scope, IPreviewBackend? backend = null, IPreviewBackend? fallback = null,
        FrameCache? cache = null, TimeProvider? timeProvider = null, TimeSpan? debounce = null, TimeSpan? watchdog = null, bool enableFallback = true,
        IPreviewBackend? backgroundBackend = null, IPreviewBackend? backgroundFallback = null, TimeSpan? disposeGrace = null, TimeSpan? disposeTimeout = null)
        : this(backend, fallback, cache ?? new FrameCache(scope), timeProvider, debounce, watchdog, enableFallback, backgroundBackend, backgroundFallback, disposeGrace, disposeTimeout)
    {
        _rawScope = scope;
        if (!ReferenceEquals(_cache.OwnershipScope, scope))
            throw new ArgumentException("raw scopeが異なるcacheです。", nameof(cache));
    }
    private bool TryPrepareFrameNotification(PreviewFrameResult value, out OwnedPreviewEnvelope? owned)
    {
        owned = null;
        if (_rawScope == null) return true;
        System.Diagnostics.Debug.Assert(FrameReady == null, "owned通知では公開FrameReadyを購読できません。");
        return OwnedPreviewEnvelope.TryCreateFrame(value, out owned);
    }

    private void EmitPreparedFrame(PreviewFrameResult value, OwnedPreviewEnvelope? owned)
    {
        // 取得成功の確認と通知状態の確定を呼出元で順序付け、ここで同じ画素を取得し直さない。
        if (owned != null) OwnedFrameReady?.Invoke(owned);
        else EmitFrame(value);
    }

    private void EmitFrame(PreviewFrameResult value)
    {
        if (_rawScope == null)
        {
            FrameReady?.Invoke(value);
            return;
        }
        System.Diagnostics.Debug.Assert(FrameReady == null, "owned通知では公開FrameReadyを購読できません。");
        using var owned = new OwnedPreviewEnvelope(frame: value);
        OwnedFrameReady?.Invoke(owned);
    }
    private void EmitCompletion(PreviewCompletion value)
    {
        if (_rawScope == null)
        {
            Completed?.Invoke(value);
            return;
        }
        System.Diagnostics.Debug.Assert(Completed == null, "owned通知では公開Completedを購読できません。");
        using var owned = new OwnedPreviewEnvelope(completed: value);
        OwnedCompleted?.Invoke(owned);
    }
    private void EmitSegment(PreviewSegmentUpdate value)
    {
        if (_rawScope == null)
        {
            SegmentUpdated?.Invoke(value);
            return;
        }
        System.Diagnostics.Debug.Assert(SegmentUpdated == null, "owned通知では公開SegmentUpdatedを購読できません。");
        using var owned = new OwnedPreviewEnvelope(update: value);
        OwnedSegmentUpdated?.Invoke(owned);
    }
    private sealed class RawDisposableBag : IDisposable
    {
        private readonly List<IDisposable> _items = new();
        internal void Add(IDisposable item) => _items.Add(item);
        public void Dispose()
        {
            foreach (var item in _items)
                item.Dispose();
            _items.Clear();
        }
    }
}

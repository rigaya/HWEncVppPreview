using System.Collections.ObjectModel;

namespace HWEncVppPreview.Core;

// 既存の選択/照合は同じframeを使い、listの各保持だけを明示tokenにする。
internal sealed class RawFrameList(string kind, Action<IDisposable>? release = null) : Collection<Y4mFrame>, IReadOnlyList<Y4mFrame>, IDisposable
{
    private readonly List<RawFrameLease?> _leases = new();
    private bool _hasOwned;
    private void Drop(IDisposable? item)
    {
        if (item == null)
            return;
        if (release == null)
            item.Dispose();
        else
            release(item);
    }
    protected override void InsertItem(int index, Y4mFrame item)
    {
        var retained = item.RawOwner == null ? null : RawFrameLease.Capture(item, kind);
        try
        {
            base.InsertItem(index, item);
            _leases.Insert(index, retained);
            _hasOwned |= item.RawOwner != null;
        }
        catch
        {
            retained?.Dispose();
            throw;
        }
    }
    protected override void RemoveItem(int index)
    {
        var lease = _leases[index];
        _leases.RemoveAt(index);
        base.RemoveItem(index);
        Drop(lease);
    }
    protected override void SetItem(int index, Y4mFrame item)
    {
        var lease = item.RawOwner == null ? null : RawFrameLease.Capture(item, kind);
        var old = _leases[index];
        base.SetItem(index, item);
        _leases[index] = lease;
        _hasOwned |= item.RawOwner != null;
        Drop(old);
    }
    protected override void ClearItems()
    {
        var leases = _leases.ToArray();
        _leases.Clear();
        base.ClearItems();
        foreach (var lease in leases)
            Drop(lease);
    }
    internal void AddRange(IEnumerable<Y4mFrame> frames)
    {
        foreach (var frame in frames)
            Add(frame);
    }
    internal ReadOnlyCollection<Y4mFrame> AsReadOnly() => new(this);
    public void Dispose()
    {
        if (_hasOwned)
            Clear();
    }
}

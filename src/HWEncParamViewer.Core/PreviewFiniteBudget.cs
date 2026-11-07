namespace HWEncParamViewer.Core;

/// <summary>前景と背景の有限取得が同時に使う一時rawを、合計1GiB以内に分ける。</summary>
internal sealed class PreviewFiniteBudget
{
    internal const long CapacityBytes = 1L << 30;
    private readonly object _gate = new();
    private long _bytes;

    internal long SizeBytes
    {
        get
        {
            lock (_gate) return _bytes;
        }
    }

    internal Window Open(long cacheCapacity, long? windowLimit = null)
        => new(this, Math.Min(windowLimit ?? CapacityBytes,
            Math.Min(cacheCapacity > 0 ? cacheCapacity : CapacityBytes, CapacityBytes / 2)));

    internal sealed class Window(PreviewFiniteBudget owner, long capacity) : IDisposable
    {
        private long _bytes;
        internal long CapacityBytes => capacity;

        internal long SizeBytes
        {
            get
            {
                lock (owner._gate) return _bytes;
            }
        }

        internal bool TryAdd(long bytes)
        {
            lock (owner._gate)
            {
                if (bytes < 0 || bytes > capacity - _bytes || bytes > PreviewFiniteBudget.CapacityBytes - owner._bytes) return false;
                _bytes += bytes;
                owner._bytes += bytes;
                return true;
            }
        }

        internal void Remove(long bytes)
        {
            lock (owner._gate)
            {
                _bytes -= bytes;
                owner._bytes -= bytes;
            }
        }

        internal void Reset()
        {
            lock (owner._gate)
            {
                owner._bytes -= _bytes;
                _bytes = 0;
            }
        }

        public void Dispose() => Reset();
    }
}

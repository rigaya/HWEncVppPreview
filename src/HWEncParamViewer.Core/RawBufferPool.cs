namespace HWEncParamViewer.Core;

// 所有終了後の配列だけを保持するため、入力や画像形式ではなく必要な長さだけで再利用できる。
internal sealed class RawBufferPool
{
    internal const long DefaultFreeLimitBytes = 1L << 30;
    internal const int DefaultMaximumFrameBytes = 64 << 20;
    private readonly object _gate = new();
    private readonly LinkedList<byte[]> _free = new();
    private readonly long _freeLimit;
    private readonly int _maximumFrame;
    private bool _closed;
    private int _returnedPoisonForTesting = -1;
    private long _freeBytes, _loanedBytes, _peakFreeBytes, _peakLoanedBytes, _peakStrongBytes;
    private long _hits, _misses, _newArrays, _newBytes, _returned, _keptReturns, _droppedReturns;
    private long _discardedFreeBytes, _discardedFreeBuffers, _dropOversize, _dropCapacity, _dropClosed;

    internal sealed record Snapshot(bool Enabled, bool Closed, long FreeBytes, int FreeBuffers,
        long LoanedBytes, long PeakLoanedBytes, long PeakFreeBytes, long PeakStrongBytes,
        long Hits, long Misses, long NewArrays, long NewBytes, long Returned, long KeptReturns, long DroppedReturns,
        long DiscardedFreeBytes, long DiscardedFreeBuffers, long DropOversize, long DropCapacity, long DropClosed,
        long FreeLimitBytes, int MaximumFrameBytes);
    internal static Snapshot Disabled { get; } = new(false, false, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    internal RawBufferPool(long freeLimitBytes = DefaultFreeLimitBytes, int maximumFrameBytes = DefaultMaximumFrameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(freeLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumFrameBytes);
        _freeLimit = freeLimitBytes;
        _maximumFrame = maximumFrameBytes;
    }
    internal void SetReturnedBufferPoisonForTesting(byte value) => Volatile.Write(ref _returnedPoisonForTesting, value);
    internal byte[] Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        lock (_gate)
        {
            if (!_closed)
            {
                for (var node = _free.Last; node != null; node = node.Previous)
                {
                    if (node.Value.Length != length) continue;
                    byte[] reused = node.Value;
                    _free.Remove(node);
                    _freeBytes -= length;
                    ++_hits;
                    AddLoanCore(length);
                    return reused;
                }
            }
        }
        // 大きな確保やガベージコレクションを排他内で起こさず、不足時は待たずに新規配列へ進む。
        byte[] buffer = new byte[length];
        lock (_gate)
        {
            ++_misses; ++_newArrays; _newBytes += length;
            AddLoanCore(length);
        }
        return buffer;
    }
    private void AddLoanCore(int length)
    {
        _loanedBytes += length;
        _peakLoanedBytes = Math.Max(_peakLoanedBytes, _loanedBytes);
        _peakStrongBytes = Math.Max(_peakStrongBytes, _loanedBytes + _freeBytes);
    }
    internal void Return(byte[] buffer)
    {
        // 入力変更をまたいでも所有中は返らず、次の読取は全量を上書きするので、配列の出自は見ない。
        int poison = Volatile.Read(ref _returnedPoisonForTesting);
        if (poison >= 0) Array.Fill(buffer, (byte)poison);
        var node = buffer.Length <= _maximumFrame && buffer.Length <= _freeLimit
            ? new LinkedListNode<byte[]>(buffer) : null;
        lock (_gate)
        {
            _loanedBytes -= buffer.Length;
            ++_returned;
            if (_closed) { ++_dropClosed; ++_droppedReturns; return; }
            if (buffer.Length > _maximumFrame) { ++_dropOversize; ++_droppedReturns; return; }
            if (buffer.Length > _freeLimit) { ++_dropCapacity; ++_droppedReturns; return; }
            // 最近返った配列を残し、上限に収まるまで長く使われていない配列から捨てる。
            while (buffer.Length > _freeLimit - _freeBytes) DiscardFirstCore();
            _free.AddLast(node!);
            _freeBytes += buffer.Length;
            ++_keptReturns;
            _peakFreeBytes = Math.Max(_peakFreeBytes, _freeBytes);
            _peakStrongBytes = Math.Max(_peakStrongBytes, _loanedBytes + _freeBytes);
        }
    }
    private void DiscardFirstCore()
    {
        int length = _free.First!.Value.Length;
        _free.RemoveFirst();
        _freeBytes -= length;
        _discardedFreeBytes += length;
        ++_discardedFreeBuffers;
    }
    internal void ClearFreeBuffers()
    {
        lock (_gate) while (_free.Count != 0) DiscardFirstCore();
    }
    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            while (_free.Count != 0) DiscardFirstCore();
        }
    }
    internal Snapshot Capture()
    {
        bool closed;
        int count;
        long free, lent, peakLent, peakFree, peakStrong, hits, misses, arrays, bytes, returned, kept, dropped;
        long discardedBytes, discardedBuffers, oversize, capacity, closedDrops;
        lock (_gate)
        {
            closed = _closed; count = _free.Count; free = _freeBytes; lent = _loanedBytes;
            peakLent = _peakLoanedBytes; peakFree = _peakFreeBytes; peakStrong = _peakStrongBytes;
            hits = _hits; misses = _misses; arrays = _newArrays; bytes = _newBytes;
            returned = _returned; kept = _keptReturns; dropped = _droppedReturns;
            discardedBytes = _discardedFreeBytes; discardedBuffers = _discardedFreeBuffers;
            oversize = _dropOversize; capacity = _dropCapacity; closedDrops = _dropClosed;
        }
        return new(true, closed, free, count, lent, peakLent, peakFree, peakStrong,
            hits, misses, arrays, bytes, returned, kept, dropped, discardedBytes, discardedBuffers,
            oversize, capacity, closedDrops, _freeLimit, _maximumFrame);
    }
}

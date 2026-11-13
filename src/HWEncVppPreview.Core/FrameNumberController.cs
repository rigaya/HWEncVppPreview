namespace HWEncVppPreview.Core;

/// <summary>番号指定で取得した映像と、そのフレームのKFM属性。</summary>
public sealed record NumberedFrame(int Position, Y4mFrame Frame, KfmFrameProperties? Kfm, bool IsKept);

/// <summary>番号指定の取得・キャッシュ・KFMの局所的な間引きを管理する。</summary>
public sealed class FrameNumberController : IAsyncDisposable
{
    private readonly IFrameNumberSource _source;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, (NumberedFrame Frame, LinkedListNode<int> Node)> _cache = new();
    private readonly LinkedList<int> _lru = new();
    private readonly Dictionary<int, KfmFrameProperties?> _properties = new();
    private readonly long _maxBytes;
    private long _bytes;
    private bool _disposed;
    public FrameNumberInfo Info => _source.Info;
    public FrameNumberController(IFrameNumberSource source, long maxCacheBytes = 128L * 1024 * 1024)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegative(maxCacheBytes);
        _maxBytes = maxCacheBytes;
    }

    private void Check(int position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)position >= (uint)Info.NumFrames) throw new ArgumentOutOfRangeException(nameof(position));
    }
    private KfmFrameProperties? Properties(int position, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_properties.TryGetValue(position, out var cached)) return cached;
        var value = _source.GetProperties(position);
        token.ThrowIfCancellationRequested();
        if (value is { Duration: < 1 or > 4 } || value is { FrameType: < 1 or > 4 }) throw new InvalidDataException("KFM属性が不正です。");
        if (_properties.Count >= 8192) _properties.Clear();
        _properties[position] = value;
        return value;
    }
    private bool Kept(int position, CancellationToken token)
    {
        // P2の測定と同じサイクル境界から2サイクル前を起点にする。
        int cursor = Math.Max(0, position / 10 * 10 - 20);
        while (cursor < position)
        {
            var prop = Properties(cursor, token);
            if (prop is null) return true;
            cursor += prop.Value.Duration;
        }
        return cursor == position;
    }
    private NumberedFrame Read(int position, CancellationToken token)
    {
        Check(position);
        token.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(position, out var cached))
        {
            _lru.Remove(cached.Node); _lru.AddFirst(cached.Node);
            return cached.Frame;
        }
        var props = Properties(position, token);
        bool kept = props is null || Kept(position, token);
        var frame = _source.GetFrame(position);
        token.ThrowIfCancellationRequested();
        var result = new NumberedFrame(position, frame, props, kept);
        long bytes = frame.Data.LongLength;
        if (bytes <= _maxBytes)
        {
            while (_bytes + bytes > _maxBytes && _lru.Last is { } tail)
            {
                var old = _cache[tail.Value]; _cache.Remove(tail.Value); _lru.RemoveLast(); _bytes -= old.Frame.Frame.Data.LongLength;
            }
            var node = _lru.AddFirst(position); _cache.Add(position, (result, node)); _bytes += bytes;
        }
        return result;
    }
    private Task<T> Run<T>(Func<CancellationToken, T> operation, CancellationToken token) => Task.Run(async () =>
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return operation(token); }
        finally { _gate.Release(); }
    }, token);

    /// <summary>指定位置の画像を取得する。間引き対象でも要求位置自体を返す。</summary>
    public Task<NumberedFrame> GetFrameAsync(int position, CancellationToken cancellationToken = default)
        => Run(token => Read(position, token), cancellationToken);

    private int? Neighbor(int position, int direction, bool decimate, CancellationToken token)
    {
        Check(position);
        for (int n = position + direction; n >= 0 && n < Info.NumFrames; n += direction)
        {
            token.ThrowIfCancellationRequested();
            if (!decimate || Properties(n, token) is null || Kept(n, token)) return n;
        }
        return null;
    }

    /// <summary>間引きを考慮した前後のフレーム番号を返す。範囲外ならnull。</summary>
    public Task<int?> GetNeighborAsync(int position, int direction, bool decimate = true, CancellationToken cancellationToken = default)
        => Run(token =>
        {
            if (direction != -1 && direction != 1) throw new ArgumentOutOfRangeException(nameof(direction));
            return Neighbor(position, direction, decimate, token);
        }, cancellationToken);

    /// <summary>前後の表示対象を取得してキャッシュする。</summary>
    public Task PrefetchAsync(int position, int before, int after, bool decimate = true, CancellationToken cancellationToken = default)
        => Run(token =>
        {
            Check(position); ArgumentOutOfRangeException.ThrowIfNegative(before); ArgumentOutOfRangeException.ThrowIfNegative(after);
            _ = Read(position, token);
            foreach (var (direction, count) in new[] { (-1, before), (1, after) })
            {
                int current = position;
                for (int i = 0; i < count; i++)
                {
                    int? next = Neighbor(current, direction, decimate, token);
                    if (next is null) break;
                    _ = Read(next.Value, token); current = next.Value;
                }
            }
            return true;
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (!_disposed) { _disposed = true; _cache.Clear(); _lru.Clear(); _properties.Clear(); _source.Dispose(); } }
        finally { _gate.Release(); }
    }
}

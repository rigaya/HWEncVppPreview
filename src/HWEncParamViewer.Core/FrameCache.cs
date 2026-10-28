namespace HWEncParamViewer.Core;

/// <summary>成功した要求の区間を、フレームデータの合計バイト数で制限するLRUキャッシュ。</summary>
/// <remarks>
/// 呼び出し側が成功した要求だけをStoreへ渡す。キーの引数配列と値のリストは保存時にコピーする。
/// 画像の二重保持を避けるため、Y4mFrameとDataは共有する。保存後および取得結果のDataは変更しないこと。
/// ヘッダとコレクションの管理領域は容量に含めない。各操作は複数スレッドから呼び出せる。
/// </remarks>
public sealed class FrameCache
{
    private readonly object _gate = new();
    private readonly long _capacityBytes;
    private readonly Dictionary<CacheKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _sizeBytes;

    public FrameCache(long capacityBytes = 1L << 30)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        _capacityBytes = capacityBytes;
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public long SizeBytes
    {
        get { lock (_gate) return _sizeBytes; }
    }

    /// <summary>一致する区間を取得し、使用順を最新にする。失敗時は空のリストを返す。</summary>
    public bool TryGet(PreviewRequest request, out IReadOnlyList<Y4mFrame> frames)
    {
        var key = new CacheKey(request);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                frames = Array.Empty<Y4mFrame>();
                return false;
            }
            _lru.Remove(node);
            _lru.AddFirst(node);
            frames = node.Value.Frames;
            return true;
        }
    }

    /// <summary>成功した要求の全フレームを保存する。容量が0、または1区間が上限を超える場合は保存しない。</summary>
    public void Store(PreviewRequest request, IReadOnlyList<Y4mFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var key = new CacheKey(request);
        var snapshot = Array.AsReadOnly(frames.ToArray());
        long bytes = 0;
        foreach (var frame in snapshot) bytes = checked(bytes + frame.Data.LongLength);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var previous)) Remove(previous);
            if (_capacityBytes == 0 || bytes > _capacityBytes) return;
            // 引き算で比較し、巨大な上限での加算オーバーフローを避ける。
            while (_sizeBytes > _capacityBytes - bytes) Remove(_lru.Last!);
            var entry = new Entry(key, snapshot, bytes);
            _entries.Add(key, _lru.AddFirst(entry));
            _sizeBytes += bytes;
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node.Value.Key);
        _lru.Remove(node);
        _sizeBytes -= node.Value.Bytes;
    }

    private sealed record Entry(CacheKey Key, IReadOnlyList<Y4mFrame> Frames, long Bytes);

    private sealed class CacheKey : IEquatable<CacheKey>
    {
        private readonly string _encoderPath;
        private readonly string _inputFile;
        private readonly string[] _inputArgs;
        private readonly string[] _vppArgs;
        private readonly double? _seekSeconds;
        private readonly int _frameCount;

        public CacheKey(PreviewRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var plan = CommandBuilder.Plan(request);
            _encoderPath = request.EncoderPath;
            _inputFile = request.InputFile;
            _inputArgs = request.InputArgs.ToArray();
            _vppArgs = request.VppArgs.ToArray();
            _seekSeconds = plan.SeekSeconds;
            _frameCount = plan.FrameCount;
        }

        public bool Equals(CacheKey? other) => other != null
            && string.Equals(_encoderPath, other._encoderPath, StringComparison.Ordinal)
            && string.Equals(_inputFile, other._inputFile, StringComparison.Ordinal)
            && _inputArgs.SequenceEqual(other._inputArgs, StringComparer.Ordinal)
            && _vppArgs.SequenceEqual(other._vppArgs, StringComparer.Ordinal)
            && _seekSeconds == other._seekSeconds
            && _frameCount == other._frameCount;

        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_encoderPath, StringComparer.Ordinal);
            hash.Add(_inputFile, StringComparer.Ordinal);
            foreach (var arg in _inputArgs) hash.Add(arg, StringComparer.Ordinal);
            hash.Add(_inputArgs.Length);
            foreach (var arg in _vppArgs) hash.Add(arg, StringComparer.Ordinal);
            hash.Add(_vppArgs.Length);
            hash.Add(_seekSeconds);
            hash.Add(_frameCount);
            return hash.ToHashCode();
        }
    }
}

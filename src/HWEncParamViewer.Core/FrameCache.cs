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
    private readonly Dictionary<long, SegmentEntry> _segments = new();
    private long _nextSegmentId;
    private long _useSequence;
    private long? _currentSegmentId;
    private double _currentSeconds;
    private readonly HashSet<long> _activeSegments = new();
    private readonly List<PreviewRequest> _stitchDisabled = new();
    private readonly Dictionary<long, PreviewRequest> _pendingSources = new();
    private long _nextPendingId;

    public long CapacityBytes => _capacityBytes;

    public FrameCache(long capacityBytes = 1L << 30)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        _capacityBytes = capacityBytes;
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count + _segments.Count; }
    }

    public long SizeBytes
    {
        get { lock (_gate) return _sizeBytes; }
    }

    /// <summary>一致する区間を取得し、使用順を最新にする。失敗時は空のリストを返す。</summary>
    public bool TryGet(PreviewRequest request, out IReadOnlyList<Y4mFrame> frames) => TryGet(request, out frames, out _);

    /// <summary>legacyの画像と、その生Xtsを生成した実物理windowを一緒に返す。</summary>
    public bool TryGet(PreviewRequest request, out IReadOnlyList<Y4mFrame> frames, out PreviewRequest physicalRequest)
    {
        var key = new CacheKey(request);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                frames = Array.Empty<Y4mFrame>();
                physicalRequest = null!;
                return false;
            }
            _lru.Remove(node);
            _lru.AddFirst(node);
            node.Value.LastUse = ++_useSequence;
            frames = node.Value.Frames;
            physicalRequest = node.Value.PhysicalRequest;
            return true;
        }
    }

    /// <summary>成功した要求の全フレームを保存する。容量が0、または1区間が上限を超える場合は保存しない。</summary>
    public void Store(PreviewRequest request, IReadOnlyList<Y4mFrame> frames, PreviewRequest? physicalRequest = null)
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
            while (_sizeBytes > _capacityBytes - bytes)
                if (!EvictOne()) return;
            var physical = physicalRequest ?? request;
            physical = physical with { InputArgs = physical.InputArgs.ToArray(), VppArgs = physical.VppArgs.ToArray() };
            var entry = new Entry(key, snapshot, bytes, physical) { LastUse = ++_useSequence };
            _entries.Add(key, _lru.AddFirst(entry));
            _sizeBytes += bytes;
        }
    }

    /// <summary>保存区間をすべて捨てる。F5の明示的な再実行で利用する。</summary>
    public void Clear()
    {
        lock (_gate) { _entries.Clear(); _lru.Clear(); _segments.Clear(); _activeSegments.Clear(); _stitchDisabled.Clear(); _pendingSources.Clear(); _currentSegmentId = null; _sizeBytes = 0; }
    }

    /// <summary>同じ入力・引数で、表示可能な時刻を含む信頼済み区間を探す。</summary>
    public bool TryGetAt(PreviewRequest request, double targetSeconds, out PreviewSegmentSnapshot segment)
    {
        lock (_gate)
        {
            if (_pendingSources.Values.Any(source => SameContent(source, request))) { segment = null!; return false; }
            if (_currentSegmentId is long currentId && _segments.TryGetValue(currentId, out var current)
                && SameContent(current.Request, request) && !current.CanStitch)
            { segment = null!; return false; }
            var entry = _segments.Values.Where(x => x.Clock.TrustedAbsolute && x.CanStitch && SameContent(x.Request, request)
                && x.Frames.Count > 0 && targetSeconds >= x.SafeStart - 1e-7 && targetSeconds <= x.SafeEnd + 1e-7
                && FrameSelector.Select(Snapshot(x), targetSeconds, request.Fps) >= 0)
                .OrderByDescending(x => x.LastUse).FirstOrDefault();
            if (entry == null) { segment = null!; return false; }
            entry.LastUse = ++_useSequence;
            segment = Snapshot(entry);
            return true;
        }
    }

    /// <summary>有限foregroundの早期画像が未検証の間、同sourceの旧区間を公開しない。</summary>
    public long BeginPendingSource(PreviewRequest request)
    {
        lock (_gate) { long token = ++_nextPendingId; _pendingSources.Add(token, request); return token; }
    }

    public void EndPendingSource(long token)
    {
        lock (_gate) _pendingSources.Remove(token);
    }

    /// <summary>foregroundのsource切替時だけ診断をやり直す。未信頼の旧フレームは復活させない。</summary>
    public void ResetSourceDiagnosis(PreviewRequest request, bool discard = false)
    {
        lock (_gate)
        {
            bool diagnosed = _stitchDisabled.RemoveAll(source => SameContent(source, request)) > 0;
            if (!diagnosed && !discard) return;
            foreach (long id in _segments.Values.Where(entry => SameContent(entry.Request, request)).Select(entry => entry.Id).ToArray()) RemoveSegmentCore(id);
            foreach (var node in _entries.Where(pair => pair.Key.MatchesSource(request)).Select(pair => pair.Value).ToArray()) Remove(node);
        }
    }

    /// <summary>absolute時計が壊れたsourceの旧フレームを破棄し、区間接続を停止する。</summary>
    public void InvalidateSource(PreviewRequest request)
    {
        lock (_gate)
        {
            if (!_stitchDisabled.Any(source => SameContent(source, request))) _stitchDisabled.Add(request);
            foreach (long id in _segments.Values.Where(entry => SameContent(entry.Request, request)).Select(entry => entry.Id).ToArray()) RemoveSegmentCore(id);
            foreach (var node in _entries.Where(pair => pair.Key.MatchesSource(request)).Select(pair => pair.Value).ToArray()) Remove(node);
        }
    }

    /// <summary>画素不一致が判明しておらず、新しい物理要求を区間接続できるsourceか。</summary>
    public bool CanStitch(PreviewRequest request)
    {
        lock (_gate) return !_stitchDisabled.Any(source => SameContent(source, request));
    }

    /// <summary>同じ入力・処理の信頼済み区間から、安全な移動候補を区間跨ぎの重複を除いて返す。</summary>
    public IReadOnlyList<PreviewFrameReference> GetNavigationFrames(PreviewRequest request)
    {
        lock (_gate)
        {
            if (_pendingSources.Values.Any(source => SameContent(source, request))) return Array.Empty<PreviewFrameReference>();
            if (_currentSegmentId is long id && _segments.TryGetValue(id, out var current)
                && SameContent(current.Request, request) && !current.CanStitch)
            {
                // 照合待ち/不一致では現在の物理取得だけを使い、absolute時計はそのまま維持する。
                var snapshot = Snapshot(current);
                return Array.AsReadOnly(Enumerable.Range(0, snapshot.Frames.Count)
                    .Select(index => new PreviewFrameReference(snapshot, index)).Where(frame => frame.IsSafe && frame.Clock.TrustedAbsolute).ToArray());
            }
            var snapshots = _segments.Values.Where(entry => entry.Clock.TrustedAbsolute && entry.CanStitch && SameContent(entry.Request, request))
                .Select(Snapshot).ToArray();
            var view = FrameSelector.MergeNavigationFrames(snapshots.SelectMany(segment =>
                Enumerable.Range(0, segment.Frames.Count).Select(index => new PreviewFrameReference(segment, index))));
            if (view.Count > 0 && !view[0].Segment.CanStitch && _segments.TryGetValue(view[0].SegmentId, out var newest))
            {
                var keeper = RejectStitch(newest);
                var snapshot = Snapshot(keeper);
                return Array.AsReadOnly(Enumerable.Range(0, snapshot.Frames.Count)
                    .Select(index => new PreviewFrameReference(snapshot, index)).Where(frame => frame.IsSafe).ToArray());
            }
            return view;
        }
    }

    /// <summary>現在位置を含み、同じ入力・処理で接続が確認済みの連続した表示範囲。</summary>
    public (double Start, double End)? GetConnectedSafeRange(PreviewRequest request)
    {
        lock (_gate)
        {
            if (_pendingSources.Values.Any(source => SameContent(source, request))) return null;
            if (_currentSegmentId is long id && _segments.TryGetValue(id, out var current)
                && SameContent(current.Request, request) && !current.CanStitch) return null;
            if (!_segments.Values.Any(entry => SameContent(entry.Request, request) && entry.Clock.TrustedAbsolute
                && entry.CanStitch && entry.SafeStart <= request.TargetSeconds && entry.SafeEnd >= request.TargetSeconds)) return null;
            var ranges = _segments.Values.Where(entry => SameContent(entry.Request, request) && entry.Clock.TrustedAbsolute && entry.CanStitch
                && entry.SafeEnd >= entry.SafeStart && entry.Frames.Count > 0).OrderBy(entry => entry.SafeStart).ToArray();
            double start = double.NaN, end = double.NaN;
            foreach (var range in ranges)
            {
                if (double.IsNaN(start) || range.SafeStart > end + 1e-7)
                {
                    if (!double.IsNaN(start) && request.TargetSeconds >= start - 1e-7 && request.TargetSeconds <= end + 1e-7) return (start, end);
                    start = range.SafeStart;
                    end = range.SafeEnd;
                }
                else end = Math.Max(end, range.SafeEnd);
            }
            return !double.IsNaN(start) && request.TargetSeconds >= start - 1e-7 && request.TargetSeconds <= end + 1e-7 ? (start, end) : null;
        }
    }

    /// <summary>生きた区間を登録する。現在表示位置と別にactiveを保護する。</summary>
    public long BeginSegment(PreviewRequest request, PreviewClockMap clock, double safeStartSeconds, bool active = true)
    {
        lock (_gate)
        {
            long id = ++_nextSegmentId;
            var entry = new SegmentEntry(id, request, clock, safeStartSeconds) { LastUse = ++_useSequence, CanStitch = clock.TrustedAbsolute };
            _segments.Add(id, entry);
            if (active) _activeSegments.Add(id);
            return id;
        }
    }

    /// <summary>現在の表示位置を保護し、通常時は後ろ2秒より古いprefixを捨てる。</summary>
    public void PinCurrent(long? segmentId, double currentSeconds)
    {
        lock (_gate)
        {
            _currentSegmentId = segmentId;
            _currentSeconds = currentSeconds;
            if (segmentId is long id && _activeSegments.Contains(id) && _segments.TryGetValue(id, out var entry))
                TrimPrefix(entry, currentSeconds - 2);
        }
    }

    /// <summary>次のフレームを保持できるか確認する。未読の未来を捨てず、足りなければ早くpauseする。</summary>
    public bool CanAppend(long segmentId, long bytes)
    {
        lock (_gate) return _segments.ContainsKey(segmentId) && MakeRoom(bytes, segmentId);
    }

    /// <summary>取得した1枚をすぐ保存する。上限で未来の穴を作らないよう、呼出し前にCanAppendする。</summary>
    public bool Append(long segmentId, Y4mFrame frame)
    {
        lock (_gate)
        {
            if (!_segments.TryGetValue(segmentId, out var entry) || !MakeRoom(frame.Data.LongLength, segmentId)) return false;
            if (entry.Frames.Count == 0)
                entry.SafeStart = SafeStartIncludingFirst(entry.SafeStart, entry.Clock, frame, entry.Request.Fps);
            entry.Frames.Add(frame);
            entry.Bytes += frame.Data.LongLength;
            _sizeBytes += frame.Data.LongLength;
            entry.SafeEnd = entry.Clock.ToInputSeconds(frame, entry.Frames.Count - 1, entry.Request.Fps) + FrameSelector.Duration(frame, entry.Request.Fps);
            entry.LastUse = ++_useSequence;
            if (_currentSegmentId == segmentId) TrimPrefix(entry, _currentSeconds - 2);
            ValidateAfterChange(entry, closed: false);
            return _segments.ContainsKey(segmentId);
        }
    }

    public void SetClock(long segmentId, PreviewClockMap clock)
    {
        lock (_gate)
        {
            if (_segments.TryGetValue(segmentId, out var entry))
            {
                entry.Clock = clock;
                entry.SafeStart = SafeStartIncludingFirst(entry.SafeStart, clock, entry.Frames.FirstOrDefault(), entry.Request.Fps);
                entry.CanStitch = clock.TrustedAbsolute;
            }
        }
    }

    /// <summary>区間を終了し、有限の末尾ガードまたは自然EOFの末尾を確定する。</summary>
    public void CompleteSegment(long segmentId, bool reachedInputEnd, double tailGuardSeconds = 0)
    {
        lock (_gate)
        {
            _activeSegments.Remove(segmentId);
            if (!_segments.TryGetValue(segmentId, out var entry)) return;
            entry.ReachedInputEnd = reachedInputEnd;
            if (!reachedInputEnd) entry.SafeEnd -= tailGuardSeconds;
            ValidateAfterChange(entry, closed: true);
        }
    }

    public PreviewSegmentSnapshot? GetSegment(long segmentId)
    {
        lock (_gate) return _segments.TryGetValue(segmentId, out var entry) ? Snapshot(entry) : null;
    }

    public void RemoveSegment(long segmentId)
    {
        lock (_gate) RemoveSegmentCore(segmentId);
    }

    /// <summary>成功した有限区間を範囲検索へ保存する。</summary>
    public PreviewSegmentSnapshot? StoreSegment(PreviewRequest request, IReadOnlyList<Y4mFrame> frames,
        PreviewClockMap clock, double safeStartSeconds, double safeEndSeconds, bool reachedInputEnd = false)
    {
        long bytes = frames.Sum(x => x.Data.LongLength);
        lock (_gate)
        {
            long id = ++_nextSegmentId;
            var entry = new SegmentEntry(id, request, clock, SafeStartIncludingFirst(safeStartSeconds, clock, frames.FirstOrDefault(), request.Fps))
            { SafeEnd = safeEndSeconds, Bytes = bytes, ReachedInputEnd = reachedInputEnd, LastUse = ++_useSequence };
            entry.Frames.AddRange(frames);
            // 容量不足で保存できない場合も、残る旧区間との整合を先に確認する。
            ValidateOverlap(entry, closed: true);
            if (!MakeRoom(bytes, null)) return null;
            _segments.Add(id, entry);
            _sizeBytes += bytes;
            ValidateAfterChange(entry, closed: true);
            return _segments.ContainsKey(id) ? Snapshot(entry) : null;
        }
    }

    /// <summary>計画上の安全域より実際の先頭画像が遅い場合、存在しないprefixを安全域に含めない。</summary>
    internal static double SafeStartIncludingFirst(double planned, PreviewClockMap clock, Y4mFrame? first, double fps)
    {
        if (first?.Xts is not double timestamp || !double.IsFinite(timestamp)) return planned;
        double actual = clock.ToInputSeconds(first, 0, fps);
        return double.IsFinite(actual) ? Math.Max(planned, actual) : planned;
    }

    public static bool SameContent(PreviewRequest a, PreviewRequest b) =>
        a.EncoderPath == b.EncoderPath && a.InputFile == b.InputFile
        && a.InputArgs.SequenceEqual(b.InputArgs, StringComparer.Ordinal)
        && a.VppArgs.SequenceEqual(b.VppArgs, StringComparer.Ordinal)
        && a.Capabilities == b.Capabilities && a.TimestampOriginSeconds == b.TimestampOriginSeconds;

    private void ValidateAfterChange(SegmentEntry entry, bool closed)
    {
        ValidateOverlap(entry, closed);
        if (!_segments.ContainsKey(entry.Id)) return;
        // 古いactive区間が後から伸びた場合も、新しい要求側の接続可否を原子的に更新する。
        foreach (var newer in _segments.Values.Where(peer => peer.Id > entry.Id && SameContent(peer.Request, entry.Request)).ToArray())
        {
            if (_segments.ContainsKey(newer.Id)) ValidateOverlap(newer, closed: !_activeSegments.Contains(newer.Id));
        }
    }

    private void ValidateOverlap(SegmentEntry entry, bool closed)
    {
        if (!entry.Clock.TrustedAbsolute) { entry.CanStitch = false; return; }
        if (_stitchDisabled.Any(source => SameContent(source, entry.Request)))
        { RejectStitch(entry); return; }
        bool pending = false;
        foreach (var previous in _segments.Values.Where(peer => peer.Id < entry.Id && peer.Clock.TrustedAbsolute
            && SameContent(peer.Request, entry.Request)).ToArray())
        {
            if (entry.ValidatedPeers.Contains(previous.Id)) continue;
            double start = Math.Max(entry.SafeStart, previous.SafeStart);
            double end = Math.Min(entry.SafeEnd, previous.SafeEnd);
            if (start > end + 1e-7) continue;
            var current = Snapshot(entry);
            var old = Snapshot(previous);
            var matches = new List<(PreviewFrameReference Current, PreviewFrameReference Old)>();
            var oldFrames = Enumerable.Range(0, old.Frames.Count).Select(index => new PreviewFrameReference(old, index))
                .Where(frame => frame.IsSafe && frame.Frame.Xts is double time && double.IsFinite(time)).OrderBy(frame => frame.InputSeconds).ToArray();
            var used = new HashSet<int>();
            for (int i = 0; i < current.Frames.Count; ++i)
            {
                var frame = new PreviewFrameReference(current, i);
                if (!frame.IsSafe || frame.InputSeconds < start - 1e-7 || frame.InputSeconds > end + 1e-7) continue;
                PreviewFrameReference? nearest = null;
                double distance = double.PositiveInfinity;
                double half = FrameSelector.Duration(frame.Frame, current.PhysicalRequest.Fps) / 2 + 1e-9;
                for (int j = LowerBound(oldFrames, frame.InputSeconds - half); j < oldFrames.Length && oldFrames[j].InputSeconds <= frame.InputSeconds + half; ++j)
                {
                    var other = oldFrames[j];
                    if (used.Contains(other.FrameIndex)) continue;
                    double delta = Math.Abs(frame.InputSeconds - other.InputSeconds);
                    if (FrameSelector.HaveSameTimestamp(frame, other) && delta < distance)
                    { nearest = other; distance = delta; }
                }
                if (nearest != null) { used.Add(nearest.FrameIndex); matches.Add((frame, nearest)); }
                if (!closed && matches.Count == 3) break;
            }
            // 受信途中は3点、有限の終了時は2点。点が増えるまでは旧区間と混ぜない。
            int required = closed ? 2 : 3;
            if (matches.Count < required)
            {
                if (closed) { RejectStitch(entry); return; }
                pending = true;
                continue;
            }
            int[] samples = matches.Count == 2 ? new[] { 0, 1 } : new[] { 0, matches.Count / 2, matches.Count - 1 };
            foreach (int sample in samples)
            {
                var pair = matches[sample];
                if (!pair.Current.Frame.Data.AsSpan().SequenceEqual(pair.Old.Frame.Data))
                { RejectStitch(entry); return; }
            }
            entry.ValidatedPeers.Add(previous.Id);
        }
        entry.CanStitch = !pending;
    }

    private static int LowerBound(PreviewFrameReference[] frames, double time)
    {
        int low = 0, high = frames.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (frames[middle].InputSeconds < time) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private SegmentEntry RejectStitch(SegmentEntry entry)
    {
        entry.CanStitch = false;
        if (!_stitchDisabled.Any(source => SameContent(source, entry.Request))) _stitchDisabled.Add(entry.Request);
        // olderのlate overlap検査からでも、最も新しい要求を残す。未挿入の新要求も候補に含める。
        var keeper = _segments.Values.Where(peer => SameContent(peer.Request, entry.Request)).Append(entry).MaxBy(peer => peer.Id)!;
        keeper.CanStitch = false;
        RemoveOtherSourceEntries(keeper);
        return keeper;
    }

    private void RemoveOtherSourceEntries(SegmentEntry entry)
    {
        foreach (long id in _segments.Values.Where(peer => peer.Id != entry.Id && SameContent(peer.Request, entry.Request)).Select(peer => peer.Id).ToArray())
            RemoveSegmentCore(id);
        foreach (var node in _entries.Where(pair => pair.Key.MatchesSource(entry.Request)).Select(pair => pair.Value).ToArray()) Remove(node);
    }

    private bool MakeRoom(long bytes, long? appendingSegment)
    {
        if (bytes < 0 || bytes > _capacityBytes || _capacityBytes == 0) return false;
        while (_sizeBytes > _capacityBytes - bytes)
        {
            if (EvictOne(appendingSegment)) continue;
            // pinはentryを保護する。メモリ不足時だけ、選択中の1枚を残して古いprefixをさらに縮める。
            bool removed = false;
            foreach (var entry in _segments.Values)
            {
                if (entry.Id == _currentSegmentId && entry.Frames.Count > 0)
                {
                    int selected = FrameSelector.Select(Snapshot(entry), _currentSeconds, entry.Request.Fps);
                    double end = entry.Clock.ToInputSeconds(entry.Frames[^1], entry.Frames.Count - 1, entry.Request.Fps)
                        + FrameSelector.Duration(entry.Frames[^1], entry.Request.Fps);
                    if (selected > 0 || _currentSeconds >= end - 1e-8) { RemoveFirst(entry); removed = true; break; }
                }
            }
            if (!removed) return false;
        }
        return true;
    }

    private bool EvictOne(long? protectedSegment = null)
    {
        var segment = _segments.Values.Where(x => x.Id != protectedSegment && x.Id != _currentSegmentId && !_activeSegments.Contains(x.Id))
            .OrderBy(x => x.LastUse).FirstOrDefault();
        var legacy = _lru.Last;
        if (legacy != null && (segment == null || legacy.Value.LastUse <= segment.LastUse)) { Remove(legacy); return true; }
        if (segment != null) { RemoveSegmentCore(segment.Id); return true; }
        return false;
    }

    private void TrimPrefix(SegmentEntry entry, double beforeSeconds)
    {
        while (entry.Frames.Count > 1 && entry.Clock.ToInputSeconds(entry.Frames[1], 1, entry.Request.Fps) <= beforeSeconds)
            RemoveFirst(entry);
    }

    private void RemoveFirst(SegmentEntry entry)
    {
        long bytes = entry.Frames[0].Data.LongLength;
        entry.Frames.RemoveAt(0);
        entry.Clock = entry.Clock with { FrameIndexOffset = entry.Clock.FrameIndexOffset + 1 };
        entry.Bytes -= bytes;
        _sizeBytes -= bytes;
        if (entry.Frames.Count > 0) entry.SafeStart = Math.Max(entry.SafeStart,
            entry.Clock.ToInputSeconds(entry.Frames[0], 0, entry.Request.Fps));
    }

    private void RemoveSegmentCore(long id)
    {
        if (_segments.Remove(id, out var entry)) _sizeBytes -= entry.Bytes;
        _activeSegments.Remove(id);
        if (_currentSegmentId == id) _currentSegmentId = null;
    }

    private PreviewSegmentSnapshot Snapshot(SegmentEntry entry) => new(entry.Id, entry.Request, entry.Clock,
        Array.AsReadOnly(entry.Frames.ToArray()), entry.SafeStart, entry.SafeEnd, _activeSegments.Contains(entry.Id), entry.ReachedInputEnd) { CanStitch = entry.CanStitch };

    private sealed class SegmentEntry(long id, PreviewRequest request, PreviewClockMap clock, double safeStart)
    {
        public long Id { get; } = id;
        public PreviewRequest Request { get; } = request;
        public PreviewClockMap Clock { get; set; } = clock;
        public double SafeStart { get; set; } = safeStart;
        public double SafeEnd { get; set; } = double.NegativeInfinity;
        public List<Y4mFrame> Frames { get; } = new();
        public long Bytes { get; set; }
        public long LastUse { get; set; }
        public bool ReachedInputEnd { get; set; }
        public bool CanStitch { get; set; } = true;
        public HashSet<long> ValidatedPeers { get; } = new();
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node.Value.Key);
        _lru.Remove(node);
        _sizeBytes -= node.Value.Bytes;
    }

    private sealed record Entry(CacheKey Key, IReadOnlyList<Y4mFrame> Frames, long Bytes, PreviewRequest PhysicalRequest)
    {
        public long LastUse { get; set; }
    }

    private sealed class CacheKey : IEquatable<CacheKey>
    {
        private readonly string _encoderPath;
        private readonly string _inputFile;
        private readonly string[] _inputArgs;
        private readonly string[] _vppArgs;
        private readonly double? _seekSeconds;
        private readonly int _frameCount;
        private readonly PreviewCapabilities _capabilities;
        private readonly double? _origin;

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
            _capabilities = request.Capabilities;
            _origin = request.TimestampOriginSeconds;
        }

        public bool MatchesSource(PreviewRequest request) => _encoderPath == request.EncoderPath && _inputFile == request.InputFile
            && _inputArgs.SequenceEqual(request.InputArgs, StringComparer.Ordinal) && _vppArgs.SequenceEqual(request.VppArgs, StringComparer.Ordinal)
            && _capabilities == request.Capabilities && _origin == request.TimestampOriginSeconds;

        public bool Equals(CacheKey? other) => other != null
            && string.Equals(_encoderPath, other._encoderPath, StringComparison.Ordinal)
            && string.Equals(_inputFile, other._inputFile, StringComparison.Ordinal)
            && _inputArgs.SequenceEqual(other._inputArgs, StringComparer.Ordinal)
            && _vppArgs.SequenceEqual(other._vppArgs, StringComparer.Ordinal)
            && _seekSeconds == other._seekSeconds
            && _frameCount == other._frameCount && _capabilities == other._capabilities && _origin == other._origin;

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
            hash.Add(_capabilities);
            hash.Add(_origin);
            return hash.ToHashCode();
        }
    }
}

namespace HWEncVppPreview.Core;

/// <summary>成功した要求の区間を、フレームデータの合計バイト数で制限するLRUキャッシュ。</summary>
/// <remarks>
/// 呼び出し側が成功した要求だけをStoreへ渡す。キーの引数配列と値のリストは保存時にコピーする。
/// 画像の二重保持を避けるため、Y4mFrameとDataは共有する。保存後および取得結果のDataは変更しないこと。
/// ヘッダとコレクションの管理領域は容量に含めない。各操作は複数スレッドから呼び出せる。
/// </remarks>
public sealed partial class FrameCache
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
    private long? _displayedBeforeSegmentId;
    private double _currentSeconds;
    private readonly HashSet<long> _activeSegments = new();
    private readonly List<PreviewRequest> _stitchDisabled = new();
    private readonly Dictionary<long, PreviewRequest> _pendingSources = new();
    private long _nextPendingId;

    // 未到達の順先読み需要だけに、中心元との既照合direct接続を要求する。
    internal bool AreDirectlyConnected(long segmentId, long peerId)
    {
        lock (_gate) return _segments.TryGetValue(segmentId, out var segment)
            && _segments.TryGetValue(peerId, out var peer) && DirectlyConnected(segment, peer);
    }

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
        foreach (var frame in snapshot)
        {
            ValidateRawScope(frame);
            bytes = checked(bytes + frame.RawByteLength);
        }
        using var release = ReleaseRawOutsideGate();
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var previous)) Remove(previous);
            if (_capacityBytes == 0 || bytes > _capacityBytes) return;
            // 引き算で比較し、巨大な上限での加算オーバーフローを避ける。
            while (_sizeBytes > _capacityBytes - bytes)
                if (!EvictOne() && !TryReclaimDisplayedBefore(bytes, null, null, "legacy_capacity_after_other_entries")) return;
            var physical = physicalRequest ?? request;
            physical = physical with { InputArgs = physical.InputArgs.ToArray(), VppArgs = physical.VppArgs.ToArray() };
            var entry = new Entry(key, snapshot, bytes, physical) { LastUse = ++_useSequence, RawLease = snapshot.Any(f => f.RawOwner != null) ? RawFrameSetLease.Capture(snapshot, "Cache") : null };
            _entries.Add(key, _lru.AddFirst(entry));
            _sizeBytes += bytes;
        }
    }

    /// <summary>保存区間をすべて捨てる。F5の明示的な再実行で利用する。</summary>
    public void Clear()
    {
        using var release = ReleaseRawOutsideGate();
        lock (_gate)
        {
            foreach (var entry in _segments.Values)
                DeferRawRelease(entry.Frames);
            foreach (var node in _entries.Values)
                if (node.Value.RawLease != null)
                    DeferRawRelease(node.Value.RawLease);
            _entries.Clear();
            _lru.Clear();
            _segments.Clear();
            _activeSegments.Clear();
            _stitchDisabled.Clear();
            _pendingSources.Clear();
            _currentSegmentId = null;
            ClearBeforeProtectionCore();
            _sizeBytes = 0;
        }
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
                && x.Frames.Count > 0 && PreviewSafeRange.Contains(targetSeconds, x.SafeStart, x.SafeEnd)
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
        using var release = ReleaseRawOutsideGate();
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
        using var release = ReleaseRawOutsideGate();
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
        using var release = ReleaseRawOutsideGate();
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
                && entry.CanStitch && PreviewSafeRange.Contains(request.TargetSeconds, entry.SafeStart, entry.SafeEnd))) return null;
            var ranges = _segments.Values.Where(entry => SameContent(entry.Request, request) && entry.Clock.TrustedAbsolute && entry.CanStitch
                && entry.SafeEnd >= entry.SafeStart && entry.Frames.Count > 0).OrderBy(entry => entry.SafeStart).ToArray();
            double start = double.NaN, end = double.NaN;
            foreach (var range in ranges)
            {
                if (double.IsNaN(start) || PreviewSafeRange.IsAfter(range.SafeStart, end))
                {
                    if (!double.IsNaN(start) && PreviewSafeRange.Contains(request.TargetSeconds, start, end)) return (start, end);
                    start = range.SafeStart;
                    end = range.SafeEnd;
                }
                else end = Math.Max(end, range.SafeEnd);
            }
            return !double.IsNaN(start) && PreviewSafeRange.Contains(request.TargetSeconds, start, end) ? (start, end) : null;
        }
    }

    /// <summary>生きた区間を登録する。現在表示位置と別にactiveを保護する。</summary>
    public long BeginSegment(PreviewRequest request, PreviewClockMap clock, double safeStartSeconds, bool active = true)
    {
        lock (_gate)
        {
            long id = ++_nextSegmentId;
            var entry = new SegmentEntry(id, request, clock, safeStartSeconds, DeferRawRelease) { LastUse = ++_useSequence, CanStitch = clock.TrustedAbsolute };
            _segments.Add(id, entry);
            if (active) _activeSegments.Add(id);
            return id;
        }
    }

    /// <summary>現在の表示位置を保護し、古いprefixは容量不足時のMakeRoomによる回収まで保持する。</summary>
    public void PinCurrent(long? segmentId, double currentSeconds)
    {
        lock (_gate)
        {
            _currentSegmentId = segmentId;
            _currentSeconds = currentSeconds;
        }
    }

    /// <summary>表示準備中の元画像を保護し、容量不足時に残す一枚と受理済みの移動方向を記録する。</summary>
    public void ProtectDisplayedBeforeSegment(long? id, long frameId = 0, int direction = 0)
    {
        lock (_gate)
        {
            if (id is not long value || !_segments.ContainsKey(value)) { ClearBeforeProtectionCore(); return; }
            _displayedBeforeSegmentId = value;
            _displayedBeforeFrameId = frameId;
            _displayedBeforeDirection = Math.Sign(direction);
        }
    }

    /// <summary>次のフレームを保持できるか確認する。未読の未来を捨てず、足りなければ早くpauseする。</summary>
    public bool CanAppend(long segmentId, long bytes)
    {
        using var release = ReleaseRawOutsideGate();
        lock (_gate) return _segments.ContainsKey(segmentId) && MakeRoom(bytes, segmentId);
    }

    /// <summary>取得した1枚をすぐ保存する。上限で未来の穴を作らないよう、呼出し前にCanAppendする。</summary>
    public bool Append(long segmentId, Y4mFrame frame)
    {
        ValidateRawScope(frame);
        using var release = ReleaseRawOutsideGate();
        lock (_gate)
        {
            if (!_segments.TryGetValue(segmentId, out var entry) || !MakeRoom(frame.RawByteLength, segmentId))
                return false;
            if (entry.Frames.Count == 0)
                entry.SafeStart = SafeStartIncludingFirst(entry.SafeStart, entry.Clock, frame, entry.Request.Fps);
            entry.Frames.Add(frame);
            entry.Bytes += frame.RawByteLength;
            _sizeBytes += frame.RawByteLength;
            entry.SafeEnd = entry.Clock.ToInputSeconds(frame, entry.Frames.Count - 1, entry.Request.Fps) + FrameSelector.Duration(frame, entry.Request.Fps);
            entry.LastUse = ++_useSequence;
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
        using var release = ReleaseRawOutsideGate();
        lock (_gate)
        {
            _activeSegments.Remove(segmentId);
            if (!_segments.TryGetValue(segmentId, out var entry)) return;
            entry.ReachedInputEnd = reachedInputEnd;
            if (!reachedInputEnd) entry.SafeEnd -= tailGuardSeconds;
            ValidateAfterChange(entry, closed: true);
        }
    }

    // 存在確認はsnapshot/画素所有を作らない。成功後の画素取得には別途生存確認が必要。
    internal bool ContainsSegment(long segmentId)
    {
        lock (_gate) return _segments.ContainsKey(segmentId);
    }

    public PreviewSegmentSnapshot? GetSegment(long segmentId)
    {
        lock (_gate) return _segments.TryGetValue(segmentId, out var entry) ? Snapshot(entry) : null;
    }

    public void RemoveSegment(long segmentId)
    {
        using var release = ReleaseRawOutsideGate();
        lock (_gate) RemoveSegmentCore(segmentId);
    }

    /// <summary>成功した有限区間を範囲検索へ保存する。</summary>
    public PreviewSegmentSnapshot? StoreSegment(PreviewRequest request, IReadOnlyList<Y4mFrame> frames,
        PreviewClockMap clock, double safeStartSeconds, double safeEndSeconds, bool reachedInputEnd = false, bool reverseWindow = false, double? firstOutputInputSeconds = null, bool pinCurrent = false)
    {
        foreach (var frame in frames)
            ValidateRawScope(frame);
        long bytes = frames.Sum(x => x.RawByteLength);
        using var release = ReleaseRawOutsideGate();
        lock (_gate)
        {
            // 前景の旧pin解除から新pinまでを同じgateで行い、背景保存の割込みで新Afterを失わないようにする。
            // 保存失敗時は従来の前景経路と同じく旧pinを戻さず、activeの保護は独立して維持する。
            if (pinCurrent)
            {
                _currentSegmentId = null;
                _currentSeconds = request.TargetSeconds;
            }
            long id = ++_nextSegmentId;
            // 暖機pixelを保持しない窓でも、実際に受信した元の先頭時刻で従来と同じ安全判定を行う。
            double start = firstOutputInputSeconds is double first && double.IsFinite(first)
                ? Math.Max(safeStartSeconds, first) : SafeStartIncludingFirst(safeStartSeconds, clock, frames.FirstOrDefault(), request.Fps);
            var entry = new SegmentEntry(id, request, clock, start, DeferRawRelease)
            { SafeEnd = safeEndSeconds, Bytes = bytes, ReachedInputEnd = reachedInputEnd, LastUse = ++_useSequence };
            bool committed = false;
            try
            {
            entry.Frames.AddRange(frames);
            // 容量不足で保存できない場合も、残る旧区間との整合を先に確認する。
            ValidateOverlap(entry, closed: true);
            // 逆先読みも古いclosed区間を通常の使用順で回収する。bridgeは削らず、
            // 競合したactive等しか残らない場合は保存を拒否する。currentのprefixもここでは削らない。
            var reverseProtection = reverseWindow
                ? ReverseProtectedSegments(request, request.TargetSeconds, request.TargetSeconds) : null;
            if (reverseWindow && reverseProtection == null || !MakeRoom(bytes, null, reverseProtection, trimCurrent: !reverseWindow))
            {
                // 比較による診断と、容量不足で実区間を保存できない結果を区別する。
                if (PerfTrace.Enabled) PerfTrace.Write("cache.store_rejected", new { entry.Id, request.CorrelationId,
                    reason = "capacity_after_overlap_check", bytes, capacityBytes = _capacityBytes, cacheBytes = _sizeBytes,
                    entry.CanStitch, sourceCanStitch = !_stitchDisabled.Any(source => SameContent(source, request)),
                    entry.SafeStart, entry.SafeEnd });
                return null;
            }
            _segments.Add(id, entry);
            committed=true;
            _sizeBytes += bytes;
            ValidateAfterChange(entry, closed: true);
            if (!_segments.ContainsKey(id)) return null;
            // MakeRoomより前に作った候補の辺も、途中で縮めた元画像との共通点を返却前に確認する。
            if (_displayedBeforeSegmentId is long beforeId && _segments.TryGetValue(beforeId, out var before)
                && before.BeforeWindowTrimmed && entry.Id != beforeId && (entry.ValidatedPeers.Contains(beforeId) || before.ValidatedPeers.Contains(entry.Id)))
                DropInsufficientBeforePeerEdge(entry, before);
            if (pinCurrent) _currentSegmentId = id;
            return Snapshot(entry);
            }
            finally
            {
                // 列挙やRetain/照合の例外でも、未保存候補の所有だけはgate外で確実に閉じる。
                if (!committed)
                    DeferRawRelease(entry.Frames);
            }
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
            if (PreviewSafeRange.IsAfter(start, end)) continue;
            var current = Snapshot(entry);
            var old = Snapshot(previous);
            using var rawComparison = new DeferredRaw(this, RawFrameSetLease.Capture(current.Frames.Concat(old.Frames), "StitchComparison"));
            var matches = new List<(PreviewFrameReference Current, PreviewFrameReference Old)>();
            var oldFrames = Enumerable.Range(0, old.Frames.Count).Select(index => new PreviewFrameReference(old, index))
                .Where(frame => frame.IsSafe && frame.Frame.Xts is double time && double.IsFinite(time)).OrderBy(frame => frame.InputSeconds).ToArray();
            var used = new HashSet<int>();
            for (int i = 0; i < current.Frames.Count; ++i)
            {
                var frame = new PreviewFrameReference(current, i);
                if (!frame.IsSafe || !PreviewSafeRange.Contains(frame.InputSeconds, start, end)) continue;
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
                // 容量整理で二点を残せなかった窓は未接続に留め、残る一点の画素不一致だけは従来どおり拒否する。
                if (closed && matches.Count < MinimumConnectedSafeFrameCount && (entry.BeforeWindowTrimmed || previous.BeforeWindowTrimmed))
                {
                    foreach (var pair in matches)
                    {
                        if (pair.Current.Frame.RawPixels.AsSpan().SequenceEqual(pair.Old.Frame.RawPixels)) continue;
                        if (PerfTrace.Enabled) PerfTrace.Write("cache.overlap_reject", new { entry.Id, peerId = previous.Id,
                            entry.Request.CorrelationId, reason = "pixel_mismatch", matches = matches.Count, required,
                            overlapStart = start, overlapEnd = end, currentXts = pair.Current.Frame.Xts,
                            previousXts = pair.Old.Frame.Xts });
                        RejectStitch(entry, "pixel_mismatch");
                        return;
                    }
                    continue;
                }
                if (closed)
                {
                    if (PerfTrace.Enabled) PerfTrace.Write("cache.overlap_reject", new { entry.Id, peerId = previous.Id,
                        entry.Request.CorrelationId, reason = "insufficient_timestamp_matches", matches = matches.Count,
                        required, overlapStart = start, overlapEnd = end });
                    RejectStitch(entry, "insufficient_timestamp_matches");
                    return;
                }
                pending = true;
                continue;
            }
            int[] samples = matches.Count == 2 ? new[] { 0, 1 } : new[] { 0, matches.Count / 2, matches.Count - 1 };
            foreach (int sample in samples)
            {
                var pair = matches[sample];
                if (!pair.Current.Frame.RawPixels.AsSpan().SequenceEqual(pair.Old.Frame.RawPixels))
                {
                    if (PerfTrace.Enabled) PerfTrace.Write("cache.overlap_reject", new { entry.Id, peerId = previous.Id,
                        entry.Request.CorrelationId, reason = "pixel_mismatch", matches = matches.Count, required,
                        overlapStart = start, overlapEnd = end, currentXts = pair.Current.Frame.Xts,
                        previousXts = pair.Old.Frame.Xts });
                    RejectStitch(entry, "pixel_mismatch");
                    return;
                }
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

    private SegmentEntry RejectStitch(SegmentEntry entry, string reason = "ambiguous_navigation")
    {
        if (PerfTrace.Enabled && !_stitchDisabled.Any(source => SameContent(source, entry.Request)))
            PerfTrace.Write("cache.stitch_reject", new { entry.Id, entry.Request.CorrelationId, reason,
                entry.SafeStart, entry.SafeEnd, frames = entry.Frames.Count, cacheBytes = _sizeBytes });
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

    // 元画像の取り直しを避けるため、保護されない区間と現在区間の古い先頭部分を先に回収し、元画像の区間は最後に回す。
    private bool MakeRoom(long bytes, long? appendingSegment, HashSet<long>? protectedSegments = null, bool trimCurrent = true)
    {
        if (bytes < 0 || bytes > _capacityBytes || _capacityBytes == 0) return false;
        while (_sizeBytes > _capacityBytes - bytes)
        {
            if (EvictOne(appendingSegment, protectedSegments)) continue;
            // pinはentryを保護する。メモリ不足時だけ、選択中の1枚を残して古いprefixをさらに縮める。
            // 既照合peerの過去prefixは表示移動に従い回収し、共通raw点数を永久固定しない。
            // 未来tailも切るreverse計画では、接続を維持する共通点を別途残す。
            bool removed = false;
            if (trimCurrent)
            {
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
            }
            if (!removed && !TryReclaimDisplayedBefore(bytes, appendingSegment, protectedSegments, "capacity_after_current_prefix")) return false;
        }
        return true;
    }

    // 見積もりと実際の回収で同じ候補を使い、回収できない区間を空きとして数えない。
    private IEnumerable<SegmentEntry> EvictableSegments(long? protectedSegment = null, HashSet<long>? protectedSegments = null,
        bool includeDisplayedBefore = false)
    {
        var directPeers = CurrentDirectPeerIds();
        return _segments.Values.Where(x => x.Id != protectedSegment && x.Id != _currentSegmentId
            && (includeDisplayedBefore || x.Id != _displayedBeforeSegmentId) && !_activeSegments.Contains(x.Id) && !directPeers.Contains(x.Id) && !(protectedSegments?.Contains(x.Id) ?? false));
    }

    private bool EvictOne(long? protectedSegment = null, HashSet<long>? protectedSegments = null)
    {
        var segment = EvictableSegments(protectedSegment, protectedSegments).OrderBy(x => x.LastUse).FirstOrDefault();
        var legacy = _lru.Last;
        if (legacy != null && (segment == null || legacy.Value.LastUse <= segment.LastUse))
        {
            if (PerfTrace.Enabled) PerfTrace.Write("cache.evict_legacy", new { legacy.Value.PhysicalRequest.CorrelationId,
                legacy.Value.PhysicalRequest.TargetSeconds, bytes = legacy.Value.Bytes,
                currentSegmentId = _currentSegmentId, currentSeconds = _currentSeconds, protectedSegment });
            Remove(legacy);
            return true;
        }
        if (segment != null)
        {
            if (PerfTrace.Enabled) PerfTrace.Write("cache.evict", new { segment.Id, segment.Request.CorrelationId,
                segment.SafeStart, segment.SafeEnd, bytes = segment.Bytes, currentSegmentId = _currentSegmentId,
                currentSeconds = _currentSeconds, protectedSegment, distanceStart = segment.SafeStart - _currentSeconds,
                distanceEnd = segment.SafeEnd - _currentSeconds, sameContent = _currentSegmentId is long currentId
                    && _segments.TryGetValue(currentId, out var current) && SameContent(current.Request, segment.Request) });
            RemoveSegmentCore(segment.Id);
            return true;
        }
        return false;
    }

    private void RemoveFirst(SegmentEntry entry)
    {
        long bytes = entry.Frames[0].RawByteLength;
        entry.Frames.RemoveAt(0);
        entry.Clock = entry.Clock with { FrameIndexOffset = entry.Clock.FrameIndexOffset + 1 };
        entry.Bytes -= bytes;
        _sizeBytes -= bytes;
        if (entry.Frames.Count > 0) entry.SafeStart = Math.Max(entry.SafeStart,
            entry.Clock.ToInputSeconds(entry.Frames[0], 0, entry.Request.Fps));
    }

    private void RemoveSegmentCore(long id)
    {
        if (_segments.Remove(id, out var entry))
        {
            _sizeBytes -= entry.Bytes;
            DeferRawRelease(entry.Frames);
        }
        _activeSegments.Remove(id);
        if (_currentSegmentId == id) _currentSegmentId = null;
        if (_displayedBeforeSegmentId == id) ClearBeforeProtectionCore();
    }

    private PreviewSegmentSnapshot Snapshot(SegmentEntry entry) => new(entry.Id, entry.Request, entry.Clock,
        Array.AsReadOnly(entry.Frames.ToArray()), entry.SafeStart, entry.SafeEnd, _activeSegments.Contains(entry.Id), entry.ReachedInputEnd) { CanStitch = entry.CanStitch };

    private sealed class SegmentEntry(long id, PreviewRequest request, PreviewClockMap clock, double safeStart, Action<IDisposable> release)
    {
        public long Id { get; } = id;
        public PreviewRequest Request { get; } = request;
        public PreviewClockMap Clock { get; set; } = clock;
        public double SafeStart { get; set; } = safeStart;
        public double SafeEnd { get; set; } = double.NegativeInfinity;
        public RawFrameList Frames { get; } = new("Cache", release);
        public long Bytes { get; set; }
        public long LastUse { get; set; }
        public bool ReachedInputEnd { get; set; }
        public bool CanStitch { get; set; } = true;
        // 意図的な縮小の点数不足を自然な照合失敗と区別し、source全体の接続禁止で連続取得を止めない。
        public bool BeforeWindowTrimmed { get; set; }
        public HashSet<long> ValidatedPeers { get; } = new();
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        if (node.Value.RawLease != null)
            DeferRawRelease(node.Value.RawLease);
        _entries.Remove(node.Value.Key);
        _lru.Remove(node);
        _sizeBytes -= node.Value.Bytes;
    }

    private sealed record Entry(CacheKey Key, IReadOnlyList<Y4mFrame> Frames, long Bytes, PreviewRequest PhysicalRequest)
    {
        internal RawFrameSetLease? RawLease
        {
            get; init;
        }
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

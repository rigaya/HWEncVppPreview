namespace HWEncVppPreview.Core;

public sealed partial class FrameCache
{
    private const int BeforeWindowCapacityDivisor = 8;
    private const int MinimumConnectedSafeFrameCount = 2;
    private long _displayedBeforeFrameId;
    private int _displayedBeforeDirection;

    private void ClearBeforeProtectionCore()
    {
        _displayedBeforeSegmentId = null;
        _displayedBeforeFrameId = 0;
        _displayedBeforeDirection = 0;
    }

    private sealed record BeforeReclaimPlan(SegmentEntry Entry, int First, int Last, long RetainedBytes,
        string? EvictionReason = null)
    {
        internal long ReleasedBytes => Entry.Bytes - RetainedBytes;
        internal bool EvictsEntry => EvictionReason != null;
    }

    // 見積もりと実回収で同じ計画を使い、表示一枚を残す分を空き容量へ二重計上しない。
    private BeforeReclaimPlan? PlanDisplayedBeforeReclaim(long requiredBytes, long? protectedSegment,
        HashSet<long>? protectedSegments, bool minimumWindow = false)
    {
        if (_displayedBeforeSegmentId is not long id) return null;
        var entry = EvictableSegments(protectedSegment, protectedSegments, includeDisplayedBefore: true)
            .FirstOrDefault(candidate => candidate.Id == id);
        if (entry == null) return null;
        if (!entry.Clock.TrustedAbsolute) return new(entry, 0, -1, 0, "untrusted_clock");
        int anchor = _displayedBeforeFrameId > 0
            ? Enumerable.Range(0, entry.Frames.Count).FirstOrDefault(index => entry.Frames[index].FrameId == _displayedBeforeFrameId, -1) : -1;
        if (anchor < 0) return new(entry, 0, -1, 0, "frame_id_missing");

        int first = anchor, last = anchor;
        long retained = entry.Frames[anchor].RawByteLength;
        // 一枚が1/8枠を超えても表示を失わない。cache全体の上限は呼出元で守る。
        long limit = Math.Max(_capacityBytes / BeforeWindowCapacityDivisor, retained);
        if (!minimumWindow)
        {
            while (true)
            {
                bool left = first > 0 && _displayedBeforeDirection <= 0
                    && entry.Frames[first - 1].RawByteLength <= limit - retained;
                bool right = last + 1 < entry.Frames.Count && _displayedBeforeDirection >= 0
                    && entry.Frames[last + 1].RawByteLength <= limit - retained;
                if (!left && !right) break;
                if (left && (!right || anchor - first <= last - anchor)) retained += entry.Frames[--first].RawByteLength;
                else retained += entry.Frames[++last].RawByteLength;
            }
            long needed = Math.Max(0, requiredBytes - (_capacityBytes - _sizeBytes));
            // 方向窓だけで不足する場合も遠い端から縮め、最後まで選択一枚は残す。
            while (entry.Bytes - retained < needed && first < last)
            {
                if (first < anchor && (last == anchor || anchor - first >= last - anchor))
                    retained -= entry.Frames[first++].RawByteLength;
                else retained -= entry.Frames[last--].RawByteLength;
            }
            if (entry.Bytes - retained < needed) return null;
        }
        return new(entry, first, last, retained);
    }

    private bool TryReclaimDisplayedBefore(long requiredBytes, long? protectedSegment,
        HashSet<long>? protectedSegments, string stage)
    {
        var plan = PlanDisplayedBeforeReclaim(requiredBytes, protectedSegment, protectedSegments);
        if (plan == null || !plan.EvictsEntry && plan.ReleasedBytes <= 0) return false;
        var entry = plan.Entry;
        if (plan.EvictsEntry)
        {
            // 旧一引数APIや場所を確定できない時計では、従来の全区間回収を維持する。
            if (PerfTrace.Enabled) PerfTrace.Write("cache.evict", new { entry.Id, entry.Request.CorrelationId,
                entry.SafeStart, entry.SafeEnd, bytes = entry.Bytes, currentSegmentId = _currentSegmentId,
                currentSeconds = _currentSeconds, protectedSegment, distanceStart = entry.SafeStart - _currentSeconds,
                distanceEnd = entry.SafeEnd - _currentSeconds, sameContent = _currentSegmentId is long currentId
                    && _segments.TryGetValue(currentId, out var current) && SameContent(current.Request, entry.Request),
                reason = "displayed_before_last_resort", fallbackReason = plan.EvictionReason,
                frameId = _displayedBeforeFrameId, direction = _displayedBeforeDirection, stage });
            RemoveSegmentCore(entry.Id);
            return true;
        }
        int oldFrames = entry.Frames.Count;
        long oldBytes = entry.Bytes;
        int kept = plan.Last - plan.First + 1;
        for (int index = 0; index < plan.First; index++) RemoveFirst(entry);
        bool tailRemoved = entry.Frames.Count > kept;
        while (entry.Frames.Count > kept)
        {
            long bytes = entry.Frames[^1].RawByteLength;
            entry.Frames.RemoveAt(entry.Frames.Count - 1);
            entry.Bytes -= bytes;
            _sizeBytes -= bytes;
        }
        if (tailRemoved)
        {
            entry.SafeEnd = Math.Min(entry.SafeEnd,
                entry.Clock.ToInputSeconds(entry.Frames[^1], entry.Frames.Count - 1, entry.Request.Fps)
                    + FrameSelector.Duration(entry.Frames[^1], entry.Request.Fps));
            entry.ReachedInputEnd = false;
        }
        entry.BeforeWindowTrimmed = true;
        // 共通点を失った辺だけを外し、容量整理を画素不一致やsource全体の接続禁止へ変えない。
        foreach (var peer in _segments.Values.Where(peer => peer.Id != entry.Id
            && (entry.ValidatedPeers.Contains(peer.Id) || peer.ValidatedPeers.Contains(entry.Id))))
            DropInsufficientBeforePeerEdge(entry, peer);
        if (PerfTrace.Enabled) PerfTrace.Write("cache.before_trim", new { entry.Id, entry.Request.CorrelationId,
            frameId = _displayedBeforeFrameId, direction = _displayedBeforeDirection, stage, oldFrames,
            newFrames = entry.Frames.Count, oldBytes, newBytes = entry.Bytes, first = plan.First, last = plan.Last,
            singleFrame = entry.Frames.Count == 1, entry.SafeStart, entry.SafeEnd });
        return true;
    }

    private void DropInsufficientBeforePeerEdge(SegmentEntry entry, SegmentEntry peer)
    {
        if (ReversePeerMatches(entry, peer).Count >= MinimumConnectedSafeFrameCount) return;
        entry.ValidatedPeers.Remove(peer.Id);
        peer.ValidatedPeers.Remove(entry.Id);
    }
}

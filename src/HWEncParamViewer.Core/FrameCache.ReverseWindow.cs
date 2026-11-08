namespace HWEncParamViewer.Core;

public sealed partial class FrameCache
{
    internal long ReverseWindowRoom(PreviewRequest request,double boundary,double target,double overlap)
    {
        lock (_gate)
        {
            var range=GetConnectedSafeRange(request);
            if(range==null || !PreviewSafeRange.Contains(boundary,range.Value.Start,range.Value.End)
                || !PreviewSafeRange.Contains(target,range.Value.Start,range.Value.End))return 0;
            var protectedSegments = ReverseProtectedSegments(request, boundary, target);
            if (protectedSegments == null) return 0;
            long reclaimable = ReverseReclaimableBytes(protectedSegments);
            return Math.Max(_capacityBytes - _sizeBytes, ReverseRoomCore(request,boundary,target,overlap)?.Bytes ?? 0)
                + reclaimable;
        }
    }

    private static bool DirectlyConnected(SegmentEntry entry, SegmentEntry peer)
        => entry.Id != peer.Id && entry.Clock.TrustedAbsolute && peer.Clock.TrustedAbsolute && entry.CanStitch && peer.CanStitch
            && SameContent(entry.Request, peer.Request)
            && (entry.ValidatedPeers.Contains(peer.Id) || peer.ValidatedPeers.Contains(entry.Id))
            && !PreviewSafeRange.IsAfter(Math.Max(entry.SafeStart, peer.SafeStart), Math.Min(entry.SafeEnd, peer.SafeEnd));

    // 照合済みで直接重なる前後1件だけを、現在entryの接続先として共通に保護する。
    private (SegmentEntry? Previous, SegmentEntry? Next) CurrentDirectPeers()
    {
        if (_currentSegmentId is not long id || !_segments.TryGetValue(id, out var current)) return (null, null);
        var candidates = _segments.Values.Where(peer => DirectlyConnected(current, peer)).ToArray();
        var previous = candidates.Where(peer => PreviewSafeRange.IsAfter(current.SafeStart, peer.SafeStart))
            .OrderByDescending(peer => peer.SafeStart).ThenByDescending(peer => peer.LastUse).ThenByDescending(peer => peer.Id).FirstOrDefault();
        var next = candidates.Where(peer => PreviewSafeRange.IsAfter(peer.SafeEnd, current.SafeEnd))
            .OrderBy(peer => peer.SafeEnd).ThenByDescending(peer => peer.LastUse).ThenByDescending(peer => peer.Id).FirstOrDefault();
        return (previous, next);
    }

    private HashSet<long> CurrentDirectPeerIds()
    {
        var peers = CurrentDirectPeers();
        return new[] { peers.Previous, peers.Next }.OfType<SegmentEntry>().Select(peer => peer.Id).ToHashSet();
    }

    private HashSet<long>? ReverseProtectedSegments(PreviewRequest request, double boundary, double target)
    {
        var protectedSegments = CurrentDirectPeerIds();
        if (_currentSegmentId is not long currentId || !_segments.TryGetValue(currentId, out var current)
            || !SameContent(current.Request, request)) return protectedSegments;
        protectedSegments.Add(currentId);
        foreach (double point in new[] { boundary, target })
        {
            if (PreviewSafeRange.Contains(point, current.SafeStart, current.SafeEnd)) continue;
            // 時刻が同じ範囲にあるだけでは固定しない。既検証の辺をたどる必要最小のbridgeだけを残す。
            var path = new Dictionary<long, long> { [currentId] = currentId };
            var queue = new Queue<SegmentEntry>();
            queue.Enqueue(current);
            SegmentEntry? goal = null;
            while (queue.TryDequeue(out var entry))
            {
                if (PreviewSafeRange.Contains(point, entry.SafeStart, entry.SafeEnd)) { goal = entry; break; }
                foreach (var peer in _segments.Values.Where(peer => !path.ContainsKey(peer.Id) && DirectlyConnected(entry, peer))
                    .OrderByDescending(peer => peer.LastUse).ThenByDescending(peer => peer.Id))
                { path.Add(peer.Id, entry.Id); queue.Enqueue(peer); }
            }
            if (goal == null) return null;
            for (long id = goal.Id; id != currentId; id = path[id]) protectedSegments.Add(id);
        }
        return protectedSegments;
    }

    private long ReverseReclaimableBytes(HashSet<long> protectedSegments)
        => EvictableSegments(protectedSegments: protectedSegments).Sum(entry => entry.Bytes)
            + _entries.Values.Sum(node => node.Value.Bytes)
            + (PlanDisplayedBeforeReclaim(0, null, protectedSegments, minimumWindow: true)?.ReleasedBytes ?? 0);

    private (SegmentEntry Entry,int First,int Last,long Bytes)? ReverseRoomCore(PreviewRequest request,double boundary,double target,double overlap)
    {
        if (_currentSegmentId is not long id || !_segments.TryGetValue(id,out var entry)
            || !SameContent(entry.Request,request) || !entry.Clock.TrustedAbsolute || !entry.CanStitch
            || !PreviewSafeRange.Contains(boundary,entry.SafeStart,entry.SafeEnd)
            || !PreviewSafeRange.Contains(target,entry.SafeStart,entry.SafeEnd)) return null;
        var snapshot=Snapshot(entry);
        int selected=FrameSelector.Select(snapshot,target,request.Fps);
        if (selected<0) return null;
        int first=-1,last=Math.Min(entry.Frames.Count-1,selected+1),matches=0;
        for(int i=0;i<entry.Frames.Count;i++)
        {
            double time=entry.Clock.ToInputSeconds(entry.Frames[i],i,request.Fps);
            if (!PreviewSafeRange.Contains(time,entry.SafeStart,entry.SafeEnd)) continue;
            if (first<0 && time>=boundary-PreviewSafeRange.ToleranceSeconds) first=i;
            if (PreviewSafeRange.Contains(time,boundary,boundary+overlap)) {++matches;last=Math.Max(last,i);}
        }
        // 複数物理entryを跨ぐbridgeや、照合の2点すら無いVFRは破壊せず次の移動で再評価する。
        if (first<0 || first>selected || matches<2) return null;
        var peers = CurrentDirectPeers();
        foreach (var peer in new[] { peers.Previous, peers.Next }.OfType<SegmentEntry>().DistinctBy(peer => peer.Id))
        {
            var common = ReversePeerMatches(entry, peer);
            // 既存照合を再実行せず、接続を保つ2safe共通点だけをrawの最小保持範囲へ加える。
            if (common.Count < 2) return null;
            var required = peers.Previous?.Id == peer.Id && peers.Next?.Id == peer.Id
                ? common.OrderBy(index => Math.Abs(index - selected)).ThenBy(index => index).Take(2)
                : peers.Previous?.Id == peer.Id ? common.TakeLast(2) : common.Take(2);
            first = Math.Min(first, required.Min());
            last = Math.Max(last, required.Max());
        }
        long retained=0;
        for (int i = first; i <= last; i++)
            retained += entry.Frames[i].RawByteLength;
        long free=_capacityBytes-(_sizeBytes-entry.Bytes)-retained;
        return free>0?(entry,first,last,free):null;
    }

    // ValidateOverlapと同じtimestamp対応だけを使い、保持すべき既検証の共通点を調べる。
    private List<int> ReversePeerMatches(SegmentEntry entry, SegmentEntry peer)
    {
        double start = Math.Max(entry.SafeStart, peer.SafeStart), end = Math.Min(entry.SafeEnd, peer.SafeEnd);
        var current = Snapshot(entry);
        var previous = Snapshot(peer);
        var oldFrames = Enumerable.Range(0, previous.Frames.Count).Select(index => new PreviewFrameReference(previous, index))
            .Where(frame => frame.IsSafe && PreviewSafeRange.Contains(frame.InputSeconds, start, end)
                && frame.Frame.Xts is double time && double.IsFinite(time)).OrderBy(frame => frame.InputSeconds).ToArray();
        var used = new HashSet<int>();
        var indices = new List<int>();
        for (int i = 0; i < current.Frames.Count; i++)
        {
            var frame = new PreviewFrameReference(current, i);
            if (!frame.IsSafe || !PreviewSafeRange.Contains(frame.InputSeconds, start, end)) continue;
            int nearest = -1;
            double distance = double.PositiveInfinity;
            double half = FrameSelector.Duration(frame.Frame, current.PhysicalRequest.Fps) / 2 + 1e-9;
            for (int j = LowerBound(oldFrames, frame.InputSeconds - half); j < oldFrames.Length && oldFrames[j].InputSeconds <= frame.InputSeconds + half; j++)
            {
                var other = oldFrames[j];
                double delta = Math.Abs(frame.InputSeconds - other.InputSeconds);
                if (!used.Contains(other.FrameIndex) && FrameSelector.HaveSameTimestamp(frame, other) && delta < distance)
                { nearest = other.FrameIndex; distance = delta; }
            }
            if (nearest >= 0) { used.Add(nearest); indices.Add(i); }
        }
        return indices;
    }

    internal bool TryTrimReverseWindow(PreviewRequest request,double boundary,double target,double overlap,long bytes)
    {
        using var release = ReleaseRawOutsideGate();
        lock (_gate)
        {
            // 保護されない完了区間、現在区間の接続部縮小、表示中の元画像区間の順で回収し、表示用の窓を長く残す。
            // 見積もりが不足する場合は、回収やtrimを始める前にそのまま失敗する。
            var range=GetConnectedSafeRange(request);
            if(range==null || !PreviewSafeRange.Contains(boundary,range.Value.Start,range.Value.End)
                || !PreviewSafeRange.Contains(target,range.Value.Start,range.Value.End))return false;
            if (bytes <= 0) return false;
            var protectedSegments = ReverseProtectedSegments(request, boundary, target);
            if (protectedSegments == null) return false;
            if (bytes <= _capacityBytes - _sizeBytes) return true;
            long reclaimable = ReverseReclaimableBytes(protectedSegments);
            var plan = ReverseRoomCore(request,boundary,target,overlap);
            long available = Math.Max(_capacityBytes - _sizeBytes, plan?.Bytes ?? 0) + reclaimable;
            if (bytes > available) return false;
            while (bytes > _capacityBytes - _sizeBytes && EvictOne(protectedSegments: protectedSegments)) { }
            if (bytes <= _capacityBytes - _sizeBytes) return true;
            if (plan != null)
            {
                var (entry,first,last,_)=plan.Value;
                int kept=last-first+1;
                for(int i=0;i<first;i++)RemoveFirst(entry);
                bool tailRemoved=entry.Frames.Count>kept;
                while(entry.Frames.Count>kept)
                {
                    long removed = entry.Frames[^1].RawByteLength;
                    entry.Frames.RemoveAt(entry.Frames.Count-1);entry.Bytes-=removed;_sizeBytes-=removed;
                }
                if (tailRemoved)
                {
                    entry.SafeEnd=Math.Min(entry.SafeEnd,
                        entry.Clock.ToInputSeconds(entry.Frames[^1],entry.Frames.Count-1,request.Fps)+FrameSelector.Duration(entry.Frames[^1],request.Fps));
                    entry.ReachedInputEnd=false;
                }
                if(PerfTrace.Enabled)PerfTrace.Write("cache.reverse_trim",new{entry.Id,bytes,cacheBytes=_sizeBytes,kept,first,tailRemoved});
            }
            if (bytes <= _capacityBytes - _sizeBytes) return true;
            return TryReclaimDisplayedBefore(bytes, null, protectedSegments, "reverse_capacity_after_bridge_trim")
                && bytes <= _capacityBytes - _sizeBytes;
        }
    }

}

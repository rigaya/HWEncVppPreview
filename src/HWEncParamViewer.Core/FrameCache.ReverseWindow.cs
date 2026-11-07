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
            return Math.Max(_capacityBytes-_sizeBytes,ReverseRoomCore(request,boundary,target,overlap)?.Bytes??0);
        }
    }

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
        long retained=0;
        for(int i=first;i<=last;i++)retained+=entry.Frames[i].Data.LongLength;
        long free=_capacityBytes-(_sizeBytes-entry.Bytes)-retained;
        return free>0?(entry,first,last,free):null;
    }

    internal bool TryTrimReverseWindow(PreviewRequest request,double boundary,double target,double overlap,long bytes)
    {
        lock (_gate)
        {
            // 空きだけで収まる1080等では、既存bridgeや未来の保持を一切変えない。
            var range=GetConnectedSafeRange(request);
            if(range==null || !PreviewSafeRange.Contains(boundary,range.Value.Start,range.Value.End)
                || !PreviewSafeRange.Contains(target,range.Value.Start,range.Value.End))return false;
            if (bytes>0 && bytes<=_capacityBytes-_sizeBytes) return true;
            var plan=ReverseRoomCore(request,boundary,target,overlap);
            if (plan==null || bytes<=0 || bytes>plan.Value.Bytes) return false;
            var (entry,first,last,_)=plan.Value;
            int kept=last-first+1;
            for(int i=0;i<first;i++)RemoveFirst(entry);
            bool tailRemoved=entry.Frames.Count>kept;
            while(entry.Frames.Count>kept)
            {
                long removed=entry.Frames[^1].Data.LongLength;
                entry.Frames.RemoveAt(entry.Frames.Count-1);entry.Bytes-=removed;_sizeBytes-=removed;
            }
            if (tailRemoved)
            {
                entry.SafeEnd=Math.Min(entry.SafeEnd,
                    entry.Clock.ToInputSeconds(entry.Frames[^1],entry.Frames.Count-1,request.Fps)+FrameSelector.Duration(entry.Frames[^1],request.Fps));
                entry.ReachedInputEnd=false;
            }
            if(PerfTrace.Enabled)PerfTrace.Write("cache.reverse_trim",new{entry.Id,bytes,cacheBytes=_sizeBytes,kept,first,tailRemoved});
            return true;
        }
    }

}

using HWEncParamViewer.Core;
namespace HWEncParamViewer.Tests;

public sealed class ReverseWindowBudgetTests
{
    private static readonly Y4mHeader Header=new(){Width=2,Height=2,FpsNum=60,FpsDen=1};
    private static PreviewRequest Request()=>new(){EncoderPath="encoder",InputFile="input",Fps=60,TargetSeconds=10.1,Capabilities=new(true),TimestampOriginSeconds=1000};
    private static Y4mFrame Frame(int absoluteIndex)=>new(Header,1000+absoluteIndex/60d,1d/60,Enumerable.Repeat((byte)absoluteIndex,6).ToArray());
    private static (FrameCache Cache,long Id) Full()
    {
        var cache=new FrameCache(86*6);
        var request=Request();
        long id=cache.BeginSegment(request,new(1000,7,true),10);
        for(int i=0;i<86;i++)Assert.True(cache.Append(id,Frame(600+i)));
        cache.PinCurrent(id,10.1);
        return(cache,id);
    }
    [Fact]
    public void FutureTrimPreservesBridgeAndMakesReverseWindowCacheable()
    {
        var(cache,id)=Full();
        Assert.True(cache.ReverseWindowRoom(Request(),10,10.1,4d/60)>=43*6);
        Assert.True(cache.TryTrimReverseWindow(Request(),10,10.1,4d/60,43*6));
        var bridge=cache.GetSegment(id)!;
        Assert.Equal(8,bridge.Frames.Count);
        Assert.True(PreviewSafeRange.Contains(10.1,bridge.SafeStartSeconds,bridge.SafeEndSeconds));
        Assert.False(cache.TryGetAt(Request(),11,out _));
        var frames=Enumerable.Range(564,43).Select(Frame).ToArray();
        var stored=cache.StoreSegment(Request(),frames,new(1000,7,true),9.4,10.083333333333,false,true);
        Assert.NotNull(stored);
        Assert.True(stored.CanStitch);
        Assert.True(cache.CanStitch(Request()));
        Assert.True(cache.TryGetAt(Request(),9.5,out _));
        Assert.True(cache.TryGetAt(Request(),10.1,out _));
        Assert.InRange(cache.SizeBytes,1,86*6);
    }
    [Fact]
    public void FailedTrimDoesNotChangeFramesOrSelection()
    {
        var(cache,id)=Full();
        var before=cache.GetSegment(id)!;
        Assert.False(cache.TryTrimReverseWindow(Request(),10,11.4,4d/60,43*6));
        var after=cache.GetSegment(id)!;
        Assert.Equal(before.Frames,after.Frames);
        Assert.Equal(before.SafeEndSeconds,after.SafeEndSeconds);
        Assert.Equal(86*6,cache.SizeBytes);
    }
    [Fact]
    public void CompetingStoreMayRejectReverseWindowWithoutDeletingBridge()
    {
        var(cache,id)=Full();
        Assert.True(cache.TryTrimReverseWindow(Request(),10,10.1,4d/60,43*6));
        var unrelated=Request() with {VppArgs=new[]{"before"}};
        // closed/legacyは今回の回収対象なので、競合相手を回収できないactive区間にする。
        long active=cache.BeginSegment(unrelated,new(1000,0,true),0);
        foreach(var frame in Enumerable.Range(0,60).Select(Frame))Assert.True(cache.Append(active,frame));
        Assert.NotNull(cache.GetSegment(active));
        var bridge=cache.GetSegment(id)!;
        var frames=Enumerable.Range(564,43).Select(Frame).ToArray();
        Assert.Null(cache.StoreSegment(Request(),frames,new(1000,7,true),9.4,10.083333333333,false,true));
        Assert.Equal(bridge.Frames,cache.GetSegment(id)!.Frames);
        Assert.True(cache.TryGetAt(Request(),10.1,out _));
    }
    [Fact]
    public void BoundaryOutsideCurrentEntryIsNotTrimmedAsIfItWereOneBridge()
    {
        var(cache,id)=Full();
        Assert.Equal(0,cache.ReverseWindowRoom(Request(),9.9,10.1,4d/60));
        Assert.False(cache.TryTrimReverseWindow(Request(),9.9,10.1,4d/60,6));
        Assert.Equal(86,cache.GetSegment(id)!.Frames.Count);
    }
    [Fact]
    public void ClearedCacheHasNoBridgeToTrim()
    {
        var(cache,_)=Full();
        cache.Clear();
        Assert.Equal(0,cache.ReverseWindowRoom(Request(),10,10.1,4d/60));
        Assert.False(cache.TryTrimReverseWindow(Request(),10,10.1,4d/60,43*6));
        Assert.Equal(0,cache.SizeBytes);
    }
}

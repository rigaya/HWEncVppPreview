using HWEncVppPreview.Core;
namespace HWEncVppPreview.Tests;

public sealed class RawAnchorRetentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleFrameAnchorPreservesClockAndApprovalWithoutHoldingOtherFrames(bool timestamp)
    {
        var request=new PreviewRequest {EncoderPath="encoder",InputFile="input",Fps=30};
        var header=new Y4mHeader {Width=2,Height=2,FpsNum=30,FpsDen=1};
        var frames=Enumerable.Range(0,8).Select(i=>new Y4mFrame(header,timestamp?1000+i/30d:null,null,new byte[6])).ToArray();
        var segment=new PreviewSegmentSnapshot(12,request,new(1000,8,true) {FrameIndexOffset=20},frames,0,100,false,false) {CanStitch=false};
        var original=new PreviewFrameReference(segment,5);
        var anchor=original.RetainFrameOnly();
        Assert.Single(anchor.Segment.Frames);
        Assert.Same(original.Frame,anchor.Frame);
        Assert.Same(original.Frame.Data,anchor.Frame.Data);
        Assert.Equal(original.InputSeconds,anchor.InputSeconds);
        Assert.Equal(original.SegmentId,anchor.SegmentId);
        Assert.Equal(original.IsSafe,anchor.IsSafe);
        Assert.False(anchor.Segment.CanStitch);
        Assert.True(FrameSelector.FromSameSegment(original,anchor));
        Assert.Equal(original.InputSeconds,anchor.RetainFrameOnly().InputSeconds);
    }
}

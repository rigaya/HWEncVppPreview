using HWEncVppPreview.Core;
namespace HWEncVppPreview.Tests;

public sealed class FiniteRawBudgetTests
{
    [Fact]
    public void ForegroundAndBackgroundHaveIndependentSpaceWithinOneSharedGiB()
    {
        var budget=new PreviewFiniteBudget();
        using var foreground=budget.Open(0);
        using var background=budget.Open(1L<<30);
        using var third=budget.Open(1L<<30);
        Assert.Equal(1L<<29,foreground.CapacityBytes);
        Assert.True(foreground.TryAdd(1L<<29));
        Assert.True(background.TryAdd(1L<<29));
        Assert.Equal(1L<<30,budget.SizeBytes);
        Assert.False(third.TryAdd(1));
        foreground.Reset();
        Assert.True(third.TryAdd(1));
        foreground.Dispose();
        Assert.Equal((1L<<29)+1,budget.SizeBytes);
    }

    [Fact]
    public void SmallCacheAlsoLimitsEachFiniteWindow()
    {
        var budget=new PreviewFiniteBudget();
        using var window=budget.Open(18);
        Assert.Equal(18,window.CapacityBytes);
        Assert.True(window.TryAdd(12));
        Assert.False(window.TryAdd(7));
        window.Remove(6);
        Assert.True(window.TryAdd(12));
        Assert.Equal(18,budget.SizeBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RollingRetainsSelectedPixelsAndClockWithinByteLimit(bool timestamps)
    {
        var cache=new FrameCache(18);
        var backend=new Burst(timestamps);
        await using var controller=new PreviewController(backend,cache:cache,debounce:TimeSpan.Zero);
        PreviewFrameResult? selected=null;
        PreviewCompletion? complete=null;
        PreviewFailure? failure=null;
        controller.FrameReady+=value=>selected=value;
        controller.Completed+=value=>complete=value;
        controller.Failed+=value=>failure=value;
        controller.Submit(Request(timestamps));
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.NotNull(complete);
        Assert.NotNull(selected);
        Assert.Equal((byte)10,selected.Frame.Data[0]);
        Assert.Single(selected.Segment!.Frames);
        Assert.InRange(backend.MaxRetainedBytes,6,18);
        Assert.InRange(complete.Frames.Count,1,3);
        Assert.InRange(cache.SizeBytes,0,18);
        Assert.Contains("byte budget",complete.Result.StandardError);
        if(timestamps)
        {
            Assert.Equal(1010,selected.Frame.Xts);
            Assert.True(PreviewSafeRange.Contains(10,complete.Segment!.SafeStartSeconds,complete.Segment.SafeEndSeconds));
            Assert.True(complete.Segment.Clock.FrameIndexOffset>0);
            Assert.Same(selected.Frame,complete.Frames[FrameSelector.Select(complete.Segment,10,2)]);
        }
        else Assert.Equal(5,complete.Segment!.Clock.ToInputSeconds(selected.Frame,
            complete.Frames.ToList().IndexOf(selected.Frame),2));
    }

    [Fact]
    public async Task FrameThatCannotFitFailsExplicitlyWithoutPublishingWrongFrame()
    {
        var backend=new Burst(true);
        await using var controller=new PreviewController(backend,cache:new FrameCache(3),debounce:TimeSpan.Zero);
        PreviewFailure? failure=null;
        int completed=0;
        controller.Failed+=value=>failure=value;
        controller.Completed+=_=>++completed;
        controller.Submit(Request(true));
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.NotNull(failure);
        Assert.Contains("byte budget",failure.Reason);
        Assert.Equal(0,completed);
        Assert.Equal(0,backend.MaxRetainedBytes);
    }

    [Fact]
    public async Task TailGuardUsesReceivedClockWithoutKeepingItsPixels()
    {
        var backend=new Burst(true);
        var cache=new FrameCache(6);
        await using var controller=new PreviewController(backend,cache:cache,debounce:TimeSpan.Zero);
        PreviewCompletion? complete=null;
        PreviewFailure? failure=null;
        controller.Completed+=value=>complete=value;
        controller.Failed+=value=>failure=value;
        controller.Submit(Request(true));
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.NotNull(complete);
        Assert.Equal(1010,Assert.Single(complete.Frames).Xts);
        Assert.Equal(6,backend.MaxRetainedBytes);
        Assert.True(PreviewSafeRange.Contains(10,complete.Segment!.SafeStartSeconds,complete.Segment.SafeEndSeconds));
    }

    private static PreviewRequest Request(bool timestamp)=>new()
    {
        EncoderPath="fake",InputFile="input",TargetSeconds=timestamp?10:5,
        Fps=2,PreMarginSeconds=5,PostMarginSeconds=0.5,
        Capabilities=new(timestamp),TimestampOriginSeconds=timestamp?1000:null,
    };
    private sealed class Burst(bool timestamp):IPreviewBackend
    {
        public long MaxRetainedBytes;
        public Task<PreviewRunResult> RunAsync(PreviewRequest request,IPreviewSink sink,CancellationToken token=default)
        {
            var header=new Y4mHeader {Width=2,Height=2,FpsNum=2,FpsDen=1};
            sink.OnHeader(header);
            for(int i=0;i<12;i++)
            {
                token.ThrowIfCancellationRequested();
                var frame=new Y4mFrame(header,timestamp?1005+i/2d:null,null,Enumerable.Repeat((byte)i,6).ToArray());
                // 暖機込みの元の列では、目標の画素はi=10。
                sink.OnFrame(frame);
                var frames=(IEnumerable<Y4mFrame>)sink.GetType().GetProperty("Frames")!.GetValue(sink)!;
                MaxRetainedBytes=Math.Max(MaxRetainedBytes,frames.Sum(f=>f.Data.LongLength));
            }
            return Task.FromResult(new PreviewRunResult(0,""));
        }
        public ValueTask AbortAsync()=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}

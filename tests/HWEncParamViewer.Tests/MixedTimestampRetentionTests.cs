using HWEncParamViewer.Core;
namespace HWEncParamViewer.Tests;

public sealed class MixedTimestampRetentionTests
{
    [Theory]
    [InlineData(1d, false)]
    [InlineData(0d, true)]
    public async Task 有時刻が現れた列では時刻なしの仮候補を通知しない(double timestamp, bool selectable)
    {
        var backend=new MixedBackend(timestamp);
        await using var controller=new PreviewController(backend,cache:new FrameCache(0),debounce:TimeSpan.Zero);
        var images=new List<PreviewFrameResult>();
        controller.FrameReady+=images.Add;
        controller.Submit(new(){EncoderPath="mixed",InputFile="input",Fps=30,TargetSeconds=0});
        await controller.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if(selectable)
        {
            var image=Assert.Single(images);
            Assert.Equal(0,image.Frame.Xts);
            Assert.Equal((byte)1,image.Frame.Data[0]);
        }
        else Assert.Empty(images);
    }
    private sealed class MixedBackend(double timestamp):IPreviewBackend
    {
        public Task<PreviewRunResult> RunAsync(PreviewRequest request,IPreviewSink sink,CancellationToken token=default)
        {
            var header=new Y4mHeader{Width=2,Height=2,FpsNum=30,FpsDen=1};
            sink.OnHeader(header);
            sink.OnFrame(new(header,null,null,new byte[6]));
            sink.OnFrame(new(header,timestamp,null,Enumerable.Repeat((byte)1,6).ToArray()));
            sink.OnFrame(new(header,timestamp+1d/30,null,Enumerable.Repeat((byte)2,6).ToArray()));
            return Task.FromResult(new PreviewRunResult(0,""));
        }
        public ValueTask AbortAsync()=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}

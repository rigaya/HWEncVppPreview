using HWEncVppPreview.Core;
namespace HWEncVppPreview.Tests;

public sealed class OutputStartRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownVfrHeadRetriesSameSeekWithoutOutputSuppression(bool continuous)
    {
        var backend=new WideGap();
        await using var controller=new PreviewController(backend,debounce:TimeSpan.Zero);
        PreviewFrameResult? selected=null;
        PreviewFailure? failure=null;
        controller.FrameReady+=value=>selected=value;
        controller.Failed+=value=>failure=value;
        controller.Submit(new PreviewRequest {EncoderPath="encoder",InputFile="input",Fps=30,TargetSeconds=10,
            PreMarginSeconds=2,PostMarginSeconds=0.5,Continuous=continuous,Capabilities=new(true),TimestampOriginSeconds=1000});
        await controller.WaitForIdleAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.Null(failure);
        Assert.NotNull(selected);
        Assert.Equal(1010.002,selected.Frame.Xts!.Value,6);
        Assert.Equal(2,backend.Requests.Count);
        Assert.Equal(7,CommandBuilder.Plan(backend.Requests[0]).SeekSeconds);
        Assert.Equal(CommandBuilder.Plan(backend.Requests[0]).SeekSeconds,CommandBuilder.Plan(backend.Requests[1]).SeekSeconds);
        Assert.False(backend.Requests[0].DisableOutputStart);
        Assert.True(backend.Requests[1].DisableOutputStart);
        if(!continuous)Assert.Equal(CommandBuilder.BuildServer(backend.Requests[0]),CommandBuilder.BuildServer(backend.Requests[1]));
    }
    private sealed class WideGap:IPreviewBackend
    {
        public List<PreviewRequest> Requests {get;}=new();
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request,IPreviewSink sink,CancellationToken token=default)
        {
            Requests.Add(request);
            var header=new Y4mHeader {Width=2,Height=2,FpsNum=30,FpsDen=1};
            sink.OnHeader(header);
            double[] times=request.DisableOutputStart?[1007,1008.6,1010.002,1010.2,1010.4,1010.6,1010.8]:[1010.002,1010.2,1010.4,1010.6,1010.8];
            foreach(double time in times)
            {
                if(token.IsCancellationRequested)break;
                await sink.BeforeReadAsync(token);
                sink.OnFrame(new(header,time,0.2,Enumerable.Repeat((byte)7,6).ToArray()));
            }
            return new PreviewRunResult(0,"",token.IsCancellationRequested){OutputStartApplied=!request.DisableOutputStart};
        }
        public ValueTask AbortAsync()=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}

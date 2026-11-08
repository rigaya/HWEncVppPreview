using HWEncParamViewer.Testing;
using System.Text.Json;
using HWEncParamViewer.Core;
namespace HWEncParamViewer.Tests;

public sealed class OutputStartBackendTests
{
    private static PreviewRequest Request()=>new()
    {
        EncoderPath=BackendTests.FakeEncoderPath(),InputFile="input",Fps=2,TargetSeconds=10,
        PreMarginSeconds=3,PostMarginSeconds=0.5,Capabilities=new(true),TimestampOriginSeconds=1000
    };
    [Fact]
    public void AbsoluteBoundaryUsesViewerSeekAndDoesNotApplySafeToleranceAgain()
    {
        Assert.Equal(1_008_250_000,ServerBackend.OutputStartMicroseconds(Request(),true,true));
        Assert.Equal(0,ServerBackend.OutputStartMicroseconds(Request() with {TimestampOriginSeconds=-8.25},true,true));
        Assert.Equal(-1_000_000,ServerBackend.OutputStartMicroseconds(Request() with {TimestampOriginSeconds=-9.25},true,true));
    }
    [Fact]
    public void UnknownLegacyAndStartOfInputDoNotSendBoundary()
    {
        var request=Request();
        Assert.Null(ServerBackend.OutputStartMicroseconds(request,false,true));
        Assert.Null(ServerBackend.OutputStartMicroseconds(request,true,false));
        Assert.Null(ServerBackend.OutputStartMicroseconds(request with {Capabilities=new()},true,true));
        Assert.Null(ServerBackend.OutputStartMicroseconds(request with {TimestampOriginTrusted=false},true,true));
        Assert.Null(ServerBackend.OutputStartMicroseconds(request with {TimestampOriginSeconds=null},true,true));
        Assert.Null(ServerBackend.OutputStartMicroseconds(request with {TargetSeconds=0},true,true));
        Assert.Null(ServerBackend.OutputStartMicroseconds(request with {WarmupSeconds=0},true,true));
    }
    [Theory]
    [InlineData(null,false)]
    [InlineData("0",false)]
    [InlineData("2",false)]
    [InlineData("1",true)]
    public async Task OnlyCurrentSessionsExactCapabilityEnablesNewField(string? capability,bool expected)
    {
        string log=Path.Combine(Path.GetTempPath(),"output-start-"+Guid.NewGuid().ToString("N")+".jsonl");
        try
        {
            await using(var backend=new ServerBackend(configureStartInfo:start=>
            {
                start.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"]=log;
                start.Environment["FAKE_ENCODER_WINDOW_SEEK"]="1";
                if(capability!=null)start.Environment["FAKE_ENCODER_OUTPUT_START"]=capability;
                else start.Environment.Remove("FAKE_ENCODER_OUTPUT_START");
            })) Assert.Equal(0,(await backend.RunAsync(Request(),new Sink()).WaitAsync(TimeSpan.FromSeconds(15))).ExitCode);
            var commands=FakeLogFiles.ReadAllLines(log).Select(line=>JsonDocument.Parse(line).RootElement.Clone()).Where(x=>x.GetProperty("cmd").GetString()=="run").ToArray();
            var run=Assert.Single(commands);
            Assert.Equal(expected,run.TryGetProperty("output_start_us",out var value));
            if(expected)Assert.Equal(1_008_250_000,value.GetInt64());
            Assert.Equal(CommandBuilder.Plan(Request()).FrameCount,int.Parse(run.GetProperty("args").EnumerateArray().Select(x=>x.GetString()).SkipWhile(x=>x!="--frames").Skip(1).First()!));
        }
        finally{FakeLogFiles.Delete(log);}
    }
    private sealed class Sink:IPreviewSink
    {
        public void OnHeader(Y4mHeader header){}
        public void OnFrame(Y4mFrame frame){}
        public void OnOutput(){}
    }
}

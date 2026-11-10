using HWEncVppPreview.Testing;
using System.Text.Json;
using HWEncVppPreview.Core;
namespace HWEncVppPreview.Tests;

public sealed class WindowSeekBackendTests
{
    private static readonly TimeSpan Safety = TestTimeouts.Hang;

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("2", false)]
    [InlineData("1", true)]
    public async Task OptInIsSentOnlyForAnExplicitReadyCapability(string? capability, bool expected)
    {
        string log = Path.Combine(Path.GetTempPath(), "window-seek-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            await using (var backend = new ServerBackend(configureStartInfo: start =>
            {
                start.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = log;
                if (capability != null) start.Environment["FAKE_ENCODER_WINDOW_SEEK"] = capability;
                else start.Environment.Remove("FAKE_ENCODER_WINDOW_SEEK");
            }))
            {
                Assert.Equal(0, (await backend.RunAsync(Request(), new Sink()).WaitWithDiagnosticsAsync(Safety)).ExitCode);
            }
            var runs = ReadRuns(log);
            Assert.Single(runs);
            Assert.Equal(expected, runs[0].TryGetProperty("window_seek", out var value));
            if (expected) Assert.True(value.GetBoolean());
            Assert.Equal(CommandBuilder.BuildServer(Request()), runs[0].GetProperty("args").EnumerateArray().Select(x => x.GetString()!).ToArray());
        }
        finally { FakeLogFiles.Delete(log); }
    }

    [Theory]
    [InlineData(false, "--trim", "0:10")]
    [InlineData(false, "--trim=0:10", "")]
    [InlineData(true, "--trim", "0:10")]
    public async Task ExplicitTrimKeepsTheLegacyInputFrameNumbering(bool vpp, string option, string value)
    {
        string log=Path.Combine(Path.GetTempPath(),"window-trim-"+Guid.NewGuid().ToString("N")+".jsonl");
        try
        {
            await using(var backend=new ServerBackend(configureStartInfo:start=>
            {
                start.Environment["FAKE_ENCODER_WINDOW_SEEK"]="1";
                start.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"]=log;
            }))
            {
                string[] trim=value.Length==0?[option]:[option,value];
                var request=vpp?Request() with {VppArgs=trim}:Request() with {InputArgs=trim};
                Assert.Equal(0,(await backend.RunAsync(request,new Sink()).WaitWithDiagnosticsAsync(Safety)).ExitCode);
            }
            Assert.False(Assert.Single(ReadRuns(log)).TryGetProperty("window_seek",out _));
        }
        finally{FakeLogFiles.Delete(log);}
    }

    [Fact]
    public async Task RestartUsesEachNewSessionsCapability()
    {
        string log = Path.Combine(Path.GetTempPath(), "window-seek-restart-" + Guid.NewGuid().ToString("N") + ".jsonl");
        int started = 0;
        try
        {
            await using (var backend = new ServerBackend(restartPolicy: new ServerRestartPolicy(maxRequests: 1), configureStartInfo: start =>
            {
                start.Environment["FAKE_ENCODER_COMMAND_LOG_FILE"] = log;
                start.Environment["FAKE_ENCODER_WINDOW_SEEK"] = ++started == 2 ? "0" : "1";
            }))
            {
                for (int i = 0; i < 3; ++i)
                    Assert.Equal(0, (await backend.RunAsync(Request(), new Sink()).WaitWithDiagnosticsAsync(Safety)).ExitCode);
            }
            Assert.Equal(3, started);
            var runs = ReadRuns(log);
            Assert.Equal(3, runs.Length);
            Assert.True(runs[0].GetProperty("window_seek").GetBoolean());
            Assert.False(runs[1].TryGetProperty("window_seek", out _));
            Assert.True(runs[2].GetProperty("window_seek").GetBoolean());
        }
        finally { FakeLogFiles.Delete(log); }
    }

    private static JsonElement[] ReadRuns(string file) => FakeLogFiles.ReadAllLines(file).Select(line =>
    {
        using var json = JsonDocument.Parse(line);
        return json.RootElement.Clone();
    }).Where(root => root.GetProperty("cmd").GetString() == "run").ToArray();

    private static PreviewRequest Request() => new()
    {
        EncoderPath = BackendTests.FakeEncoderPath(), InputFile = "window seek input.ts", Fps = 30,
        TargetSeconds = 4, PreMarginSeconds = 0, PostMarginSeconds = 0.1,
    };
    private sealed class Sink : IPreviewSink
    {
        public void OnHeader(Y4mHeader header) { }
        public void OnOutput() { }
        public void OnFrame(Y4mFrame frame) { }
    }
}

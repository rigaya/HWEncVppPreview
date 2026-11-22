using System.Globalization;
using System.Text.Json;
using HWEncVppPreview.Cli;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.Tests;

public sealed partial class AmatsukazeEngineTests
{
    [Theory]
    [InlineData("bob", 3050, 3071, 0.021, 0.013)]
    [InlineData("decimate", 3155, 3211, 0.056, 0.039)]
    public async Task 入力枚数を要するフィルタで実出力の隣へ前後に進む(
        string mode, int seekMilliseconds, int nextMilliseconds, double duration, double nextDuration)
    {
        await using var protocol = new EncoderProtocolFixture();
        await using (var oldBackend = protocol.Encoder.CreateBackend())
        {
            var request = new PreviewRequest
            {
                EncoderPath = "fake", InputFile = "source.avs", InputArgs = ["--avs"], Fps = 30,
                TargetSeconds = seekMilliseconds / 1000d, PreMarginSeconds = 0, PostMarginSeconds = 0,
                VppArgs = [EncoderPeerFixture.Filter, "mode=" + mode],
            };
            var emptyOutput = new CountedSink();
            var oldResult = await oldBackend.RunAsync(request, emptyOutput);
            Assert.Equal(0, oldResult.ExitCode);
            Assert.Equal(0, emptyOutput.Frames);
        }

        await protocol.ConfigureEncoderAsync(mode);
        var seek = await protocol.CommandAsync(FormattableString.Invariant(
            $"{{\"cmd\":\"view\",\"id\":1,\"gen\":1,\"op\":\"seek\",\"seconds\":{seekMilliseconds / 1000d},\"format\":\"yuv\"}}"), 1);
        AssertSelected(seek, seekMilliseconds, duration);
        var next = await protocol.CommandAsync("""{"cmd":"view","id":2,"gen":1,"op":"step","direction":"next"}""", 2);
        AssertSelected(next, nextMilliseconds, nextDuration);
        var previous = await protocol.CommandAsync("""{"cmd":"view","id":3,"gen":1,"op":"step","direction":"previous"}""", 3);
        AssertSelected(previous, seekMilliseconds, duration);

        var runs = protocol.Encoder.Runs.Where(run => run.Args.Contains(EncoderPeerFixture.Filter)).ToArray();
        Assert.Contains(runs, run => run.InputFrames == 1 && run.OutputFrames == 0);
        var newRuns = runs.Skip(1).ToArray();
        Assert.NotEmpty(newRuns);
        Assert.All(newRuns, run =>
        {
            Assert.True(run.InputFrames >= EncoderPeerFixture.DelayedInputs);
            Assert.True(run.OutputFrames > 0);
            Assert.False(run.HasOutputStart);
            Assert.Contains("--avs", run.Args);
            Assert.Contains("--interlace", run.Args);
            Assert.Contains("tff", run.Args);
        });
        Assert.Contains(newRuns, run => run.Args.Contains("--seek"));

        protocol.Input.Send("""{"cmd":"frame","id":4,"gen":1,"pos":9}""");
        Assert.StartsWith("AMTPREVIEW ERROR id=4 gen=1 ", await protocol.Output.LineAsync());
        await protocol.FinishAsync();
    }

    [Fact]
    public async Task 前後比較と同じ表示の再取得は画像長で応答を分離する()
    {
        await using var protocol = new EncoderProtocolFixture();
        await protocol.ConfigureEncoderAsync("bob");
        var before = await protocol.CommandAsync("""{"cmd":"view","id":1,"gen":1,"op":"display","mode":"before"}""", 1);
        Assert.Equal("before", Assert.Single(before[^1].Frames).Side);
        var comparison = await protocol.CommandAsync("""{"cmd":"view","id":2,"gen":1,"op":"display","mode":"sideBySide"}""", 2);
        var paired = comparison[^1];
        Assert.Equal(2, paired.Frames.Length);
        Assert.Equal(EncoderPeerFixture.MakePixels(0, false), paired.Frames.Single(frame => frame.Side == "before").Pixels);
        Assert.Equal(EncoderPeerFixture.MakePixels(0, true), paired.Frames.Single(frame => frame.Side == "after").Pixels);

        var snapshot = await protocol.CommandAsync(FormattableString.Invariant(
            $"{{\"cmd\":\"view\",\"id\":3,\"gen\":1,\"op\":\"snapshot\",\"revision\":{paired.Revision},\"scale\":2}}"), 3);
        var rescaled = Assert.Single(snapshot);
        Assert.Equal(paired.Revision, rescaled.Revision);
        Assert.All(rescaled.Frames, frame =>
        {
            Assert.Equal(2, frame.Width);
            Assert.Equal(2, frame.Height);
            Assert.Equal(6, frame.Pixels.Length);
        });
        await protocol.FinishAsync();
    }

    [Fact]
    public async Task 暫定から確定した同一の画像は再送せず別の画素や再表示は送る()
    {
        await using var protocol = new EncoderProtocolFixture();
        await protocol.ConfigureEncoderAsync("bob");
        var after = CreateFrame(1);
        var before = CreateFrame(2);
        var delivery = new EncoderPreviewViewDelivery();
        var provisional = await SendAsync(1, after, before with { IsProvisional = true }, delivery);
        Assert.All(provisional.Frames, frame => Assert.Equal(EncoderPeerFixture.FrameBytes, frame.Pixels.Length));
        Assert.True(provisional.Frames.Single(frame => frame.Side == "before").Provisional);

        var confirmed = await SendAsync(2, after, before, delivery);
        Assert.Equal(2, confirmed.Frames.Length);
        Assert.All(confirmed.Frames, frame =>
        {
            Assert.True(frame.Reuse);
            Assert.False(frame.Provisional);
            Assert.Empty(frame.Pixels);
        });

        // 同じXtsでもCoreが選び直した異なる画像は送る。
        var different = CreateFrame(3);
        var changed = await SendAsync(3, after, different, delivery);
        Assert.True(changed.Frames.Single(frame => frame.Side == "after").Reuse);
        var replacement = changed.Frames.Single(frame => frame.Side == "before");
        Assert.False(replacement.Reuse);
        Assert.All(replacement.Pixels, pixel => Assert.Equal(3, pixel));

        await SendAsync(4, after, null, delivery);
        var restored = await SendAsync(5, after, different, delivery);
        Assert.False(restored.Frames.Single(frame => frame.Side == "before").Reuse);

        var snapshot = await SendAsync(6, after, different, null, "png");
        Assert.All(snapshot.Frames, frame =>
        {
            Assert.False(frame.Reuse);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, frame.Pixels[..8]);
        });
        await protocol.FinishAsync();

        static EncoderPreviewFrame CreateFrame(byte pixel)
        {
            var header = new Y4mHeader { Width = 4, Height = 4, FpsNum = 30, FpsDen = 1 };
            var frame = new Y4mFrame(header, 1, null, Enumerable.Repeat(pixel, header.FrameBytes).ToArray());
            return new(frame, new(1, 1, null, 1d / 30), 1);
        }

        async Task<EngineView> SendAsync(long revision, EncoderPreviewFrame afterFrame, EncoderPreviewFrame? beforeFrame,
            EncoderPreviewViewDelivery? previous, string format = "yuv")
        {
            var state = new EncoderPreviewState(1, revision, 1, 8, null, EncoderPreviewDisplayMode.SideBySide,
                PreviewProgress.Done, false, false, null, "", false);
            using var view = new EncoderPreviewView(state, afterFrame, beforeFrame, null);
            return await protocol.SendViewAsync(view, previous, format);
        }
    }

    [Fact]
    public async Task 取消を受け付けた要求の応答を捨て同じ世代で読み直す()
    {
        await using var protocol = new EncoderProtocolFixture();
        await protocol.ConfigureEncoderAsync("bob");
        await protocol.CommandAsync("""{"cmd":"view","id":1,"gen":1,"op":"seek","seconds":0.05}""", 1);
        var blocked = protocol.Encoder.BlockNextFilteredRun();
        try
        {
            protocol.Input.Send("""{"cmd":"view","id":2,"gen":1,"op":"seek","seconds":3.05}""");
            await blocked.Started.Task;
            protocol.Input.Send("""{"cmd":"cancel","id":2}""");
            protocol.Input.Send("""{"cmd":"unknown"}""");
            Assert.StartsWith("AMTPREVIEW ERROR id=0 gen=1 ", await protocol.Output.LineAsync());
        }
        finally { blocked.Released.TrySetResult(); }

        var retry = await protocol.CommandAsync("""{"cmd":"view","id":3,"gen":1,"op":"seek","seconds":4.05}""", 3);
        AssertSelected(retry, 4050, 0.021);
        await protocol.FinishAsync();
    }

    [Fact]
    public async Task 世代切替後は保留中のエンコーダ要求から応答を返さない()
    {
        await using var protocol = new EncoderProtocolFixture();
        await protocol.ConfigureEncoderAsync("bob");
        await protocol.CommandAsync("""{"cmd":"view","id":1,"gen":1,"op":"seek","seconds":0.05}""", 1);
        var blocked = protocol.Encoder.BlockNextFilteredRun();
        try
        {
            protocol.Input.Send("""{"cmd":"view","id":2,"gen":1,"op":"seek","seconds":3.05}""");
            await blocked.Started.Task;
            await protocol.Input.SendAndObserveConsumptionAsync("""{"cmd":"config","gen":2,"kind":"avs","path":"next.avs"}""");
        }
        finally { blocked.Released.TrySetResult(); }

        Assert.StartsWith("AMTPREVIEW INFO gen=2 frames=20", await protocol.Output.LineAsync());
        protocol.Input.Send("""{"cmd":"frame","id":3,"gen":2,"pos":8}""");
        string header = await protocol.Output.LineAsync();
        Assert.StartsWith("AMTPREVIEW FRAME id=3 gen=2 pos=8 ", header);
        Assert.All(await protocol.Output.BytesAsync(24), pixel => Assert.Equal(8, pixel));
        await protocol.FinishAsync();
    }

    [Fact]
    public async Task 応答ヘッダ送信後の取消でも画像を書き切り次の境界を保つ()
    {
        await using var protocol = new EncoderProtocolFixture();
        await protocol.ConfigureEncoderAsync("bob");
        await protocol.CommandAsync("""{"cmd":"view","id":1,"gen":1,"op":"seek","seconds":0.05}""", 1);
        var resume = protocol.Output.BlockAfterWritingLine("AMTPREVIEW VIEW id=2 gen=1 ");
        string header;
        try
        {
            protocol.Input.Send("""{"cmd":"view","id":2,"gen":1,"op":"snapshot"}""");
            header = await protocol.Output.LineAsync();
            Assert.StartsWith("AMTPREVIEW VIEW id=2 gen=1 ", header);
            await protocol.Input.SendAndObserveConsumptionAsync("""{"cmd":"cancel","id":2}""");
        }
        finally { resume.TrySetResult(); }

        var inFlight = await protocol.ReadViewAsync(header, 2);
        Assert.Equal(EncoderPeerFixture.MakePixels(50, true), Assert.Single(inFlight.Frames).Pixels);
        var following = await protocol.CommandAsync("""{"cmd":"view","id":3,"gen":1,"op":"snapshot"}""", 3);
        AssertSelected(following, 50, 0.021);
        await protocol.FinishAsync();
    }

    private static void AssertSelected(EngineView[] views, int inputMilliseconds, double duration)
    {
        var view = views[^1];
        var frame = Assert.Single(view.Frames);
        Assert.Equal("after", frame.Side);
        Assert.Equal(inputMilliseconds / 1000d, frame.InputSeconds, 7);
        Assert.Equal(inputMilliseconds / 1000d, view.CurrentSeconds, 7);
        Assert.NotNull(frame.Xts);
        Assert.Equal(duration, Assert.IsType<double>(frame.Xdur), 8);
        Assert.Equal(EncoderPeerFixture.MakePixels(inputMilliseconds, true), frame.Pixels);
    }

    private sealed record EngineFrame(string Side, double InputSeconds, double? Xts, double? Xdur,
        int Width, int Height, byte[] Pixels, bool Reuse, bool Provisional);
    private sealed record EngineView(long Revision, double CurrentSeconds, EngineFrame[] Frames);

    private sealed class CountedSink : IPreviewSink
    {
        public int Frames { get; private set; }
        public void OnFrame(Y4mFrame frame) => ++Frames;
        public void OnHeader(Y4mHeader header) { }
        public void OnOutput() { }
    }

    private sealed class EncoderProtocolFixture : IAsyncDisposable
    {
        public ChannelStream Input { get; } = new();
        public ChannelStream Output { get; } = new();
        public EncoderPeerFixture Encoder { get; } = new(new FakeTimeProvider());

        private readonly Task _run;
        private readonly AmatsukazeEngine _engine;

        public EncoderProtocolFixture()
        {
            _engine = new AmatsukazeEngine(Input, Output, TextWriter.Null, "/tmp", "/tmp",
                new() { ["QSVEnc"] = "fake" }, (_, _) => new Source(), encoderSessionFactory: Encoder.CreateSession);
            _run = RunEngineAsync(_engine, Output);
        }

        public async Task ConfigureEncoderAsync(string mode)
        {
            Assert.Equal("AMTPREVIEW READY version=2", await Output.LineAsync());
            Input.Send(JsonSerializer.Serialize(new
            {
                cmd = "config", gen = 1, kind = "encoder", encoder = "QSVEnc", interlace = "tff",
                vpp = new[] { EncoderPeerFixture.Filter, "mode=" + mode },
            }));
            string info = await Output.LineAsync();
            Assert.StartsWith("AMTPREVIEW INFO gen=1 ", info);
            var fields = Fields(info);
            Assert.Equal("encoder", fields["kind"]);
            Assert.Equal("30", fields["fpsnum"]);
            Assert.Equal("1", fields["fpsden"]);
            Assert.Equal(8, double.Parse(fields["durationSeconds"], CultureInfo.InvariantCulture));
        }

        public async Task<EngineView> SendViewAsync(EncoderPreviewView view, EncoderPreviewViewDelivery? delivery, string format)
        {
            Assert.True(await _engine.WriteViewAsync(9, 1, view, new(format, 1, null), includeOldFrames: true,
                CancellationToken.None, delivery: delivery));
            return await ReadViewAsync(await Output.LineAsync(), 9);
        }

        public async Task<EngineView[]> CommandAsync(string json, long id)
        {
            Input.Send(json);
            var views = new List<EngineView>();
            var latest = new Dictionary<string, EngineFrame>(StringComparer.Ordinal);
            string prefix = FormattableString.Invariant($"AMTPREVIEW DONE id={id} gen=1 ");
            while (true)
            {
                string line = await Output.LineAsync();
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    Assert.Equal("ok", Fields(line)["status"]);
                    Assert.NotEmpty(views);
                    return views.ToArray();
                }
                var view = await ReadViewAsync(line, id);
                var frames = view.Frames.Select(frame =>
                {
                    if (!frame.Reuse) return frame;
                    Assert.True(latest.TryGetValue(frame.Side, out var previous));
                    return frame with { Width = previous!.Width, Height = previous.Height, Pixels = previous.Pixels };
                }).ToArray();
                latest = frames.ToDictionary(frame => frame.Side, StringComparer.Ordinal);
                views.Add(view with { Frames = frames });
            }
        }

        public async Task<EngineView> ReadViewAsync(string line, long id)
        {
            Assert.StartsWith(FormattableString.Invariant($"AMTPREVIEW VIEW id={id} gen=1 "), line);
            var fields = Fields(line);
            long revision = long.Parse(fields["revision"], CultureInfo.InvariantCulture);
            int metaBytes = int.Parse(fields["metaBytes"], CultureInfo.InvariantCulture);
            int bytes = int.Parse(fields["bytes"], CultureInfo.InvariantCulture);
            using var metadata = JsonDocument.Parse(await Output.BytesAsync(metaBytes));
            JsonElement root = metadata.RootElement;
            var frames = new List<EngineFrame>();
            foreach (JsonElement item in root.GetProperty("frames").EnumerateArray())
            {
                string side = item.GetProperty("side").GetString()!;
                int frameBytes = item.GetProperty("bytes").GetInt32();
                bool reuse = item.TryGetProperty("reuse", out var flag) && flag.GetBoolean();
                Assert.Equal(reuse, frameBytes == 0);
                frames.Add(new(side, item.GetProperty("inputSeconds").GetDouble(),
                    NullableNumber(item.GetProperty("xts")), NullableNumber(item.GetProperty("xdur")),
                    reuse ? 0 : item.GetProperty("width").GetInt32(), reuse ? 0 : item.GetProperty("height").GetInt32(),
                    reuse ? [] : await Output.BytesAsync(frameBytes), reuse, item.GetProperty("provisional").GetBoolean()));
            }
            Assert.Equal(bytes, frames.Sum(frame => frame.Pixels.Length));
            return new(revision, root.GetProperty("currentSeconds").GetDouble(), frames.ToArray());
        }

        public async Task FinishAsync()
        {
            Input.Send("""{"cmd":"quit"}""");
            Assert.Equal(0, await Output.ReadAsync(new byte[1]));
            await _run;
        }

        public async ValueTask DisposeAsync() => await CompleteEngineAsync(Input, _run);

        private static Dictionary<string, string> Fields(string line) => line.Split(' ').Skip(2)
            .Select(field => field.Split('=', 2)).Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

        private static double? NullableNumber(JsonElement number)
            => number.ValueKind == JsonValueKind.Null ? null : number.GetDouble();
    }
}

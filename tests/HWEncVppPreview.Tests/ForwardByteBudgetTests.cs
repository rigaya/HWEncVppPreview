using System.Threading.Channels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class ForwardByteBudgetTests
{
    [Theory]
    [InlineData(30, 2400, 12)]
    [InlineData(60, 2400, 11.6666666667)]
    [InlineData(60, 480, 10.3333333333)]
    [InlineData(60, 48, 10.0666666667)]
    public async Task 実byteと最低四枚でpauseし小さい二秒窓は変わらない(int fps, long capacity, double expected)
    {
        var backend = new Backend(fps);
        await using var controller = new PreviewController(backend, cache: new FrameCache(capacity), debounce: TimeSpan.Zero);
        var request = new PreviewRequest { EncoderPath = "stub", InputFile = "input", Fps = fps,
            TargetSeconds = 10, Continuous = true, Capabilities = new(true), TimestampOriginSeconds = 1000 };
        controller.Submit(request);
        double last = await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.InRange(last, expected - 1e-7, expected + 1.0 / fps + 1e-7);
        await controller.WaitForCurrentDemandAsync().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        // 目標を進めると同じstreamのgateが開き、実byteの先読みも移動する。
        controller.Submit(request with { TargetSeconds = 10.1, CorrelationId = 2 }, navigation: true);
        double next = await backend.Pauses.Reader.ReadAsync().AsTask().WaitWithDiagnosticsAsync(TestTimeouts.Hang);
        Assert.True(next > last);
        Assert.Equal(1, backend.Calls);
    }

    private static PreviewSegmentSnapshot Segment(params (double? Time, int Bytes)[] values)
    {
        var request = new PreviewRequest { EncoderPath = "stub", InputFile = "input", Fps = 999 };
        var frames = values.Select(value => new Y4mFrame(new Y4mHeader { Width = 2, Height = 2 },
            value.Time, null, new byte[value.Bytes])).ToArray();
        return new(1, request, new(1000, 0, true), frames, 0, 100, true, false);
    }

    [Fact]
    public void 可変byteと不等間隔Xtsをそのまま合計しfps推測をしない()
    {
        var segment = Segment((1009, 100), (1010, 100), (1010.001, 5), (1010.02, 7), (1010.5, 9), (1011.9, 11));
        var result = PreviewForwardReadBudget.Evaluate(segment, 10, 11.9, 128);
        Assert.False(result.NeedsRead); Assert.Equal("bytes", result.Reason);
        Assert.Equal(32, result.AheadBytes); Assert.Equal(4, result.AheadFrames);
        Assert.True(PreviewForwardReadBudget.Evaluate(segment, 10, 11.9, 132).NeedsRead);
    }

    [Fact]
    public void 巨大三枚では停止せず四枚目でbyte停止する()
    {
        var three = Segment((1010.1, 100), (1010.2, 100), (1010.3, 100));
        Assert.True(PreviewForwardReadBudget.Evaluate(three, 10, 10.3, 1000).NeedsRead);
        var four = Segment((1010.1, 100), (1010.2, 100), (1010.3, 100), (1010.4, 100));
        Assert.False(PreviewForwardReadBudget.Evaluate(four, 10, 10.4, 1000).NeedsRead);
    }

    [Fact]
    public void 疎なVfrは四枚未満でも既存二秒上限で停止する()
    {
        var result = PreviewForwardReadBudget.Evaluate(Segment((1010, 6), (1012.5, 6)), 10, 12.5, 1000);
        Assert.False(result.NeedsRead); Assert.Equal("time", result.Reason);
    }

    [Fact]
    public void Xts欠落と未信頼時計にはbyte制限を適用しない()
    {
        var valid = Segment((1010.1, 6), (1010.2, 6), (1010.3, 6), (1010.4, 6));
        Assert.True(PreviewForwardReadBudget.Evaluate(valid with { Clock = new(1000, 0, false) }, 10, 10.4, 24).NeedsRead);
        var missing = Segment((1010.1, 6), (null, 6), (1010.3, 6), (1010.4, 6));
        Assert.True(PreviewForwardReadBudget.Evaluate(missing, 10, 10.4, 24).NeedsRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 正の容量が無いときは既存時間上限だけを使う(long capacity)
    {
        var segment = Segment((1010.1, 6), (1010.2, 6), (1010.3, 6), (1010.4, 6));
        Assert.True(PreviewForwardReadBudget.Evaluate(segment, 10, 10.4, capacity).NeedsRead);
        Assert.False(PreviewForwardReadBudget.Evaluate(segment, 10, 12, capacity).NeedsRead);
    }

    private sealed class Backend(int fps) : IPreviewBackend
    {
        public Channel<double> Pauses { get; } = Channel.CreateUnbounded<double>();
        public int Calls { get; private set; }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async Task<PreviewRunResult> RunAsync(PreviewRequest request, IPreviewSink sink, CancellationToken cancellationToken = default)
        {
            ++Calls;
            double seek = CommandBuilder.Plan(request).SeekSeconds ?? 0;
            double last = seek;
            var header = new Y4mHeader { Width = 2, Height = 2, FpsNum = fps, FpsDen = 1 };
            for (int index = 0; ; ++index)
            {
                var permission = sink.BeforeReadAsync(cancellationToken);
                if (!permission.IsCompleted) { sink.OnReadStateChanged(false); Pauses.Writer.TryWrite(last); }
                await permission;
                sink.OnReadStateChanged(true);
                cancellationToken.ThrowIfCancellationRequested();
                last = seek + index / (double)fps;
                sink.OnOutput();
                sink.OnFrame(new(header, 1000 + last, 1d / fps, new byte[6]));
            }
        }
    }
}

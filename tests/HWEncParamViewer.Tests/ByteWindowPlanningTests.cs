using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class ByteWindowPlanningTests
{
    private static PreviewRequest Physical() => new()
    {
        EncoderPath = "encoder", InputFile = "input", Fps = 60000d / 1001, TargetSeconds = 100,
        PreMarginSeconds = 5, PostMarginSeconds = 1, SeekSecondsOverride = 95, WarmupSeconds = 2,
        Capabilities = new(true), TimestampOriginSeconds = 1000,
    };

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void FourKShortensDisplayAndOverlapButPreservesWarmupAndGuard(int depth)
    {
        var header = new Y4mHeader { Width = 3840, Height = 2160, BitDepth = depth, FpsNum = 60000, FpsDen = 1001 };
        var original = Physical();
        Assert.True(PreviewByteWindow.TryLimit(original, header, 1L << 29, out var limited));
        Assert.Equal(2, limited.WarmupSeconds);
        Assert.Equal(0.5 + 4 / header.Fps, limited.PostMarginSeconds, 9);
        Assert.Equal(original.TargetSeconds, limited.TargetSeconds);
        Assert.Equal(original.Fps, limited.Fps);
        Assert.True(CommandBuilder.Plan(limited).SeekSeconds > 95);
        double safeBegin = CommandBuilder.Plan(limited).SeekSeconds!.Value + limited.WarmupSeconds;
        double retainedDuration = limited.TargetSeconds - safeBegin + limited.PostMarginSeconds - 0.5;
        Assert.InRange((long)Math.Ceiling(retainedDuration * header.Fps) * header.FrameBytes, 1, 1L << 29);
    }

    [Fact]
    public void FullHdKeepsExistingWindowAndHalfSecondOverlap()
    {
        var header = new Y4mHeader { Width = 1440, Height = 1080, FpsNum = 60000, FpsDen = 1001 };
        var original = Physical();
        Assert.True(PreviewByteWindow.TryLimit(original, header, 1L << 29, out var limited));
        Assert.Equal(original, limited);
    }

    [Fact]
    public void BoundaryAtInputStartDoesNotInventWarmupSkip()
    {
        var header = new Y4mHeader { Width = 3840, Height = 2160, FpsNum = 60000, FpsDen = 1001 };
        var original = Physical() with { TargetSeconds = 1, SeekSecondsOverride = 0 };
        Assert.True(PreviewByteWindow.TryLimit(original, header, 1L << 29, out var limited));
        Assert.Null(CommandBuilder.Plan(limited).SeekSeconds);
        Assert.Equal(original.WarmupSeconds, limited.WarmupSeconds);
        Assert.Equal(0, PreviewWarmupPolicy.SafeStartSeconds(limited, false));
    }

    [Fact]
    public void BudgetUnableToHoldOneDisplayFrameReturnsFalse()
    {
        var header = new Y4mHeader { Width = 3840, Height = 2160, FpsNum = 60000, FpsDen = 1001 };
        Assert.False(PreviewByteWindow.TryLimit(Physical(), header, header.FrameBytes, out _));
    }

    [Fact]
    public void OutputRateRatherThanInputRateSizesRetainedFrameCount()
    {
        var header = new Y4mHeader { Width = 3840, Height = 2160, FpsNum = 60000, FpsDen = 1001 };
        Assert.True(PreviewByteWindow.TryLimit(Physical(), header, 1L << 29, out var a));
        Assert.True(PreviewByteWindow.TryLimit(Physical() with { Fps = 30000d / 1001 }, header, 1L << 29, out var b));
        Assert.Equal(a.PreMarginSeconds, b.PreMarginSeconds);
        Assert.Equal(a.PostMarginSeconds, b.PostMarginSeconds);
        Assert.Equal(a.SeekSecondsOverride, b.SeekSecondsOverride);
    }
}

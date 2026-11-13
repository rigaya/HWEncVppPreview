using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class SourceDurationTests
{
    [Theory]
    [InlineData("avs: source: 4550 frames, 30000/1001 fps", 1518183333L)]
    [InlineData("vpy: source: 4550 frames, 30000/1001 fps", 1518183333L)]
    [InlineData("avi: source: 3501 frames, 30/1 fps", 1167000000L)]
    [InlineData("vpy(vs4): source: 300 frames, 30/1 fps", 100000000L)]
    [InlineData("[12:45:36] [debug] avs: source: 3 frames, 30/1 fps\r\n", 1000000L)]
    [InlineData("\u001b[35mvpy(vs4): source: \u001b[39m4550\u001b[0m frames, 30000/1001 fps\r\n", 1518183333L)]
    [InlineData("source:\t1 frames, 20000000/1 fps", 1L)]
    [InlineData("source: 3 frames, 20000000/1 fps", 2L)]
    [InlineData("source: 1 frames, 40000000/1 fps", 0L)]
    [InlineData("source: 9223372036854775807 frames, 9223372036854775807/1 fps", 10000000L)]
    [InlineData("source: 9223372036854775807 frames, 10000000/1 fps", long.MaxValue)]
    public void 総枚数と有理数fpsから最後に一回だけticksへ丸める(string log, long ticks)
    {
        Assert.Equal(TimeSpan.FromTicks(ticks), InputProbe.ParseDuration(log));
    }

    [Fact]
    public void sourceがあれば別の音声Input節のDurationより映像自身の長さを優先する()
    {
        string log = "Input #0, mp3, from 'audio.mp3':\n  Duration: 01:00:00.00, start: 0\n"
            + "avs: source: 4550 frames, 30000/1001 fps\n"
            + "Input #1, wav, from 'another.wav':\n  Duration: 02:00:00.00, start: 0\n";
        Assert.Equal(TimeSpan.FromTicks(1518183333), InputProbe.ParseDuration(log, "video.avs"));
    }

    [Fact]
    public void sourceが複数なら最後の行を使う()
    {
        string log = "avs: source: 300 frames, 30/1 fps\n"
            + "vpy(vs4): source: 4550 frames, 30000/1001 fps\n";
        Assert.Equal(TimeSpan.FromTicks(1518183333), InputProbe.ParseDuration(log));
    }

    [Theory]
    [InlineData("0 frames, 30/1 fps")]
    [InlineData("300 frames, 0/1 fps")]
    [InlineData("300 frames, 30/0 fps")]
    [InlineData("18446744073709551616 frames, 30/1 fps")]
    [InlineData("300 frames, 18446744073709551616/1 fps")]
    [InlineData("300 frames, 30/18446744073709551616 fps")]
    [InlineData("9223372036854775807 frames, 1/1 fps")]
    public void 最後の書式が合うsourceの数値が不正なら既存Duration解析を使う(string invalid)
    {
        string log = "avs: source: 300 frames, 30/1 fps\n"
            + "vpy(vs4): source: " + invalid + "\n  Duration: 00:00:04.00, start: 0\n";
        Assert.Equal(TimeSpan.FromSeconds(4), InputProbe.ParseDuration(log));
        Assert.Null(InputProbe.ParseDuration("vpy(vs4): source: " + invalid));
    }

    [Theory]
    [InlineData("-300 frames, 30/1 fps")]
    [InlineData("300 frames, -30/1 fps")]
    [InlineData("300 frames, 30/-1 fps")]
    [InlineData("three frames, 30/1 fps")]
    [InlineData("300 frames, x/1 fps")]
    [InlineData("300 frames, 30/x fps")]
    [InlineData("300 frames, 29.97/1 fps")]
    [InlineData("300 frames, 30//1 fps")]
    [InlineData("broken")]
    public void 書式が合わないsource行は候補にしない(string unrelated)
    {
        string fallback = "  Duration: 00:00:04.00, start: 0\n";
        string invalid = "vpy(vs4): source: " + unrelated + "\n";
        Assert.Equal(TimeSpan.FromSeconds(10), InputProbe.ParseDuration(
            "avs: source: 300 frames, 30/1 fps\n" + invalid + fallback));
        Assert.Equal(TimeSpan.FromSeconds(4), InputProbe.ParseDuration(invalid + fallback));
    }

    [Fact]
    public void 後続するOpenCLのsourceログで正確な長さを失わない()
    {
        string log = "vpy(vs4): source: 4550 frames, 30000/1001 fps\n"
            + "building OpenCL source: size 123.\n  Duration: 00:01:00.00, start: 0\n";
        Assert.Equal(TimeSpan.FromTicks(1518183333), InputProbe.ParseDuration(log));
    }

    [Fact]
    public void sourceがない旧ログでは従来の入力節選択を変えない()
    {
        string log = "Input #0, mov, from 'video.mp4':\n  Duration: 00:00:10.50, start: 0\n"
            + "Input #1, wav, from 'audio.wav':\n  Duration: 01:00:00.00, start: 0\n";
        Assert.Equal(TimeSpan.FromSeconds(10.5), InputProbe.ParseDuration(log, "video.mp4"));
        Assert.Null(InputProbe.ParseDuration(log, "absent.mp4"));
    }
}

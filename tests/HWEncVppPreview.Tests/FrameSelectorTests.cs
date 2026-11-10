using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public class FrameSelectorTests
{
    private static List<FrameTiming> Cfr(int n, double fps = 30.0, double t0 = 0.0) =>
        Enumerable.Range(0, n).Select(i => new FrameTiming(t0 + i / fps, 1.0 / fps)).ToList();

    [Fact]
    public void 等間隔_ちょうどのフレーム()
    {
        // Mb=0.5 は 30fps の 15 番目
        Assert.Equal(15, FrameSelector.Select(Cfr(40), 0.5, 30.0));
    }

    [Fact]
    public void 等間隔_フレームの途中の時刻は直前のフレーム()
    {
        // 0.5 と 0.5333 の間 (0.51) → ただし許容幅 Xdur/2=0.0167 以内の 16 番目 (0.5333 <= 0.51+0.0167=0.5267?) は外れる
        Assert.Equal(15, FrameSelector.Select(Cfr(40), 0.51, 30.0));
    }

    [Fact]
    public void 許容幅の境界()
    {
        var f = new List<FrameTiming> { new(0.0, 0.1), new(0.1, 0.1), new(0.2, 0.1) };
        // 次のフレーム (0.1) は Mb=0.05 でちょうど許容幅 (0.05) → 含む
        Assert.Equal(1, FrameSelector.Select(f, 0.05, 10.0));
        // わずかに外 → 含まない
        Assert.Equal(0, FrameSelector.Select(f, 0.0499, 10.0));
    }

    [Fact]
    public void 誤差で少し前に出ているXtsを拾う()
    {
        var f = Cfr(10);
        // 15番目相当の時刻 (0.5) を計算誤差で 0.4999999 と見積もっても 15 番目になる
        Assert.Equal(15, FrameSelector.Select(Cfr(40), 0.5 - 1e-7, 30.0));
        Assert.Equal(0, FrameSelector.Select(f, 0.0, 30.0));
    }

    [Fact]
    public void decimateで間引かれたXts列()
    {
        // 30fps → 24fps 相当に間引き: 5 枚中 1 枚欠ける。欠けた位置の時刻は直前のフレームが表示中
        var xts = new double[] { 0.0, 1 / 30.0, 2 / 30.0, 3 / 30.0, /* 4 欠け */ 5 / 30.0, 6 / 30.0 };
        var f = xts.Select(t => new FrameTiming(t, 1 / 30.0)).ToList();
        // T = 4/30 (欠け位置) → 許容幅 1/60 のため、直前の 3/30 の方が近い。5/30 は 4/30+1/60 より後ろ
        Assert.Equal(3, FrameSelector.Select(f, 4 / 30.0, 30.0));
        Assert.Equal(4, FrameSelector.Select(f, 5 / 30.0, 30.0));
    }

    [Fact]
    public void 可変長のXdurはフレームごとに許容幅を変える()
    {
        // 長いフレーム (dur=1.0) は許容幅 0.5
        var f = new List<FrameTiming> { new(0.0, 1.0), new(1.0, 1.0) };
        Assert.Equal(1, FrameSelector.Select(f, 0.5, 1.0));
        Assert.Equal(0, FrameSelector.Select(f, 0.49, 1.0));
    }

    [Fact]
    public void Xdurが無ければfpsから許容幅()
    {
        var f = new List<FrameTiming> { new(0.0, null), new(0.1, null) };
        Assert.Equal(1, FrameSelector.Select(f, 0.05, 10.0));
        Assert.Equal(0, FrameSelector.Select(f, 0.04, 10.0));
        // fps も無ければ許容幅 0
        Assert.Equal(0, FrameSelector.Select(f, 0.0999, 0.0));
        Assert.Equal(1, FrameSelector.Select(f, 0.1, 0.0));
    }

    [Fact]
    public void Xtsなしはfpsからフレーム番号で選ぶ()
    {
        var f = Enumerable.Range(0, 40).Select(_ => new FrameTiming(null, null)).ToList();
        Assert.Equal(15, FrameSelector.Select(f, 0.5, 30.0));
        Assert.Equal(0, FrameSelector.Select(f, 0.0, 30.0));
        Assert.Equal(15, FrameSelector.Select(f, 0.51, 30.0));
        // Xts 版と同じ規則 (許容幅は半フレーム)
        Assert.Equal(16, FrameSelector.Select(f, 0.52, 30.0));
    }

    [Fact]
    public void Xtsなしで範囲外か_fpsなしは見つからない()
    {
        var f = Enumerable.Range(0, 10).Select(_ => new FrameTiming(null, null)).ToList();
        Assert.Equal(-1, FrameSelector.Select(f, 5.0, 30.0));
        Assert.Equal(-1, FrameSelector.Select(f, 0.1, 0.0));
    }

    [Fact]
    public void 該当なし()
    {
        Assert.Equal(-1, FrameSelector.Select(Array.Empty<FrameTiming>(), 1.0, 30.0));
        // 最初のフレームが目標より後ろ
        var f = new List<FrameTiming> { new(1.0, 0.033), new(1.033, 0.033) };
        Assert.Equal(-1, FrameSelector.Select(f, 0.5, 30.0));
    }

    [Fact]
    public void 一部のフレームだけXtsが無い場合はXtsのあるものから選ぶ()
    {
        var f = new List<FrameTiming> { new(0.0, 0.1), new(null, null), new(0.1, 0.1) };
        Assert.Equal(2, FrameSelector.Select(f, 0.1, 10.0));
    }

    [Fact]
    public void 後ろのマージンが足りなくても最後のフレームを返す()
    {
        // Mb までのフレームが無くてもフォールバックしない仕様 (呼び出し側が Xts+Xdur で判断する)
        Assert.Equal(9, FrameSelector.Select(Cfr(10), 5.0, 30.0));
    }
}

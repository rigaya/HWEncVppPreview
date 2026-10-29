using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class NumberNudgeTests
{
    [Theory]
    [InlineData("0.05", 1, false, "0.06")]
    [InlineData("0.09", 1, false, "0.10")]
    [InlineData("0.05", -1, true, "-0.05")]
    [InlineData("0", -1, false, "-1")]
    [InlineData("-1", 1, false, "0")]
    [InlineData("10", 1, true, "20")]
    [InlineData("1.000", -1, false, "0.999")]
    [InlineData("005", 1, false, "006")]
    [InlineData("99.99", 1, false, "100.00")]
    public void UsesLeastSignificantDigitAndPreservesPrecision(string number, int direction, bool coarse, string expected)
    {
        string text = "--vpp-nlmeans sigma=" + number + ",h=0.05";
        int caret = text.IndexOf(number, StringComparison.Ordinal) + number.Length;
        var result = NumberNudge.TryNudge(text, caret, direction, coarse);
        Assert.NotNull(result);
        Assert.Equal("--vpp-nlmeans sigma=" + expected + ",h=0.05", result.Text);
        Assert.Equal("--vpp-nlmeans sigma=".Length + expected.Length, result.Caret);
    }

    [Theory]
    [InlineData("1e-3")]
    [InlineData("2E+10")]
    [InlineData("foo123")]
    [InlineData("--vpp-filter2")]
    [InlineData("1.2.3")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("1234567890123456789012345678.12")]
    [InlineData("79228162514264337593543950335.0")]
    public void DoesNotModifyUnsupportedTokensOrOverflow(string text)
    {
        for (int i = 0; i <= text.Length; ++i) Assert.Null(NumberNudge.TryNudge(text, i, 1));
    }

    [Fact]
    public void FindsOnlyNumberUnderCaretAndKeepsCaretAlignedWhenLengthChanges()
    {
        Assert.Null(NumberNudge.TryNudge("a=1,b=2", 4, 1));
        Assert.Equal("a=1,b=1", NumberNudge.TryNudge("a=1,b=2", 6, -1)!.Text);
        var result = NumberNudge.TryNudge("x=9.99", 3, 1);
        Assert.Equal("x=10.00", result!.Text);
        Assert.Equal(4, result.Caret);
        Assert.Null(NumberNudge.TryNudge("", 0, 1));
        Assert.Null(NumberNudge.TryNudge("1", -1, 1));
        Assert.Null(NumberNudge.TryNudge("1", 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NumberNudge.TryNudge("1", 0, 0));
    }
}

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

    [Theory]
    [InlineData("0.99", 1, false, "0", "1", "1.00")]
    [InlineData("1.00", 1, true, "0", "1", "1.00")]
    [InlineData("-1.00", -1, true, "-1", "1", "-1.00")]
    [InlineData("-0.95", -1, true, "-1", "1", "-1.00")]
    [InlineData("009", 1, true, "0", "10", "010")]
    [InlineData("0.9", 1, false, null, "0.95", "0.95")]
    [InlineData("1.20", -1, false, "1.195", null, "1.195")]
    [InlineData("1", -1, false, "0", null, "0")]
    [InlineData("0", -1, false, null, "0", "-1")]
    [InlineData("1", 1, false, null, "1e0", "1")]
    [InlineData("0.000", -1, false, "1e-3", null, "0.001")]
    [InlineData("10", 1, false, "0", "5", "05")]
    [InlineData("-10", -1, false, "-5", "0", "-05")]
    [InlineData("0.00", 1, false, ".005", "+.015", "0.01")]
    [InlineData("1", 1, false, null, null, "2")]
    public void ClampsAtBoundsWithoutLosingRequiredPrecision(string number, int direction, bool coarse,
        string? minimum, string? maximum, string expected)
    {
        string text = "value=" + number + ",other=10";
        var result = NumberNudge.TryNudge(text, 6 + number.Length, direction, coarse, new(minimum, maximum));
        Assert.NotNull(result);
        Assert.Equal("value=" + expected + ",other=10", result.Text);
        Assert.Equal(6 + expected.Length, result.Caret);
    }

    [Theory]
    [InlineData("2", "1")]
    [InlineData("NaN", null)]
    [InlineData(null, "Infinity")]
    [InlineData("", null)]
    [InlineData("1e-29", null)]
    [InlineData("0.12345678901234567890123456789", null)]
    [InlineData(null, "79228162514264337593543950336")]
    [InlineData("1e2147483648", null)]
    public void DoesNotWriteApproximateOrMalformedBounds(string? minimum, string? maximum)
    {
        Assert.Null(NumberNudge.TryNudge("0.5", 3, 1, range: new(minimum, maximum)));
    }

    [Fact]
    public void CanStopAtDecimalLimitsEvenWhenTheStepWouldOverflow()
    {
        string maximum = decimal.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string minimum = decimal.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(maximum, NumberNudge.TryNudge(maximum, maximum.Length, 1, range: new(null, maximum))!.Text);
        Assert.Equal(minimum, NumberNudge.TryNudge(minimum, minimum.Length, -1, range: new(minimum, null))!.Text);
        string nearMaximum = (decimal.MaxValue - 1m).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(maximum, NumberNudge.TryNudge(nearMaximum, nearMaximum.Length, 1, true, new(null, maximum))!.Text);
        Assert.Equal("10", NumberNudge.TryNudge("0", 1, 1, true, new(minimum, maximum))!.Text);
    }

}

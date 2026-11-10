using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class FilterCompletionTests
{
    private static readonly HelpSchema Schema = new("""
VPP Options:
  [edge] Edge
   --vpp-zeta
     Sharpen.
  [denoise] Noise
   --vpp-alpha [<param1>=<value>][,<param2>=<value>][...]
     Clean the image.
    params
      sigma=<float> Strength. (default: 0.50, range: 0..1)
      count=<int> Count. (default: 2, range: 0..10)
      mode=<string> Mode. (default: fast)
        values: fast, slow
        More detail.
      enabled=<bool> Enable it. (default: off)
      dependent=<float> Follows input. (default: derived)
      shape=<int>x<int> Size. (default: derived)
      file=<path> Input file.
      mixed=<int|auto|off> Mixed. (default: auto)
        values: auto, off
      cadence=<int> Cadence. (default: 60)
        values: 60, 24
      dynamic=<string> From input. (default: derived)
        values: auto, off
  [color] Color
   --vpp-scalar <string> Scalar mode. (default: auto)
        values: auto, off
  [other] Other
   --vpp-number <int> Amount. (default: 3, range: 0..9)
Other Options:
   --vpp-legacy [<params>]
    old=<int> Old value (default 2)
""");

    private static (string Text, int Caret) Mark(string marked)
    {
        int caret = marked.IndexOf('|');
        Assert.True(caret >= 0);
        return (marked.Remove(caret, 1), caret);
    }
    private static CompletionContext Context(string marked)
    {
        var (text, caret) = Mark(marked);
        return Assert.IsType<CompletionContext>(FilterCompletion.GetContext(text, caret, Schema));
    }
    private static CompletionResult Apply(string marked, string item)
    {
        var (text, caret) = Mark(marked);
        var context = FilterCompletion.GetContext(text, caret, Schema)!;
        return FilterCompletion.Apply(text, context, Assert.Single(FilterCompletion.GetItems(context, CompletionOrder.Alphabetical), candidate => candidate.Text == item));
    }

    [Theory]
    [InlineData("|", "")]
    [InlineData("  |  ", "")]
    [InlineData("--vpp-|", "--vpp-")]
    [InlineData("--vpp-al|pha", "--vpp-al")]
    [InlineData("|--vpp-alpha", "")]
    [InlineData("--vpp-alpha|", "--vpp-alpha")]
    [InlineData("# comment\n --vpp-a|", "--vpp-a")]
    public void OptionCompletionUsesPrefixAndWholeToken(string marked, string prefix)
    {
        var context = Context(marked);
        Assert.Equal(CompletionKind.Option, context.Kind);
        Assert.Equal(prefix, context.Prefix);
        var items = FilterCompletion.GetItems(context, CompletionOrder.Alphabetical);
        Assert.All(items, item => Assert.StartsWith(prefix.ToLowerInvariant(), item.Text));
    }

    [Fact]
    public void OptionsSortByNameOrKnownCategoryAndKeepLegacyNames()
    {
        var context = Context("|");
        Assert.Equal(new[] { "--vpp-alpha", "--vpp-legacy", "--vpp-number", "--vpp-scalar", "--vpp-zeta" },
            FilterCompletion.GetItems(context, CompletionOrder.Alphabetical).Select(item => item.Text));
        Assert.Equal(new[] { "--vpp-alpha", "--vpp-zeta", "--vpp-scalar", "--vpp-number", "--vpp-legacy" },
            FilterCompletion.GetItems(context, CompletionOrder.Category).Select(item => item.Text));
        var alpha = FilterCompletion.GetItems(context, CompletionOrder.Category)[0];
        Assert.Equal("denoise", alpha.Category);
        Assert.Equal("Clean the image.", alpha.Description);
    }

    [Theory]
    [InlineData("--vpp-alpha |", "")]
    [InlineData("--vpp-alpha sigma=0.2,|", "")]
    [InlineData("--vpp-alpha sigma=0.2, mo|de=slow", "mo")]
    [InlineData("--vpp-alpha co|unt=3,mode=fast", "co")]
    [InlineData("--vpp-alpha count|=3,mode=fast", "count")]
    public void ParameterNamesWorkAtWhitespaceCommaAndInsideNames(string marked, string prefix)
    {
        var context = Context(marked);
        Assert.Equal(CompletionKind.ParameterName, context.Kind);
        Assert.Equal(prefix, context.Prefix);
        string replaced = Mark(marked).Text.Substring(context.ReplaceStart, context.ReplaceLength);
        Assert.DoesNotContain("=", replaced);
        Assert.All(FilterCompletion.GetItems(context, CompletionOrder.Alphabetical), item => Assert.StartsWith(prefix, item.Text));
    }

    [Fact]
    public void ExcludesParametersOnBothSidesExceptTheSlotBeingEdited()
    {
        var context = Context("--vpp-alpha sigma=0.2, mo|de=fast,COUNT=3,file=\"a,b=c.png\"");
        Assert.Equal(new[] { "mode" }, FilterCompletion.GetItems(context, CompletionOrder.Category).Select(item => item.Text));
        var available = FilterCompletion.GetItems(Context("--vpp-alpha sigma=0.2,COUNT=3,file=\"a,b=c.png\", |"), CompletionOrder.Alphabetical);
        Assert.DoesNotContain(available, item => item.Text is "sigma" or "count" or "file");
        Assert.Contains(available, item => item.Text == "mode");
    }

    [Theory]
    [InlineData("--vpp-alpha mode=|", "", 2)]
    [InlineData("--vpp-alpha mode=f|ast,count=3", "f", 1)]
    [InlineData("--vpp-alpha mode=slow|,count=3", "slow", 1)]
    [InlineData("--vpp-alpha mode=\"f|ast\",count=3", "f", 1)]
    public void ValuesUseOnlyThePrefixBeforeCaret(string marked, string prefix, int count)
    {
        var context = Context(marked);
        Assert.Equal(CompletionKind.ParameterValue, context.Kind);
        Assert.Equal("mode", context.Parameter!.Name);
        Assert.Equal(prefix, context.Prefix);
        Assert.Equal(count, FilterCompletion.GetItems(context, CompletionOrder.Alphabetical).Count);
    }

    [Fact]
    public void BoolValuesIncludeBothLiteralsAndIdentifyOffAsDefault()
    {
        var context = Context("--vpp-alpha enabled=|");
        var items = FilterCompletion.GetItems(context, CompletionOrder.Alphabetical);
        Assert.Equal(new[] { "true", "false" }, items.Select(item => item.Text));
        Assert.True(items.Single(item => item.Text == "false").IsDefault);
        Assert.False(items.Single(item => item.Text == "true").IsDefault);
        var mode = FilterCompletion.GetItems(Context("--vpp-alpha mode=|"), CompletionOrder.Alphabetical);
        Assert.True(mode.Single(item => item.Text == "fast").IsDefault);
        Assert.All(mode, item => Assert.Equal("Mode.", item.Description));
    }

    [Fact]
    public void ApplyOptionsAndParametersProducesFollowupWithoutChangingOtherSlots()
    {
        var option = Apply("--vpp-al|pha", "--vpp-alpha");
        Assert.Equal("--vpp-alpha ", option.Text);
        Assert.Equal(option.Text.Length, option.Caret);
        Assert.Equal(CompletionKind.ParameterName, option.Next!.Kind);
        var mode = Apply("--vpp-alpha sigma=0.2,mo|de=slow,count=3", "mode");
        Assert.Equal("--vpp-alpha sigma=0.2,mode=slow,count=3", mode.Text);
        Assert.Equal(CompletionKind.ParameterValue, mode.Next!.Kind);
        var value = FilterCompletion.Apply(mode.Text, mode.Next, FilterCompletion.GetItems(mode.Next, CompletionOrder.Alphabetical)[0]);
        Assert.Equal("--vpp-alpha sigma=0.2,mode=fast,count=3", value.Text);
        Assert.Null(value.Next);
        Assert.Equal(value.Text.IndexOf(",count", StringComparison.Ordinal), value.Caret);
        Assert.Equal("--vpp-alpha sigma=0.2", Apply("--vpp-a|lpha sigma=0.2", "--vpp-alpha").Text);
    }

    [Theory]
    [InlineData("sigma", "0.50")]
    [InlineData("count", "2")]
    [InlineData("enabled", "off")]
    [InlineData("dependent", "")]
    [InlineData("shape", "")]
    [InlineData("file", "")]
    [InlineData("old", "")]
    public void ApplyInsertsOnlySafeDefaults(string name, string expected)
    {
        string option = name == "old" ? "legacy" : "alpha";
        var result = Apply("--vpp-" + option + " |", name);
        Assert.Equal("--vpp-" + option + " " + name + "=" + expected, result.Text);
        Assert.Null(result.Next);
        Assert.Equal(result.Text.Length, result.Caret);
    }

    [Theory]
    [InlineData("mixed", "auto", true)]
    [InlineData("cadence", "60", true)]
    [InlineData("dynamic", "auto", false)]
    public void ChoicesTakePrecedenceOverNumericOrDerivedDefaults(string name, string candidate, bool isDefault)
    {
        var result = Apply("--vpp-alpha |", name);
        Assert.Equal("--vpp-alpha " + name + "=", result.Text);
        Assert.Equal(CompletionKind.ParameterValue, result.Next!.Kind);
        var item = FilterCompletion.GetItems(result.Next, CompletionOrder.Alphabetical).Single(item => item.Text == candidate);
        Assert.Equal(isDefault, item.IsDefault);
    }

    [Fact]
    public void AppliesValuesWithoutRemovingQuotesOrSuffix()
    {
        Assert.Equal("--vpp-alpha mode=\"fast\",count=3", Apply("--vpp-alpha mode=\"f|ast\",count=3", "fast").Text);
        Assert.Equal("--vpp-alpha mode=\"fast", Apply("--vpp-alpha mode=\"f|", "fast").Text);
        var context = Context("--vpp-alpha file=\"a\"\"b,c=d|.png\",mode=fast");
        Assert.Equal("file", context.Parameter!.Name);
        Assert.Empty(FilterCompletion.GetItems(context, CompletionOrder.Alphabetical));
    }

    [Fact]
    public void Windowsの末尾バックスラッシュで閉じ引用符を無効化しない()
    {
        var context = Context("--vpp-alpha file=\"C:\\folder\\\",mo|de=fast");
        Assert.Equal(CompletionKind.ParameterName, context.Kind);
        Assert.Equal("mo", context.Prefix);
        Assert.Equal("--vpp-alpha file=\"C:\\folder\\\",mode=fast", Apply("--vpp-alpha file=\"C:\\folder\\\",mo|de=fast", "mode").Text);
    }

    [Fact]
    public void ShortFormsExposeValuesAndTheirOwnSchema()
    {
        var applied = Apply("--vpp-sca|lar", "--vpp-scalar");
        Assert.Equal(CompletionKind.ParameterValue, applied.Next!.Kind);
        Assert.Equal(new[] { "auto", "off" }, FilterCompletion.GetItems(applied.Next, CompletionOrder.Category).Select(item => item.Text));
        Assert.Null(Apply("--vpp-num|ber", "--vpp-number").Next);
        var context = Context("--vpp-scalar a|uto");
        Assert.IsType<HelpOptionSchema>(context.Parameter);
        Assert.Equal("--vpp-scalar off", FilterCompletion.Apply("--vpp-scalar auto", context, new("off", null, "")).Text);
    }

    [Theory]
    [InlineData("--vpp-alpha sigma=|0.2,mode=fast", "0.2", "sigma")]
    [InlineData("--vpp-alpha sigma=0.2|,mode=fast", "0.2", "sigma")]
    [InlineData("--vpp-alpha file=\"a,b|=c.png\",mode=fast", "\"a,b=c.png\"", "file")]
    [InlineData("--vpp-scalar au|to", "auto", "--vpp-scalar")]
    [InlineData("--vpp-number |3", "3", "--vpp-number")]
    [InlineData("--vpp-legacy old=|2", "2", "old")]
    public void ValueAtReturnsWholeLexemeIncludingQuotes(string marked, string expected, string parameter)
    {
        var (text, caret) = Mark(marked);
        var span = Assert.IsType<ValueSpan>(FilterCompletion.ValueAt(text, caret, Schema));
        Assert.Equal(expected, text.Substring(span.Start, span.Length));
        Assert.Equal(parameter, span.Parameter.Name);
    }

    [Theory]
    [InlineData("# --vpp-alpha sigma=|")]
    [InlineData("--unknown sigma=|")]
    [InlineData("--vpp-alpha unknown=|")]
    [InlineData("--vpp-alpha sigma|=0.2")]
    [InlineData("--vpp-alpha sigma=0.2,|")]
    public void ValueAtIgnoresKeysUnknownSlotsAndEmptyValues(string marked)
    {
        var (text, caret) = Mark(marked);
        Assert.Null(FilterCompletion.ValueAt(text, caret, Schema));
    }

    [Fact]
    public void HandlesInvalidCaretsAndStaleContextWithoutEditing()
    {
        Assert.Null(FilterCompletion.GetContext("", -1, Schema));
        Assert.Null(FilterCompletion.GetContext("", 1, Schema));
        Assert.Null(FilterCompletion.ValueAt("", -1, Schema));
        Assert.Null(FilterCompletion.ValueAt("", 1, Schema));
        Assert.Null(FilterCompletion.GetContext("# comment", 5, Schema));
        Assert.Null(FilterCompletion.GetContext("--input ", 8, Schema));
        var stale = Context("--vpp-a|");
        var unchanged = FilterCompletion.Apply("changed", stale, new("--vpp-alpha", null, ""));
        Assert.Equal("changed", unchanged.Text);
        var bad = stale with { ReplaceStart = -1 };
        Assert.Equal("text", FilterCompletion.Apply("text", bad, new("x", null, "")).Text);
        Assert.Equal("インターレース解除", HelpCategories.DisplayName("deinterlace"));
        Assert.Equal("その他・設定", HelpCategories.DisplayName("other"));
        Assert.Equal("future", HelpCategories.DisplayName("future"));
        Assert.Equal("", HelpCategories.DisplayName(null));
    }

    [Fact]
    public void 引数全体の引用符でも名前と値の補完を行う()
    {
        var context = Context("--vpp-alpha \"file=C:\\My Videos\\,mo|de=fast\"");
        Assert.Equal(CompletionKind.ParameterName, context.Kind);
        Assert.Equal("mo", context.Prefix);
        Assert.Equal("--vpp-alpha \"file=C:\\My Videos\\,mode=fast\"", Apply("--vpp-alpha \"file=C:\\My Videos\\,mo|de=fast\"", "mode").Text);
        Assert.Equal("--vpp-alpha \"file=C:\\My Videos\\,mode=fast\"", Apply("--vpp-alpha \"file=C:\\My Videos\\,mode=f|ast\"", "fast").Text);
        var (text, caret) = Mark("--vpp-alpha \"file=C:\\My Videos\\,count=3|\"");
        var span = FilterCompletion.ValueAt(text, caret, Schema);
        Assert.NotNull(span);
        Assert.Equal("3", text.Substring(span.Start, span.Length));
    }

}

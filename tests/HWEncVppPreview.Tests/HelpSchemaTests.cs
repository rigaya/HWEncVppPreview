using System.Globalization;
using System.Text.RegularExpressions;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class HelpSchemaTests
{
    private static string Actual(string encoder) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "help", encoder + ".txt"));
    private static string Document(string body) => "VPP Options:\n\n  [denoise] Denoise\n" + body;

    [Fact]
    public void H1HelpRetainsEveryOptionAndParameterWithMetadata()
    {
        string help = Actual("qsvenc-h1");
        var schema = new HelpSchema(help);
        var index = new HelpIndex(help);
        Assert.Equal(index.OptionNames.Where(name => name.StartsWith("--vpp-", StringComparison.Ordinal)), schema.OptionNames);
        Assert.Equal(85, schema.Options.Count);
        Assert.Equal(671, schema.Options.Sum(option => option.Parameters.Count));
        Assert.All(schema.Options, option =>
        {
            Assert.False(option.IsNameOnly, option.Name);
            Assert.NotNull(option.Category);
            Assert.All(option.Parameters, parameter => Assert.False(parameter.IsNameOnly, option.Name + " " + parameter.Name));
        });
        Assert.Equal(10, schema.Options.Select(option => option.Category).Distinct().Count());
        Assert.Equal("off", schema.Lookup("--vpp-deband")!.Parameters.Single(parameter => parameter.Name == "blurfirst").DefaultValue);
        var algorithms = schema.Lookup("--vpp-resize")!.Parameters.Single(parameter => parameter.Name == "algo").Values;
        Assert.Equal(1, algorithms.Count(value => value == "bilinear"));
        // CLI自己診断の集計と一致させ、名前だけへの意図しない後退も検出する。
        var all = schema.Options.Cast<HelpValueSchema>().Concat(schema.Options.SelectMany(option => option.Parameters)).ToArray();
        Assert.Equal(75, all.Count(value => value.IsDerivedDefault));
        Assert.All(all.Where(value => value.IsDerivedDefault), value => Assert.Null(value.DefaultValue));
        Assert.Null(schema.Lookup("--missing"));
    }

    [Fact]
    public void H1HelpReturnsScalarOptionsAndStructuredValues()
    {
        var schema = new HelpSchema(Actual("qsvenc-h1"));
        var mctf = Assert.IsType<HelpOptionSchema>(schema.Lookup("--vpp-mctf"));
        Assert.Equal("int|auto", mctf.Type);
        Assert.Equal("auto", mctf.DefaultValue);
        Assert.Contains("auto", mctf.Values);
        Assert.Equal(new HelpValueRange("1", "20"), mctf.Range);
        var deinterlace = Assert.IsType<HelpOptionSchema>(schema.Lookup("--vpp-deinterlace"));
        Assert.Equal("deinterlace", deinterlace.Category);
        Assert.Equal("string", deinterlace.Type);
        Assert.Equal("none", deinterlace.DefaultValue);
        Assert.Contains("bob", deinterlace.Values);
        Assert.Equal("path", schema.Lookup("--vpp-delogo")!.Type);
        Assert.Equal("int", schema.Lookup("--vpp-mfx-insert-clcopy")!.Type);
        Assert.True(schema.Lookup("--vpp-pad")!.IsStructured);
        var matrix = schema.Lookup("--vpp-colorspace")!.Parameters.Single(value => value.Name == "matrix");
        Assert.True(matrix.IsStructured);
        Assert.Equal("<from>:<to>", matrix.Type);
        Assert.True(matrix.IsDerivedDefault);
        Assert.Contains("retain the input", matrix.Description);
    }

    [Theory]
    [InlineData("qsvenc")]
    [InlineData("nvenc")]
    [InlineData("nvenc-current")]
    [InlineData("vceenc")]
    public void LegacyHelpOnlyReturnsNamesWithoutGuessing(string encoder)
    {
        string help = Actual(encoder);
        var schema = new HelpSchema(help);
        Assert.Equal(new HelpIndex(help).OptionNames.Where(name => name.StartsWith("--vpp-", StringComparison.Ordinal)), schema.OptionNames);
        Assert.True(schema.Options.Count > 70);
        Assert.All(schema.Options, option =>
        {
            Assert.True(option.IsNameOnly);
            Assert.Null(option.Category);
            Assert.Null(option.Type);
            Assert.Empty(option.Description);
            Assert.All(option.Parameters, parameter =>
            {
                Assert.True(parameter.IsNameOnly);
                Assert.Null(parameter.Type);
                Assert.Null(parameter.DefaultValue);
                Assert.Null(parameter.Range);
                Assert.Empty(parameter.Values);
            });
        });
        Assert.Contains(schema.Lookup("--vpp-nlmeans")!.Parameters, parameter => parameter.Name == "sigma");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ReadsDescriptionsUnionsOpenRangesAndPaths(string newline)
    {
        string text = Document("""
   --vpp-example [<param1>=<value>][,<param2>=<value>][...]
     First description.
     Second description.
    params
      amount=<float|auto|off> Amount. (default: auto, range: -1.25..)
                            values: auto, off
                            Continued explanation.
      count=<int> Count. (default: 2, range: ..100)
      flag=<bool> Flag. (default: true)
      output=<path> Optional output file.
      dimensions=<int>x<int> Structured dimensions. (default: derived)
""");
        var option = new HelpSchema(text.Replace("\r\n", "\n").Replace("\n", newline)).Lookup("--vpp-example")!;
        Assert.Equal("First description.\nSecond description.", option.Description);
        Assert.False(option.IsNameOnly);
        var amount = option.Parameters[0];
        Assert.Equal("float|auto|off", amount.Type);
        Assert.Equal("auto", amount.DefaultValue);
        Assert.Equal(new[] { "auto", "off" }, amount.Values);
        Assert.Equal(new HelpValueRange("-1.25", null), amount.Range);
        Assert.Equal("Amount.\nContinued explanation.", amount.Description);
        Assert.Equal(new HelpValueRange(null, "100"), option.Parameters[1].Range);
        Assert.Equal("true", option.Parameters[2].DefaultValue);
        Assert.Null(option.Parameters[3].DefaultValue);
        Assert.False(option.Parameters[3].IsNameOnly);
        Assert.True(option.Parameters[4].IsStructured);
        Assert.Null(option.Parameters[4].DefaultValue);
    }

    [Theory]
    [InlineData("      number=<int> Number.")]
    [InlineData("      number=int Number. (default: 1)")]
    [InlineData("      number=<> Number. (default: 1)")]
    [InlineData("      number=<int> Number. (default: 1) trailing")]
    [InlineData("      number=<int> Number. (default: abc)")]
    [InlineData("      number=<float> Number. (default: 0, range: 9..2)")]
    [InlineData("      number=<float> Number. (default: 0, range: NaN..2)")]
    [InlineData("      number=<float> Number. (default: 0, range: ..)")]
    [InlineData("      number=<float> Number. (default: 0, range: 1-2)")]
    [InlineData("      number=<float> Number. (default: Infinity)")]
    [InlineData("      number=<int> Number. [default: 1]")]
    [InlineData("     number=<int> Number. (default: 1)")]
    [InlineData("      number=int Number. (default: 1)\n      number=<int> Number. (default: 1)\n      number=<int> Duplicate. (default: 2)")]
    public void MalformedParameterFallsBackLocally(string declaration)
    {
        var schema = new HelpSchema(Document("   --vpp-example [<param1>=<value>][,<param2>=<value>][...]\n    params\n" + declaration + "\n      valid=<int> Valid. (default: 3)\n"));
        var option = schema.Lookup("--vpp-example")!;
        Assert.True(option.Parameters.Single(value => value.Name == "number").IsNameOnly);
        Assert.Equal("3", option.Parameters.Single(value => value.Name == "valid").DefaultValue);
    }

    [Theory]
    [InlineData("      mode=<string> Mode. (default: slow)\n        values: fast, fast")]
    [InlineData("      mode=<string> Mode. (default: slow)\n        values: fast, ")]
    [InlineData("      mode=<string> Mode. (default: slow)\n        values: fast")]
    [InlineData("      mode=<int|auto> Mode. (default: 1)")]
    [InlineData("      mode=<string> Mode. (default: fast)\n        values: fast\n        values: fast")]
    public void InvalidChoicesDoNotEnableValueAssistance(string declaration)
    {
        var option = new HelpSchema(Document("   --vpp-example [<param1>=<value>][,<param2>=<value>][...]\n    params\n" + declaration)).Lookup("--vpp-example")!;
        var parameter = Assert.Single(option.Parameters);
        Assert.True(parameter.IsNameOnly);
        Assert.Empty(parameter.Values);
    }

    [Theory]
    [InlineData("int|float", "0.25", "")]
    [InlineData("auto|int", "2", "        values: auto")]
    public void UnionAcceptsEveryDeclaredBasicType(string type, string defaultValue, string values)
    {
        var option = new HelpSchema(Document($"   --vpp-example <{type}> Value. (default: {defaultValue})\n{values}\n")).Lookup("--vpp-example")!;
        Assert.False(option.IsNameOnly);
        Assert.False(option.IsStructured);
        Assert.Equal(defaultValue, option.DefaultValue);
    }

    [Fact]
    public void MissingUnknownAndDuplicateCategoriesDoNotBorrowMetadata()
    {
        string body = "   --vpp-example <int> Number. (default: 2)\n";
        foreach (string text in new[] { body, "VPP Options:\n" + body, "VPP Options:\n  [unknown] Unknown\n" + body,
            Document(body + body), "Other Options:\n  [denoise] Noise\n" + body })
            Assert.True(new HelpSchema(text).Lookup("--vpp-example")!.IsNameOnly);
        var schema = new HelpSchema(Document(body + "Other Options:\n   --vpp-next <int> Number. (default: 2)\n"));
        Assert.False(schema.Lookup("--vpp-example")!.IsNameOnly);
        Assert.True(schema.Lookup("--vpp-next")!.IsNameOnly);
        Assert.True(new HelpSchema("Other Options:\n" + body + Document(body)).Lookup("--vpp-example")!.IsNameOnly);
    }

    [Fact]
    public void DoesNotTreatExamplesOrFollowingCategoryAsParameters()
    {
        string text = Document("   --vpp-example [<param1>=<value>][,<param2>=<value>][...]\n    params\n      first=<int> First. (default: 2)\n                            example=<int> is explanatory text\n  [color] Color\n   --vpp-next <string> Next. (default: auto)\n                            values: auto, off\n");
        var schema = new HelpSchema(text);
        Assert.Single(schema.Lookup("--vpp-example")!.Parameters);
        Assert.DoesNotContain("Color", schema.Lookup("--vpp-example")!.Parameters[0].Description);
        Assert.Equal("color", schema.Lookup("--vpp-next")!.Category);
        Assert.Equal(new[] { "auto", "off" }, schema.Lookup("--vpp-next")!.Values);
    }

    [Fact]
    public void AcceptsEmptyAndArbitraryTextWithoutCultureDependentNumbers()
    {
        Assert.Empty(new HelpSchema("").Options);
        Assert.Empty(new HelpSchema("not help\n\0\uD800").Options);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var option = new HelpSchema(Document("   --vpp-value <float> Value. (default: 0.25, range: -0.5..1.5)\n")).Lookup("--vpp-value")!;
            Assert.Equal("0.25", option.DefaultValue);
            Assert.Equal(new HelpValueRange("-0.5", "1.5"), option.Range);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}

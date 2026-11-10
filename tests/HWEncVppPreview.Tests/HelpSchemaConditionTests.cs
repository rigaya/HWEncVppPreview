using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class HelpSchemaConditionTests
{
    private const string Heading = "VPP Options:\n  [resize] Resize\n   --vpp-example [<param1>=<value>][,<param2>=<value>][...]\n     Option description.\n    main: amount, algo\n    params\n";
    private const string Amount = "      amount=<float> Amount. (default: 0.5, range: 0..1)\n";
    private const string Algorithm = "      algo=<string> Algorithm. (default: none)\n        values: none, fsr1, nis, libplacebo-spline, libplacebo-lanczos\n";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ResolvesLaterParameterWithoutChangingExistingMetadata(string newline)
    {
        string help = Heading + Amount + "        when: algo=fsr1, nis\n" + Algorithm;
        var option = new HelpSchema(help.Replace("\n", newline)).Lookup("--vpp-example")!;
        var condition = Assert.IsType<HelpCondition>(option.Parameters[0].Condition);
        Assert.Equal("algo", condition.Parameter);
        Assert.Equal(HelpConditionKind.Equals, condition.Kind);
        Assert.Equal(new[] { "fsr1", "nis" }, condition.Values);
        AssertMetadata(option);
    }

    [Theory]
    [InlineData("algo!=none", HelpConditionKind.NotEquals, "none")]
    [InlineData("algo=libplacebo-*", HelpConditionKind.Equals, "libplacebo-*")]
    [InlineData("algo!=libplacebo-*", HelpConditionKind.NotEquals, "libplacebo-*")]
    public void ReadsInequalityAndMatchingPrefixes(string expression, HelpConditionKind kind, string value)
    {
        var option = Parse("        when: " + expression);
        var condition = Assert.IsType<HelpCondition>(option.Parameters[0].Condition);
        Assert.Equal(kind, condition.Kind);
        Assert.Equal(new[] { value }, condition.Values);
        AssertMetadata(option);
    }

    [Fact]
    public void PresenceCanReferencePathWithoutChoices()
    {
        var option = new HelpSchema(Heading + Amount + "        when: lut3d\n"
            + "      lut3d=<path> LUT path.\n" + Algorithm).Lookup("--vpp-example")!;
        var condition = Assert.IsType<HelpCondition>(option.Parameters[0].Condition);
        Assert.Equal("lut3d", condition.Parameter);
        Assert.Equal(HelpConditionKind.Present, condition.Kind);
        Assert.Empty(condition.Values);
        AssertMetadata(option);
    }

    [Theory]
    [InlineData("        when: unknown=fsr1")]
    [InlineData("        when: unknown")]
    [InlineData("        when: algo=unknown")]
    [InlineData("        when: algo=unknown*")]
    [InlineData("        when: algo=*")]
    [InlineData("        when: algo!=*")]
    [InlineData("        when: algo=lib*placebo")]
    [InlineData("        when: algo=libplacebo-**")]
    [InlineData("        when: algo=fsr1, unknown*")]
    [InlineData("        when: algo!=unknown")]
    [InlineData("        when: algo=")]
    [InlineData("        when: algo==fsr1")]
    [InlineData("        when: algo=fsr1,")]
    [InlineData("        when: algo=,fsr1")]
    [InlineData("        when: algo=fsr1 nis")]
    [InlineData("        when: algo=fsr1,nis=nis")]
    [InlineData("        when: algo=fsr1, nis!=none")]
    [InlineData("        when: ")]
    [InlineData("        when:")]
    [InlineData("        when:algo=fsr1")]
    [InlineData("when: algo=fsr1")]
    [InlineData("      when: algo=fsr1")]
    [InlineData("\twhen: algo=fsr1")]
    [InlineData("        when: algo=fsr1\n        when: algo=nis")]
    public void InvalidMetadataDisablesOnlyCondition(string metadata)
    {
        var option = Parse(metadata);
        Assert.Null(option.Parameters[0].Condition);
        AssertMetadata(option);
        Assert.DoesNotContain("when:", option.Description);
        Assert.All(option.Parameters, parameter => Assert.DoesNotContain("when:", parameter.Description));
    }

    [Theory]
    [InlineData("        values: fast, slow\n         when: algo=fsr1")]
    [InlineData("        when: algo=fsr1\n        values: fast, slow")]
    public void InvalidPlacementPreservesChoicesAndType(string metadata)
    {
        string help = Heading + "      amount=<string> Amount. (default: fast)\n" + metadata + "\n" + Algorithm;
        var parameter = new HelpSchema(help).Lookup("--vpp-example")!.Parameters[0];
        Assert.False(parameter.IsNameOnly);
        Assert.Equal("string", parameter.Type);
        Assert.Equal("fast", parameter.DefaultValue);
        Assert.Equal(new[] { "fast", "slow" }, parameter.Values);
        Assert.Equal("Amount.", parameter.Description);
        Assert.Null(parameter.Condition);
    }

    [Theory]
    [InlineData("        when: algo=fsr1\n        Continued explanation.")]
    [InlineData("        Continued explanation.\n        when: algo=fsr1\n        Final explanation.")]
    public void ExplanationAfterWhenInvalidatesOnlyCondition(string metadata)
    {
        var option = Parse(metadata);
        var amount = option.Parameters[0];
        Assert.Null(amount.Condition);
        Assert.False(amount.IsNameOnly);
        Assert.Equal("float", amount.Type);
        Assert.Equal("0.5", amount.DefaultValue);
        Assert.Equal(new HelpValueRange("0", "1"), amount.Range);
        Assert.Equal(metadata.Contains("Final explanation.", StringComparison.Ordinal)
            ? "Amount.\nContinued explanation.\nFinal explanation." : "Amount.\nContinued explanation.", amount.Description);
        Assert.Equal(new[] { "amount", "algo" }, option.MainParameters);
        Assert.Equal("none", option.Parameters[1].DefaultValue);
    }

    [Fact]
    public void ReadsWhenAfterChoicesAtSameIndentation()
    {
        string help = Heading + "      amount=<string> Amount. (default: fast)\n"
            + "                            values: fast, slow\n                            when: algo=fsr1\n" + Algorithm;
        var parameter = new HelpSchema(help).Lookup("--vpp-example")!.Parameters[0];
        Assert.NotNull(parameter.Condition);
        Assert.Equal(new[] { "fast", "slow" }, parameter.Values);
    }

    [Fact]
    public void EqualityRequiresReferencedChoices()
    {
        string help = Heading + Amount + "        when: lut3d=example.cube\n"
            + "      lut3d=<path> LUT path.\n" + Algorithm;
        Assert.Null(new HelpSchema(help).Lookup("--vpp-example")!.Parameters[0].Condition);
    }

    [Theory]
    [InlineData("<from>:<to>", true)]
    [InlineData("<int>x<int>", false)]
    [InlineData("<from>/<to>", false)]
    [InlineData("<from>:<to>:<extra>", false)]
    [InlineData("<int>:<int>", false)]
    public void FromToRequiresExactStructureAndRetainsChoices(string declaration, bool isFromTo)
    {
        string help = Heading + $"      matrix={declaration} Matrix. (default: derived)\n"
            + "        values: auto, bt709, bt2020nc\n" + Algorithm;
        var matrix = new HelpSchema(help).Lookup("--vpp-example")!.Parameters[0];
        Assert.False(matrix.IsNameOnly);
        Assert.True(matrix.IsStructured);
        Assert.Equal(isFromTo, matrix.IsFromTo);
        Assert.Equal(new[] { "auto", "bt709", "bt2020nc" }, matrix.Values);
        Assert.True(matrix.IsDerivedDefault);
    }

    [Theory]
    [InlineData("qsvenc-h1")]
    [InlineData("qsvenc")]
    [InlineData("nvenc-current")]
    [InlineData("vceenc")]
    public void OldHelpKeepsNullConditions(string encoder)
    {
        string help = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "help", encoder + ".txt"));
        Assert.All(new HelpSchema(help).Options, option =>
        {
            Assert.Null(option.Condition);
            Assert.All(option.Parameters, parameter => Assert.Null(parameter.Condition));
        });
    }

    [Fact]
    public void WhenOutsideParametersDoesNotPolluteOptionDescription()
    {
        string help = Heading.Replace("    main:", "when: algo=fsr1\n    main:", StringComparison.Ordinal) + Amount + Algorithm;
        var option = new HelpSchema(help).Lookup("--vpp-example")!;
        AssertMetadata(option);
        Assert.All(option.Parameters, parameter => Assert.Null(parameter.Condition));
    }

    private static HelpOptionSchema Parse(string metadata) => new HelpSchema(Heading + Amount + metadata + "\n" + Algorithm).Lookup("--vpp-example")!;

    private static void AssertMetadata(HelpOptionSchema option)
    {
        Assert.False(option.IsNameOnly);
        Assert.Equal("resize", option.Category);
        Assert.Equal("Option description.", option.Description);
        Assert.Equal(new[] { "amount", "algo" }, option.MainParameters);
        var amount = option.Parameters[0];
        Assert.False(amount.IsNameOnly);
        Assert.Equal("float", amount.Type);
        Assert.Equal("0.5", amount.DefaultValue);
        Assert.Equal(new HelpValueRange("0", "1"), amount.Range);
        Assert.Equal("Amount.", amount.Description);
        var algorithm = option.Parameters.Single(parameter => parameter.Name == "algo");
        Assert.False(algorithm.IsNameOnly);
        Assert.Equal("none", algorithm.DefaultValue);
        Assert.Equal(new[] { "none", "fsr1", "nis", "libplacebo-spline", "libplacebo-lanczos" }, algorithm.Values);
    }
}

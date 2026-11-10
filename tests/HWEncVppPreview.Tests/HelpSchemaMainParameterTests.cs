using HWEncVppPreview.Core;

namespace HWEncVppPreview.Tests;

public sealed class HelpSchemaMainParameterTests
{
    private const string Heading = "VPP Options:\n  [denoise] Denoise\n   --vpp-example [<param1>=<value>][,<param2>=<value>][...]\n     Option description.\n";
    private const string Parameters = "    params\n      amount=<float> Amount. (default: 0.5, range: 0..1)\n      24fps=<bool> Cadence. (default: false)\n      mode=<string> Mode. (default: fast)\n                            values: fast, slow\n";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void MainParametersPreserveDeclaredOrderWithoutChangingTypedMetadata(string newline)
    {
        string help = Heading + "    main: mode, 24fps, amount\n" + Parameters;
        var option = new HelpSchema(help.Replace("\n", newline)).Lookup("--vpp-example")!;
        Assert.Equal(new[] { "mode", "24fps", "amount" }, option.MainParameters);
        AssertMetadata(option);
    }

    [Theory]
    [InlineData("    main: unknown")]
    [InlineData("    main: mode, mode")]
    [InlineData("    main:")]
    [InlineData("    main: ")]
    [InlineData("    main:   ")]
    [InlineData("    main: mode,")]
    [InlineData("    main: ,mode")]
    [InlineData("    main: mode,, amount")]
    [InlineData("    main: Mode")]
    [InlineData("main: mode")]
    [InlineData("   main: mode")]
    [InlineData("     main: mode")]
    [InlineData("\tmain: mode")]
    [InlineData("    main: mode\n    main: amount")]
    [InlineData("    main: mode,\n    main: amount")]
    public void MalformedMainInvalidatesOnlyMainMetadata(string main)
    {
        var option = new HelpSchema(Heading + main + "\n" + Parameters).Lookup("--vpp-example")!;
        Assert.Empty(option.MainParameters);
        AssertMetadata(option);
    }

    [Theory]
    [InlineData("    main: mode")]
    [InlineData("                            main: mode")]
    public void MainAfterParamsDoesNotPolluteParameterDescription(string main)
    {
        string help = Heading + Parameters.Replace("      24fps", main + "\n      24fps", StringComparison.Ordinal);
        var option = new HelpSchema(help).Lookup("--vpp-example")!;
        Assert.Empty(option.MainParameters);
        AssertMetadata(option);
    }

    [Fact]
    public void SecondMainAfterParamsInvalidatesEarlierMain()
    {
        var option = new HelpSchema(Heading + "    main: mode\n" + Parameters + "    main: amount\n").Lookup("--vpp-example")!;
        Assert.Empty(option.MainParameters);
        AssertMetadata(option);
    }

    [Fact]
    public void MainMustOccupyOneLineImmediatelyBeforeParams()
    {
        var option = new HelpSchema(Heading + "    main: mode\n     Continued description.\n" + Parameters).Lookup("--vpp-example")!;
        Assert.Empty(option.MainParameters);
        Assert.Equal("Option description.\nContinued description.", option.Description);
        Assert.All(option.Parameters, parameter => Assert.False(parameter.IsNameOnly));
        var missing = new HelpSchema(Heading + "    main: mode\n").Lookup("--vpp-example")!;
        Assert.Empty(missing.MainParameters);
    }

    [Fact]
    public void MainFromFollowingOptionDoesNotLeakIntoPreviousOption()
    {
        var schema = new HelpSchema(Heading + Parameters + Heading + "    main: mode\n" + Parameters);
        // 同名見出しが重複したhelpは既存の安全なフォールバックを維持する。
        Assert.Empty(schema.Lookup("--vpp-example")!.MainParameters);
        schema = new HelpSchema(Heading + Parameters + Heading.Replace("--vpp-example", "--vpp-next", StringComparison.Ordinal)
            + "    main: mode\n" + Parameters);
        Assert.Empty(schema.Lookup("--vpp-example")!.MainParameters);
        Assert.Equal(new[] { "mode" }, schema.Lookup("--vpp-next")!.MainParameters);
    }

    [Theory]
    [InlineData("qsvenc-h1")]
    [InlineData("qsvenc")]
    [InlineData("nvenc-current")]
    [InlineData("vceenc")]
    public void OldHelpWithoutMainKeepsEmptyList(string encoder)
    {
        string help = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "help", encoder + ".txt"));
        Assert.All(new HelpSchema(help).Options, option => Assert.Empty(option.MainParameters));
    }

    private static void AssertMetadata(HelpOptionSchema option)
    {
        Assert.False(option.IsNameOnly);
        Assert.Equal("denoise", option.Category);
        Assert.Equal("Option description.", option.Description);
        Assert.Equal(new[] { "amount", "24fps", "mode" }, option.Parameters.Select(parameter => parameter.Name));
        Assert.All(option.Parameters, parameter => Assert.False(parameter.IsNameOnly));
        Assert.Equal("float", option.Parameters[0].Type);
        Assert.Equal("0.5", option.Parameters[0].DefaultValue);
        Assert.Equal(new HelpValueRange("0", "1"), option.Parameters[0].Range);
        Assert.Equal("Amount.", option.Parameters[0].Description);
        Assert.Equal("bool", option.Parameters[1].Type);
        Assert.Equal("false", option.Parameters[1].DefaultValue);
        Assert.Equal("Cadence.", option.Parameters[1].Description);
        Assert.Equal("string", option.Parameters[2].Type);
        Assert.Equal("fast", option.Parameters[2].DefaultValue);
        Assert.Equal(new[] { "fast", "slow" }, option.Parameters[2].Values);
        Assert.Equal("Mode.", option.Parameters[2].Description);
    }
}

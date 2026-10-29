using System.Text.RegularExpressions;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class HelpIndexTests
{
    [Theory]
    [InlineData("nvenc")]
    [InlineData("qsvenc")]
    [InlineData("vceenc")]
    public void ActualEncoderHelpContainsEveryVppOption(string encoder)
    {
        // 実出力は改行・行末空白だけを正規化して保存する。
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "help", encoder + ".txt"));
        var index = new HelpIndex(text);
        string[] options = Regex.Matches(text, @"--vpp-[a-z0-9-]+")
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(options.Length > 70);
        foreach (string option in options)
        {
            string section = index.Lookup(option);
            Assert.NotEmpty(section);
            Assert.Contains(option, section);
            Assert.Contains(option, index.OptionNames);
        }
        Assert.Contains("resiz", index.Lookup("--vpp-resize"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sigma", index.Lookup("--vpp-nlmeans"));
        Assert.DoesNotContain("--vpp-pmd", index.Lookup("--vpp-nlmeans"));
    }

    [Fact]
    public void HandlesAliasesUnindentedHeadingAndSectionBoundaries()
    {
        var index = new HelpIndex("title\n   --first, --alias <value>\n     first description\n--vpp-resize <value>\n     resize description\n   --last\n     last description\n");
        Assert.Equal(index.Lookup("--first"), index.Lookup("--alias"));
        Assert.DoesNotContain("resize description", index.Lookup("--first"));
        Assert.Contains("resize description", index.Lookup("--vpp-resize"));
        Assert.Contains("last description", index.Lookup("--last"));
        Assert.Equal("", index.Lookup("--unknown"));
        Assert.Empty(new HelpIndex("").OptionNames);
    }

    [Fact]
    public void DeeplyIndentedMentionsDoNotCreateSections()
    {
        var index = new HelpIndex("   --first\n      --not-a-heading is mentioned\n   --second\n");
        Assert.Equal("", index.Lookup("--not-a-heading"));
        Assert.Contains("--not-a-heading", index.Lookup("--first"));
    }
}

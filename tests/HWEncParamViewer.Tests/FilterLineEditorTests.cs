using System.Globalization;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.Tests;

public sealed class FilterLineEditorTests
{
    private static readonly HelpSchema Schema = new("""
VPP Options:
  [denoise] Noise
   --vpp-alpha [<param1>=<value>][,<param2>=<value>][...]
     Test filter.
    params
      sigma=<float> Strength. (default: 0.50, range: 0..1.000)
      count=<int> Count. (default: 2, range: 0..10)
      mode=<string> Mode. (default: fast)
        values: fast, slow
      file=<path> Input file.
      enabled=<bool> Enable it. (default: true)
  [color] Color
   --vpp-scalar <string> Scalar. (default: auto)
        values: auto, off
  [other] Other
   --vpp-flag
     Flag.
Other Options:
   --vpp-legacy [<params>]
    old=<int> Old value (default 2)
""");

    [Fact]
    public void パラメータを読むとスキーマと未知の値を残す()
    {
        var info = FilterLineEditor.Read("--vpp-alpha sigma=0.2,unknown=opaque,mode=fast", Schema);
        Assert.Equal("--vpp-alpha", info.OptionName);
        Assert.Same(Schema.Lookup("--vpp-alpha"), info.Option);
        Assert.Equal(FilterLineForm.Parameters, info.Form);
        Assert.Null(info.UnavailableReason);
        Assert.Equal("0.2", info.Values["sigma"]);
        Assert.Equal("opaque", info.Values["unknown"]);
        Assert.Equal(new[] { "unknown" }, info.Unknown);
        Assert.Equal("sigma=0.2, unknown=opaque, mode=fast", FilterLineEditor.Summary(info));
    }

    [Theory]
    [InlineData("--vpp-alpha", "sigma", "0.2", "--vpp-alpha sigma=0.2")]
    [InlineData("--vpp-alpha count=3", "sigma", "0.2", "--vpp-alpha count=3,sigma=0.2")]
    [InlineData("  --vpp-alpha\tcount=3  ", "sigma", "0.2", "  --vpp-alpha\tcount=3,sigma=0.2  ")]
    [InlineData("--vpp-alpha sigma=0.1,unknown=x,mode=\"fast\"", "sigma", "0.2", "--vpp-alpha sigma=0.2,unknown=x,mode=\"fast\"")]
    [InlineData("--vpp-alpha sigma=\"0.1\",count=3", "count", "4", "--vpp-alpha sigma=\"0.1\",count=4")]
    [InlineData("--vpp-alpha SIGMA=0.1,count=3", "sigma", "0.2", "--vpp-alpha SIGMA=0.2,count=3")]
    [InlineData("--vpp-alpha file=\"C:\\a b\\\",count=3", "count", "4", "--vpp-alpha file=\"C:\\a b\\\",count=4")]
    public void 対象だけを書き換え追加し他の文字は保持する(string text, string name, string value, string expected)
    {
        string changed = FilterLineEditor.SetValue(text, Schema, name, value);
        Assert.Equal(expected, changed);
        Assert.Equal(value, FilterLineEditor.Read(changed, Schema).Values[name]);
    }

    [Theory]
    [InlineData("--vpp-alpha sigma=1,count=3,mode=fast", "sigma", "--vpp-alpha count=3,mode=fast")]
    [InlineData("--vpp-alpha sigma=1,count=3,mode=fast", "count", "--vpp-alpha sigma=1,mode=fast")]
    [InlineData("--vpp-alpha sigma=1,count=3,mode=fast", "mode", "--vpp-alpha sigma=1,count=3")]
    [InlineData("  --vpp-alpha\t sigma=1  ", "sigma", "  --vpp-alpha")]
    [InlineData("--vpp-alpha unknown=\"a b\",sigma=1,mode=\"fast\"", "sigma", "--vpp-alpha unknown=\"a b\",mode=\"fast\"")]
    [InlineData("--vpp-alpha count=3", "sigma", "--vpp-alpha count=3")]
    public void 対象と隣の区切りだけを削除する(string text, string name, string expected)
    {
        Assert.Equal(expected, FilterLineEditor.SetValue(text, Schema, name, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\My Videos\\file.ass")]
    [InlineData("C:\\My Videos\\")]
    [InlineData("say\"hello\"")]
    [InlineData("say \"hello\" again")]
    [InlineData("value=part")]
    [InlineData("#not-comment")]
    public void 引用符を含む値は引数化しても往復する(string value)
    {
        string changed = FilterLineEditor.SetValue("--vpp-alpha count=3", Schema, "file", value);
        var read = FilterLineEditor.Read(changed, Schema);
        Assert.Equal(FilterLineForm.Parameters, read.Form);
        Assert.Equal(value, read.Values["file"]);
        Assert.Equal(new[] { "--vpp-alpha", "count=3,file=" + value }, OptionTextParser.TokenizeLine(changed));
    }

    [Theory]
    [InlineData("a,b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void エンコーダへ渡せないパラメータ値を書かない(string value)
    {
        const string text = "--vpp-alpha count=3";
        Assert.Equal(text, FilterLineEditor.SetValue(text, Schema, "file", value));
    }

    [Fact]
    public void 引用符内のカンマを黙って別の値に変換しない()
    {
        const string text = "--vpp-alpha file=\"a,b.ass\",count=3";
        var info = FilterLineEditor.Read(text, Schema);
        Assert.Equal(FilterLineForm.Unavailable, info.Form);
        Assert.Contains("カンマ", info.UnavailableReason);
        Assert.Equal(text, FilterLineEditor.SetValue(text, Schema, "count", "4"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# comment")]
    [InlineData("--vpp-missing count=1")]
    [InlineData("--vpp-legacy old=2")]
    [InlineData("--vpp-alpha count=1 extra")]
    [InlineData("--vpp-alpha count=1,count=2")]
    [InlineData("--vpp-alpha count")]
    [InlineData("--vpp-alpha =1")]
    [InlineData("--vpp-alpha count=1,")]
    [InlineData("--vpp-flag unexpected")]
    [InlineData("--vpp-alpha count=1\n--vpp-alpha count=2")]
    public void 書式外の行は例外を出さず原文を保つ(string text)
    {
        var info = FilterLineEditor.Read(text, Schema);
        Assert.Equal(FilterLineForm.Unavailable, info.Form);
        Assert.False(string.IsNullOrEmpty(info.UnavailableReason));
        Assert.Equal(text, FilterLineEditor.SetValue(text, Schema, "count", "4"));
        Assert.Equal(text, FilterLineEditor.SetShortValue(text, Schema, "off"));
    }

    [Fact]
    public void スキーマにない名前は新たに書かない()
    {
        const string text = "--vpp-alpha unknown=keep,count=3";
        Assert.Equal(text, FilterLineEditor.SetValue(text, Schema, "unknown", "replace"));
        Assert.Equal(text, FilterLineEditor.SetValue(text, Schema, "missing", "new"));
    }

    [Fact]
    public void 短い形式の読み書きと削除()
    {
        var info = FilterLineEditor.Read("--vpp-scalar", Schema);
        Assert.Equal(FilterLineForm.ShortValue, info.Form);
        Assert.Null(info.ShortValue);
        Assert.Equal("--vpp-scalar auto", FilterLineEditor.SetShortValue("--vpp-scalar", Schema, "auto"));
        Assert.Equal("  --vpp-scalar\t off  ", FilterLineEditor.SetShortValue("  --vpp-scalar\t auto  ", Schema, "off"));
        Assert.Equal("  --vpp-scalar", FilterLineEditor.SetShortValue("  --vpp-scalar\t auto  ", Schema, null));
        Assert.Equal("off", FilterLineEditor.Summary(FilterLineEditor.Read("--vpp-scalar off", Schema)));
        string quoted = FilterLineEditor.SetShortValue("--vpp-scalar", Schema, "say \"hello\"");
        Assert.Equal("say \"hello\"", FilterLineEditor.Read(quoted, Schema).ShortValue);
        Assert.Equal("--vpp-scalar", FilterLineEditor.SetValue("--vpp-scalar", Schema, "mode", "off"));
    }

    [Fact]
    public void 引数のないオプションはフォームを持たない()
    {
        var info = FilterLineEditor.Read("--vpp-flag", Schema);
        Assert.Equal(FilterLineForm.None, info.Form);
        Assert.Empty(info.Values);
        Assert.Equal("", FilterLineEditor.Summary(info));
        Assert.Equal("--vpp-flag", FilterLineEditor.SetValue("--vpp-flag", Schema, "count", "1"));
    }

    [Theory]
    [InlineData("2", "0", "10", 4, "4")]
    [InlineData("0.50", "0", "1.000", 0.125, "0.125")]
    [InlineData(null, null, null, 4, "4")]
    [InlineData("1e-3", "0", "1", 0.001, "0.001")]
    [InlineData("0.1", "-1.2345", null, 0.5, "0.5000")]
    [InlineData("1e2", "0", "1000", 123, "123")]
    public void 数値はスキーマの最大桁数で指数を使わず出力する(string? defaultValue, string? minimum, string? maximum, double value, string expected)
    {
        var parameter = new HelpValueSchema { Name = "test", DefaultValue = defaultValue, Range = new(minimum, maximum) };
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(expected, FilterLineEditor.FormatNumber(value, parameter));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void 引数全体を引用した行も同じ値として読み取れる()
    {
        const string text = "  --vpp-alpha\t\"file=C:\\My Videos\\say\"\"hello\"\",count=3\"  ";
        var info = FilterLineEditor.Read(text, Schema);
        Assert.Equal(FilterLineForm.Parameters, info.Form);
        Assert.Equal("C:\\My Videos\\say\"hello\"", info.Values["file"]);
        Assert.Equal("3", info.Values["count"]);
        Assert.Equal(new[] { "--vpp-alpha", "file=C:\\My Videos\\say\"hello\",count=3" }, OptionTextParser.TokenizeLine(text));
    }

    [Theory]
    [InlineData("  --vpp-alpha\t\"file=a b,count=3\"  ", "count", "4", "  --vpp-alpha\t\"file=a b,count=4\"  ", "file=a b,count=4")]
    [InlineData("--vpp-alpha \"file=a b,count=3\"", "mode", "fast", "--vpp-alpha \"file=a b,count=3,mode=fast\"", "file=a b,count=3,mode=fast")]
    [InlineData("--vpp-alpha \"file=a b,count=3\"", "file", "say \"hello\"", "--vpp-alpha \"file=say \"\"hello\"\",count=3\"", "file=say \"hello\",count=3")]
    [InlineData("--vpp-alpha \"file=a b,count=3,mode=fast\"", "file", null, "--vpp-alpha \"count=3,mode=fast\"", "count=3,mode=fast")]
    [InlineData("--vpp-alpha \"file=a b,count=3,mode=fast\"", "count", null, "--vpp-alpha \"file=a b,mode=fast\"", "file=a b,mode=fast")]
    [InlineData("--vpp-alpha \"file=a b,count=3,mode=fast\"", "mode", null, "--vpp-alpha \"file=a b,count=3\"", "file=a b,count=3")]
    [InlineData("--vpp-alpha \"count=3\"", "count", null, "--vpp-alpha", null)]
    [InlineData("--vpp-alpha \"\"", "count", "3", "--vpp-alpha \"count=3\"", "count=3")]
    public void 引数全体の引用符を保って編集し引数化した値も一致する(string text, string name, string? value, string expected, string? expectedArgument)
    {
        string changed = FilterLineEditor.SetValue(text, Schema, name, value);
        Assert.Equal(expected, changed);
        Assert.Equal(expectedArgument is null ? new[] { "--vpp-alpha" } : new[] { "--vpp-alpha", expectedArgument }, OptionTextParser.TokenizeLine(changed));
        var info = FilterLineEditor.Read(changed, Schema);
        Assert.Equal(FilterLineForm.Parameters, info.Form);
        if (value is not null) Assert.Equal(value, info.Values[name]);
        else Assert.DoesNotContain(name, info.Values.Keys);
    }

}

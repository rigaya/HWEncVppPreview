using System.Collections.Specialized;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class ParameterConditionTests
{
    private const string Help = """
VPP Options:
  [resize] Resize
   --vpp-resize [<param1>=<value>][,<param2>=<value>][...]
     Resize image.
    main: algo, sharpness
    params
      algo=<string> Algorithm. (default: bicubic)
        values: bicubic, fsr1, nis, dpid, libplacebo-spline
      sharpness=<float> Sharpness. (default: 0.5)
        when: algo=fsr1, nis
      b=<float> Cubic coefficient. (default: 0.0)
        when: algo=bicubic
      pl-radius=<float> Radius. (default: 0.0)
        when: algo=libplacebo-*
  [color] Color
   --vpp-colorspace [<param1>=<value>][,<param2>=<value>][...]
     Convert color.
    params
      hdr2sdr=<string> Tonemap. (default: none)
        values: none, hable, mobius
      peak=<float> Peak. (default: 1000)
        when: hdr2sdr!=none
      lut3d=<path> LUT file.
      interp=<string> Interpolation. (default: tetrahedral)
        values: nearest, tetrahedral
        when: lut3d
      matrix=<from>:<to> Matrix. (default: derived)
        values: bt709, bt2020nc
      range=<from>:<to> Range. (default: limited:full)
        values: limited, full
      shape=<int>x<int> Size. (default: derived)
""";
    private static FilterLineViewModel Line(string text = "--vpp-resize", string? help = null) => new()
    {
        Text = text, HelpSchema = new HelpSchema(help ?? Help),
    };
    private static FilterParameterViewModel Parameter(FilterLineViewModel line, string name) => line.Parameters.Single(item => item.Name == name);
    private static string[] Names(IEnumerable<FilterParameterViewModel> items) => items.Select(item => item.Name).ToArray();

    [Fact]
    public void DefaultEffectiveValueControlsBothGroups()
    {
        var line = Line();
        Assert.Equal(new[] { "algo" }, Names(line.MainParameters));
        Assert.Equal(new[] { "b" }, Names(line.OtherParameters));
        Assert.Equal(1, line.OtherParameterCount);
        Assert.Equal(4, line.Parameters.Count);
        Assert.All(line.Parameters, item => Assert.False(item.IsInactive));
        line.Text = "--vpp-resize algo=nis";
        Assert.Equal(new[] { "algo", "sharpness" }, Names(line.MainParameters));
        Assert.Empty(line.OtherParameters);
        line.Text = "--vpp-resize algo=libplacebo-spline";
        Assert.Equal(new[] { "pl-radius" }, Names(line.OtherParameters));
    }

    [Fact]
    public void ExplicitInactiveValueIsPreservedUntilReset()
    {
        var line = Line("--vpp-resize algo=fsr1,b=0.3");
        var b = Parameter(line, "b");
        Assert.True(b.IsInactive);
        Assert.Contains(b, line.MainParameters);
        Assert.Equal("0.3", b.ValueText);
        line.Text = "--vpp-resize b=0.3";
        Assert.False(b.IsInactive);
        Assert.Contains(b, line.MainParameters);
        line.Text = "--vpp-resize algo=nis,b=0.3";
        Assert.True(b.IsInactive);
        b.ResetCommand.Execute(null);
        Assert.DoesNotContain(b, line.MainParameters);
        Assert.DoesNotContain(b, line.OtherParameters);
        Assert.False(b.IsInactive);
        Assert.DoesNotContain("b=", line.Text);
    }

    [Fact]
    public void ChangesReuseInstancesAndDoNotResetCollections()
    {
        var line = Line();
        var original = line.Parameters.ToArray();
        var sharpness = Parameter(line, "sharpness");
        int inactiveNotifications = 0, countNotifications = 0;
        sharpness.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(sharpness.IsInactive)) ++inactiveNotifications; };
        line.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(line.OtherParameterCount)) ++countNotifications; };
        line.Parameters.CollectionChanged += (_, _) => Assert.Fail("条件変更で項目を作り直しました");
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            Assert.NotEqual(NotifyCollectionChangedAction.Reset, args.Action);
            Assert.Empty(line.MainParameters.Intersect(line.OtherParameters));
        }
        line.MainParameters.CollectionChanged += Changed;
        line.OtherParameters.CollectionChanged += Changed;
        Parameter(line, "algo").ChoiceValue = "fsr1";
        Assert.Same(sharpness, line.MainParameters.Last());
        sharpness.ValueText = "0.7";
        Parameter(line, "algo").ResetCommand.Execute(null);
        Assert.True(sharpness.IsInactive);
        Parameter(line, "algo").ChoiceValue = "nis";
        Assert.False(sharpness.IsInactive);
        Assert.Equal(2, inactiveNotifications);
        Assert.Equal(3, countNotifications);
        for (int index = 0; index < original.Length; ++index) Assert.Same(original[index], line.Parameters[index]);
    }

    [Theory]
    [InlineData("--vpp-colorspace", false, false)]
    [InlineData("--vpp-colorspace hdr2sdr=hable", true, false)]
    [InlineData("--vpp-colorspace hdr2sdr=mobius,lut3d=sample.cube", true, true)]
    [InlineData("--vpp-colorspace lut3d=\"a b.cube\"", false, true)]
    public void InequalityAndPresenceUseWrittenOrDefaultValues(string text, bool peakVisible, bool interpVisible)
    {
        var line = Line(text);
        Assert.Equal(peakVisible, line.MainParameters.Contains(Parameter(line, "peak")));
        Assert.Equal(interpVisible, line.MainParameters.Contains(Parameter(line, "interp")));
    }

    [Fact]
    public void PresenceIsExplicitAndRemovalHidesUnwrittenDependents()
    {
        var line = Line("--vpp-colorspace lut3d=sample.cube,interp=nearest");
        var interp = Parameter(line, "interp");
        Parameter(line, "lut3d").ResetCommand.Execute(null);
        Assert.Contains(interp, line.MainParameters);
        Assert.True(interp.IsInactive);
        interp.ResetCommand.Execute(null);
        Assert.DoesNotContain(interp, line.MainParameters);
        Assert.False(interp.IsInactive);
        var withDefault = Line(help: Help.Replace("lut3d=<path> LUT file.", "lut3d=<path> LUT file. (default: sample.cube)"), text: "--vpp-colorspace");
        Assert.DoesNotContain(Parameter(withDefault, "interp"), withDefault.MainParameters);
    }

    [Fact]
    public void MissingDefaultsDoNotGuessConditionsAndOldMetadataStaysVisible()
    {
        var unknown = Line(help: Help.Replace("(default: bicubic)", "(default: derived)"));
        Assert.Empty(unknown.OtherParameters);
        Assert.Equal(new[] { "algo" }, Names(unknown.MainParameters));
        var legacy = Line(help: Help.Replace("when: algo=fsr1, nis", "when: missing=value"));
        Assert.Contains(Parameter(legacy, "sharpness"), legacy.MainParameters);
    }

    [Fact]
    public void FromToWaitsForBothSidesAndWritesOnceWithoutGuessingAuto()
    {
        var line = Line("--vpp-colorspace");
        var matrix = Parameter(line, "matrix");
        Assert.Equal(FilterParameterKind.FromTo, matrix.Kind);
        Assert.Equal(new[] { "bt709", "bt2020nc" }, matrix.FromChoices);
        Assert.Equal(matrix.FromChoices, matrix.ToChoices);
        Assert.Null(matrix.FromValue);
        Assert.Null(matrix.ToValue);
        int changes = 0;
        line.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(line.Text)) ++changes; };
        matrix.FromValue = "bt709";
        Assert.Equal(0, changes);
        Assert.False(matrix.IsSet);
        Assert.Equal("bt709", matrix.FromValue);
        Assert.Null(matrix.ToValue);
        matrix.ToValue = "bt2020nc";
        Assert.Equal(1, changes);
        Assert.Equal("--vpp-colorspace matrix=bt709:bt2020nc", line.Text);
        matrix.FromValue = "bt709";
        matrix.ToValue = null;
        matrix.FromValue = "unsupported";
        Assert.Equal(1, changes);
        matrix.FromValue = "bt2020nc";
        Assert.Equal(2, changes);
        Assert.Equal("bt2020nc:bt2020nc", matrix.ValueText);
    }

    [Fact]
    public void FromToCanStartWithDestinationAndResetPendingOrWrittenPair()
    {
        var line = Line("--vpp-colorspace");
        var matrix = Parameter(line, "matrix");
        matrix.ToValue = "bt2020nc";
        matrix.ResetCommand.Execute(null);
        Assert.Null(matrix.ToValue);
        matrix.ToValue = "bt709";
        matrix.FromValue = "bt2020nc";
        Assert.Equal("bt2020nc:bt709", matrix.ValueText);
        matrix.ResetCommand.Execute(null);
        Assert.Null(matrix.ValueText);
        Assert.Null(matrix.FromValue);
        Assert.Null(matrix.ToValue);
        Assert.Equal("--vpp-colorspace", line.Text);
    }

    [Fact]
    public void FromToFollowsTextAndKeepsPendingPairAcrossUnrelatedEdit()
    {
        var line = Line("--vpp-colorspace");
        var matrix = Parameter(line, "matrix");
        matrix.FromValue = "bt709";
        Parameter(line, "hdr2sdr").ChoiceValue = "hable";
        Assert.Equal("bt709", matrix.FromValue);
        matrix.ToValue = "bt2020nc";
        Assert.Contains("hdr2sdr=hable", line.Text);
        Assert.Contains("matrix=bt709:bt2020nc", line.Text);
        line.Text = "--vpp-colorspace matrix=bt2020nc:bt709";
        Assert.Equal("bt2020nc", matrix.FromValue);
        Assert.Equal("bt709", matrix.ToValue);
        Assert.Same(matrix, Parameter(line, "matrix"));
        line.Text = "--vpp-colorspace";
        Assert.Null(matrix.FromValue);
        Assert.Null(matrix.ToValue);
        Assert.Equal(FilterParameterKind.Text, Parameter(line, "shape").Kind);
    }

    [Fact]
    public void FromToDefaultsAndDuplicateAssignmentsKeepTextContract()
    {
        var line = Line("--vpp-colorspace");
        var range = Parameter(line, "range");
        Assert.Equal("limited", range.FromValue);
        Assert.Equal("full", range.ToValue);
        range.FromValue = "full";
        Assert.Equal("full:full", range.ValueText);
        range.ResetCommand.Execute(null);
        Assert.Equal("limited", range.FromValue);
        Assert.Equal("full", range.ToValue);
        line.Text = "--vpp-colorspace matrix=bt709:bt2020nc,matrix=bt2020nc:bt709";
        Assert.False(line.IsFormAvailable);
        string before = line.Text;
        Parameter(line, "matrix").FromValue = "bt709";
        Assert.Equal(before, line.Text);
    }
}

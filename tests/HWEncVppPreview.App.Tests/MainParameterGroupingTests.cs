using System.Collections.Specialized;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class MainParameterGroupingTests
{
    private const string Help = """
VPP Options:
  [denoise] Noise
   --vpp-alpha [<param1>=<value>][,<param2>=<value>][...]
     Filter description.
    main: third, first
    params
      first=<float> First parameter. (default: 1.0)
      second=<int> Second parameter. (default: 2)
      third=<bool> Third parameter. (default: false)
      last=<string> Last parameter. (default: off)
        values: off, on
   --vpp-scalar <int> Scalar value. (default: 1)
""";

    private static FilterLineViewModel Line(string text = "--vpp-alpha") => new()
    {
        Text = text, HelpSchema = new HelpSchema(Help),
    };
    private static string[] Names(IEnumerable<FilterParameterViewModel> items) => items.Select(item => item.Name).ToArray();

    [Fact]
    public void MainOrderPrecedesWrittenOthersInDeclarationOrder()
    {
        var line = Line("--vpp-alpha last=on,second=2");
        Assert.Equal(new[] { "third", "first", "second", "last" }, Names(line.MainParameters));
        Assert.Empty(line.OtherParameters);
        Assert.Equal(0, line.OtherParameterCount);
        Assert.Equal(new[] { "first", "second", "third", "last" }, Names(line.Parameters));
    }

    [Fact]
    public void DefaultsStayCollapsedUntilTheyAreWritten()
    {
        var line = Line();
        Assert.Equal(new[] { "third", "first" }, Names(line.MainParameters));
        Assert.Equal(new[] { "second", "last" }, Names(line.OtherParameters));
        Assert.Equal(2, line.OtherParameterCount);
        Assert.All(line.Parameters, item => Assert.False(item.IsSet));
        line.Text = "--vpp-alpha second=2";
        Assert.Equal(new[] { "third", "first", "second" }, Names(line.MainParameters));
        Assert.Equal(new[] { "last" }, Names(line.OtherParameters));
    }

    [Fact]
    public void TextEditsAndResetMoveTheSameInstancesWithoutCollectionReset()
    {
        var line = Line();
        var mainCollection = line.MainParameters;
        var otherCollection = line.OtherParameters;
        var original = line.Parameters.ToArray();
        var second = original.Single(item => item.Name == "second");
        int countNotifications = 0, groupChanges = 0;
        line.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(line.OtherParameterCount)) ++countNotifications; };
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            Assert.NotEqual(NotifyCollectionChangedAction.Reset, args.Action);
            Assert.Empty(line.MainParameters.Intersect(line.OtherParameters));
            ++groupChanges;
        }
        line.MainParameters.CollectionChanged += Changed;
        line.OtherParameters.CollectionChanged += Changed;
        line.Parameters.CollectionChanged += (_, _) => Assert.Fail("テキスト入力で編集項目を作り直しました");
        line.Text = "--vpp-alpha second=3";
        Assert.Same(second, line.MainParameters.Last());
        Assert.Equal(1, line.OtherParameterCount);
        int afterPromotion = groupChanges;
        line.Text = "--vpp-alpha second=34";
        Assert.Equal(afterPromotion, groupChanges);
        second.ResetCommand.Execute(null);
        Assert.Same(second, line.OtherParameters.First());
        Assert.Equal(2, line.OtherParameterCount);
        Assert.Equal(2, countNotifications);
        Assert.Same(mainCollection, line.MainParameters);
        Assert.Same(otherCollection, line.OtherParameters);
        for (int i = 0; i < original.Length; ++i) Assert.Same(original[i], line.Parameters[i]);
    }

    [Fact]
    public void EditingACollapsedFieldPromotesItAndRemovingItRestoresItsPosition()
    {
        var line = Line();
        var last = line.OtherParameters.Single(item => item.Name == "last");
        last.ChoiceValue = "on";
        Assert.Same(last, line.MainParameters.Last());
        Assert.Contains("last=on", line.Text);
        last.ResetCommand.Execute(null);
        Assert.Equal(new[] { "second", "last" }, Names(line.OtherParameters));
        Assert.Same(last, line.OtherParameters.Last());
        Assert.DoesNotContain("last=", line.Text);
    }

    [Fact]
    public void MissingMainKeepsEveryParameterVisible()
    {
        var line = new FilterLineViewModel { Text = "--vpp-alpha", HelpSchema = new HelpSchema(Help.Replace("    main: third, first", "")) };
        Assert.Equal(Names(line.Parameters), Names(line.MainParameters));
        Assert.Empty(line.OtherParameters);
        Assert.Equal(0, line.OtherParameterCount);
        Assert.All(line.Parameters, item => Assert.Contains(item, line.MainParameters));
    }

    [Fact]
    public void AChangedOptionOrSchemaDoesNotKeepStaleGroups()
    {
        var line = Line();
        line.Text = "--vpp-scalar 2";
        Assert.Single(line.MainParameters);
        Assert.Same(line.Parameters[0], line.MainParameters[0]);
        Assert.Empty(line.OtherParameters);
        line.Text = "--vpp-missing";
        Assert.Empty(line.MainParameters);
        Assert.Empty(line.OtherParameters);
        line.Text = "--vpp-alpha";
        line.HelpSchema = new HelpSchema(Help.Replace("main: third, first", "main: last, second"));
        Assert.Equal(new[] { "last", "second" }, Names(line.MainParameters));
        Assert.Equal(new[] { "first", "third" }, Names(line.OtherParameters));
        line.HelpSchema = null;
        Assert.Empty(line.MainParameters);
        Assert.Empty(line.OtherParameters);
        Assert.Equal(0, line.OtherParameterCount);
    }
}

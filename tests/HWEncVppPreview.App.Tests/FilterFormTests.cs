using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.Services;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;
using HWEncVppPreview.Tests.Testing;

namespace HWEncVppPreview.App.Tests;

public sealed class FilterFormTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(20);
    private const string Help = """
VPP Options:
  [denoise] Noise
   --vpp-alpha [<param1>=<value>][,<param2>=<value>][...]
     Clean the image.
    params
      sigma=<float> Strength. (default: 0.50, range: 0..1)
      count=<int> Count. (default: 2)
      mode=<string> Mode. (default: fast)
        values: fast, slow
      enabled=<bool> Enable it. (default: off)
      dependent=<float> Follows input. (default: derived)
      shape=<int>x<int> Size. (default: derived)
      file=<path> Input file.
      mixed=<int|auto|off> Mixed. (default: auto)
        values: auto, off
      low=<int> Lower bound. (default: 3, range: 0..)
  [color] Color
   --vpp-scalar <string> Scalar mode. (default: auto)
        values: auto, off
   --vpp-empty
     No parameters.
Other Options:
   --vpp-legacy [<params>]
    old=<int> Old value (default 2)
""";
    private static readonly HelpSchema Schema = new(Help);

    [Fact]
    public void SchemaBuildsAllKindsAndUnspecifiedDefaults()
    {
        var line = new FilterLineViewModel { Text = "--vpp-alpha", HelpSchema = Schema };
        Assert.True(line.IsFormAvailable);
        Assert.Equal("alpha", line.DisplayName);
        Assert.Equal("ノイズ除去", line.CategoryName);
        Assert.Empty(line.Summary);
        Assert.Equal(9, line.Parameters.Count);
        Assert.All(line.Parameters, parameter => Assert.False(parameter.IsSet));
        var sigma = Parameter(line, "sigma");
        Assert.Equal(FilterParameterKind.Slider, sigma.Kind);
        Assert.Equal(0, sigma.Minimum);
        Assert.Equal(1, sigma.Maximum);
        Assert.Equal(0.01, sigma.Step, 8);
        Assert.Equal(0.5, sigma.NumberValue);
        Assert.Equal("0.50", sigma.DefaultText);
        Assert.Equal(FilterParameterKind.Number, Parameter(line, "count").Kind);
        Assert.Equal(FilterParameterKind.Number, Parameter(line, "low").Kind);
        Assert.Equal(FilterParameterKind.Choice, Parameter(line, "mode").Kind);
        Assert.Equal(new[] { "fast", "slow" }, Parameter(line, "mode").Choices);
        Assert.Equal("fast", Parameter(line, "mode").ChoiceValue);
        Assert.Equal(FilterParameterKind.Bool, Parameter(line, "enabled").Kind);
        Assert.False(Parameter(line, "enabled").BoolValue);
        Assert.Equal(FilterParameterKind.NumberOrChoice, Parameter(line, "mixed").Kind);
        Assert.Equal(FilterParameterKind.Text, Parameter(line, "dependent").Kind);
        Assert.Equal("自動", Parameter(line, "dependent").DefaultText);
        Assert.Null(Parameter(line, "dependent").ValueText);
        Assert.Equal(FilterParameterKind.Text, Parameter(line, "shape").Kind);
        Assert.Equal(FilterParameterKind.Path, Parameter(line, "file").Kind);
    }

    [Fact]
    public void TextRefreshKeepsParameterInstancesAndDoesNotWriteBack()
    {
        var line = new FilterLineViewModel { Text = "--vpp-alpha sigma=0.25", HelpSchema = Schema };
        var original = line.Parameters.ToArray();
        int textChanges = 0, collectionChanges = 0;
        line.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(line.Text)) ++textChanges; };
        line.Parameters.CollectionChanged += (_, _) => ++collectionChanges;
        line.Text = "--vpp-alpha sigma=0.75,mode=slow,unknown=x";
        Assert.Equal(1, textChanges);
        Assert.Equal(0, collectionChanges);
        Assert.Equal(original, line.Parameters);
        Assert.Equal("0.75", Parameter(line, "sigma").ValueText);
        Assert.Equal("slow", Parameter(line, "mode").ChoiceValue);
        Assert.True(Parameter(line, "sigma").IsSet);
        Assert.Contains("unknown=x", line.Summary);
        line.Text = "--vpp-scalar off";
        Assert.Single(line.Parameters);
        Assert.NotSame(original[0], line.Parameters[0]);
        line.Parameters[0].ChoiceValue = "auto";
        Assert.Equal("--vpp-scalar auto", line.Text);
        line.Parameters[0].ResetCommand.Execute(null);
        Assert.Equal("--vpp-scalar", line.Text);
    }

    [Fact]
    public void EditingFieldsPreservesOtherTextAndResetRemovesOnlyItsAssignment()
    {
        var line = new FilterLineViewModel { Text = "--vpp-alpha sigma=0.50,unknown=keep", HelpSchema = Schema };
        var sigma = Parameter(line, "sigma");
        sigma.NumberValue = 0.75;
        Assert.Equal("--vpp-alpha sigma=0.75,unknown=keep", line.Text);
        Assert.True(sigma.IsSet);
        sigma.ResetCommand.Execute(null);
        Assert.Equal("--vpp-alpha unknown=keep", line.Text);
        Assert.False(sigma.IsSet);
        Assert.Equal(0.5, sigma.NumberValue);
        Parameter(line, "enabled").BoolValue = true;
        Assert.Contains("enabled=true", line.Text);
        Parameter(line, "mode").ChoiceValue = "slow";
        Assert.Contains("mode=slow", line.Text);
        Parameter(line, "file").ValueText = "some file.ass";
        Assert.Equal("some file.ass", Parameter(line, "file").ValueText);
    }

    [Fact]
    public void NudgeUsesCurrentPrecisionAndStopsAtTheRange()
    {
        var line = new FilterLineViewModel { Text = "--vpp-alpha sigma=0.99", HelpSchema = Schema };
        var sigma = Parameter(line, "sigma");
        sigma.Nudge(1);
        Assert.Equal("1.00", sigma.ValueText);
        sigma.Nudge(1);
        Assert.Equal("1.00", sigma.ValueText);
        sigma.Nudge(-1, true);
        Assert.Equal("0.90", sigma.ValueText);
        sigma.NumberValue = -100;
        Assert.Equal("0.00", sigma.ValueText);
        sigma.NumberValue = double.NaN;
        Assert.Equal("0.00", sigma.ValueText);
    }

    [Fact]
    public void MissingOrLegacySchemaKeepsTextEditableAndAChangedSchemaRebuildsFields()
    {
        var line = new FilterLineViewModel { Text = "--vpp-alpha sigma=0.2" };
        Assert.False(line.IsFormAvailable);
        Assert.NotEmpty(line.FormUnavailableReason);
        Assert.Empty(line.Parameters);
        line.HelpSchema = Schema;
        var sigma = Parameter(line, "sigma");
        line.HelpSchema = new HelpSchema(Help);
        Assert.NotSame(sigma, Parameter(line, "sigma"));
        line.HelpSchema = null;
        Assert.Empty(line.Parameters);
        Assert.False(line.IsFormAvailable);
        Assert.Equal("--vpp-alpha sigma=0.2", line.Text);
        line.HelpSchema = Schema;
        line.Text = "--vpp-legacy old=4";
        Assert.False(line.IsFormAvailable);
        Assert.Empty(line.Parameters);
        line.Text = "--vpp-empty";
        Assert.True(line.IsFormAvailable);
        Assert.Empty(line.Parameters);
        line.Text = "--vpp-alpha sigma=0.2 another";
        Assert.False(line.IsFormAvailable);
    }

    [Fact]
    public void CommaInputIsRejectedAndAValidTextEditClearsValidation()
    {
        var line = new FilterLineViewModel { Text = "--vpp-alpha file=old.ass", HelpSchema = Schema };
        var file = Parameter(line, "file");
        file.ValueText = "not,representable.ass";
        Assert.Equal("--vpp-alpha file=old.ass", line.Text);
        Assert.NotNull(file.ValidationMessage);
        line.Text = "--vpp-alpha file=new.ass";
        Assert.Null(file.ValidationMessage);
        Assert.Equal("new.ass", file.ValueText);
        file.ValueText = "still,invalid";
        file.ResetCommand.Execute(null);
        Assert.Null(file.ValidationMessage);
        Assert.Equal("--vpp-alpha", line.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t  ")]
    public void BlankValueTextRemovesTheAssignmentAndShortValue(string blank)
    {
        var line = new FilterLineViewModel { Text = "--vpp-alpha sigma=0.25,count=3", HelpSchema = Schema };
        var sigma = Parameter(line, "sigma");
        sigma.ValueText = blank;
        Assert.Equal("--vpp-alpha count=3", line.Text);
        Assert.False(sigma.IsSet);
        Assert.Null(sigma.ValueText);
        line.Text = "--vpp-scalar off";
        line.Parameters[0].ValueText = blank;
        Assert.Equal("--vpp-scalar", line.Text);
        Assert.Null(line.Parameters[0].ValueText);
    }

    [Fact]
    public void NullChoiceFromAControlDoesNotResetTheText()
    {
        var line = new FilterLineViewModel { Text = "--vpp-alpha mode=slow", HelpSchema = Schema };
        var mode = Parameter(line, "mode");
        mode.ChoiceValue = null;
        Assert.Equal("--vpp-alpha mode=slow", line.Text);
        Assert.Equal("slow", mode.ChoiceValue);
        mode.ResetCommand.Execute(null);
        Assert.Equal("--vpp-alpha", line.Text);
        Assert.False(mode.IsSet);
        mode.ChoiceValue = null;
        Assert.Equal("--vpp-alpha", line.Text);
        Assert.Equal("fast", mode.ChoiceValue);
    }

    [Fact]
    public void EffectiveValuesFromControlsDoNotCreateOrRewriteAssignments()
    {
        var line = new FilterLineViewModel { Text = "--vpp-alpha", HelpSchema = Schema };
        int textChanges = 0;
        line.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(line.Text)) ++textChanges; };
        var sigma = Parameter(line, "sigma");
        var mode = Parameter(line, "mode");
        var enabled = Parameter(line, "enabled");
        sigma.NumberValue = 0.5;
        mode.ChoiceValue = "fast";
        enabled.BoolValue = false;
        Assert.Equal("--vpp-alpha", line.Text);
        Assert.Equal(0, textChanges);
        Assert.All(line.Parameters, parameter => Assert.False(parameter.IsSet));

        // テキストによる明示指定は、既定値と等しくても保持する。
        sigma.ValueText = "0.500";
        mode.ValueText = "fast";
        enabled.ValueText = "off";
        string explicitText = line.Text;
        textChanges = 0;
        sigma.NumberValue = 0.5;
        mode.ChoiceValue = "fast";
        enabled.BoolValue = false;
        Assert.Equal(explicitText, line.Text);
        Assert.Equal(0, textChanges);
        Assert.True(sigma.IsSet);
        Assert.True(mode.IsSet);
        Assert.True(enabled.IsSet);
        sigma.NumberValue = 0.75;
        mode.ChoiceValue = "slow";
        enabled.BoolValue = true;
        Assert.Equal(3, textChanges);
        Assert.Equal("0.75", sigma.ValueText);
        Assert.Equal("slow", mode.ValueText);
        Assert.Equal("true", enabled.ValueText);
    }

    [Fact]
    public void BooleanWithWordsUsesChoiceAndIncludesDistinctBooleanValues()
    {
        var schema = new HelpSchema("""
VPP Options:
  [other] Other
   --vpp-bools [<param1>=<value>][,<param2>=<value>][...]
    params
      plain=<bool> Plain switch. (default: false)
        values: true, false, true
      mixed=<bool|auto> Three states. (default: auto)
        values: auto, true, false, auto
      minimal=<bool|auto> Implicit boolean states. (default: auto)
        values: auto
""");
        var line = new FilterLineViewModel { Text = "--vpp-bools", HelpSchema = schema };
        var plain = Parameter(line, "plain");
        var mixed = Parameter(line, "mixed");
        Assert.Equal(FilterParameterKind.Bool, plain.Kind);
        Assert.Equal(new[] { "true", "false" }, plain.Choices);
        Assert.Equal(FilterParameterKind.Choice, mixed.Kind);
        Assert.Equal(new[] { "auto", "true", "false" }, mixed.Choices);
        Assert.Equal(new[] { "auto", "true", "false" }, Parameter(line, "minimal").Choices);
        mixed.ChoiceValue = "true";
        Assert.Equal("--vpp-bools mixed=true", line.Text);
        Assert.Equal("true", mixed.ValueText);
    }

    [AvaloniaFact]
    public async Task FormEditsSubmitPreviewAndCatalogAllowsDuplicates()
    {
        using var services = new FormServices();
        string? previous = Environment.GetEnvironmentVariable("FAKE_ENCODER_HELP_FILE");
        Environment.SetEnvironmentVariable("FAKE_ENCODER_HELP_FILE", services.HelpPath);
        try
        {
            var clock = new FakeTimeProvider();
            int starts = 0;
            await using var vm = new MainWindowViewModel(services, clock, new OneShotBackend(_ => ++starts))
            { EncoderPath = FakeEncoderPath(), InputPath = "fake input.ts" };
            await vm.InitializeAsync(false).WaitAsync(SafetyTimeout);
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
            Assert.Equal(new[] { "--vpp-alpha", "--vpp-empty", "--vpp-scalar", "--vpp-legacy" }, vm.FilterCatalog.Select(item => item.OptionName));
            Assert.All(vm.FilterCatalog, item => Assert.False(item.IsInUse));
            vm.AddFilterCommand.Execute("--vpp-alpha");
            await RunPreparedAsync(vm, clock);
            var line = Assert.Single(vm.FilterLines);
            Assert.Same(line, vm.SelectedFilterLine);
            Assert.True(vm.FilterCatalog[0].IsInUse);
            int before = starts;
            Parameter(line, "sigma").NumberValue = 0.25;
            await RunPreparedAsync(vm, clock);
            Assert.True(starts > before);
            Assert.NotNull(vm.PreviewImage);
            Assert.Null(vm.ErrorText);
            Assert.Equal("--vpp-alpha sigma=0.25", line.Text);
            Parameter(line, "sigma").ResetCommand.Execute(null);
            await RunPreparedAsync(vm, clock);
            Assert.Equal("--vpp-alpha", line.Text);
            vm.AddFilterCommand.Execute("--vpp-alpha");
            await RunPreparedAsync(vm, clock);
            Assert.Equal(2, vm.FilterLines.Count);
            Assert.Same(vm.FilterLines[1], vm.SelectedFilterLine);
            vm.RemoveFilterLineCommand.Execute(vm.FilterLines[1]);
            vm.RemoveFilterLineCommand.Execute(vm.FilterLines[0]);
            await RunPreparedAsync(vm, clock);
            Assert.False(vm.FilterCatalog[0].IsInUse);
            vm.EncoderPath = "";
            Assert.Empty(vm.FilterCatalog);
        }
        finally { Environment.SetEnvironmentVariable("FAKE_ENCODER_HELP_FILE", previous); }
    }

    [AvaloniaFact]
    public async Task TextListModePersistsThroughSettings()
    {
        using var services = new FormServices();
        await using (var vm = new MainWindowViewModel(services))
        {
            Assert.False(vm.IsTextListMode);
            vm.IsTextListMode = true;
            await vm.SaveSettingsAsync();
        }
        await using var restored = new MainWindowViewModel(services);
        await restored.InitializeAsync().WaitAsync(SafetyTimeout);
        Assert.True(restored.IsTextListMode);
        Assert.True(AppSettings.Load(services.SettingsPath).IsTextListMode);
    }

    private static async Task RunPreparedAsync(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        await vm.WaitForPreparationAsync().WaitAsync(SafetyTimeout);
        clock.Advance(TimeSpan.FromMilliseconds(150));
        await vm.WaitForIdleAsync().WaitAsync(SafetyTimeout);
    }

    private static string FakeEncoderPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = directory.Parent!.Name;
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "FakeEncoder"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "FakeEncoder", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "FakeEncoder.exe" : "FakeEncoder");
    }

    private sealed class FormServices : IViewModelServices, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viewer-form-{Guid.NewGuid():N}");
        public FormServices() { Directory.CreateDirectory(_directory); File.WriteAllText(HelpPath, Help); }
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public string HelpPath => Path.Combine(_directory, "help.txt");
        public Task<string?> BrowseEncoderAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseInputAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseSaveFiltersAsync() => Task.FromResult<string?>(null);
        public Task<string?> ChooseLoadFiltersAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void Dispose() { foreach (string file in Directory.EnumerateFiles(_directory)) File.Delete(file); Directory.Delete(_directory); }
    }

    private static FilterParameterViewModel Parameter(FilterLineViewModel line, string name) =>
        Assert.Single(line.Parameters, parameter => parameter.Name == name);
}

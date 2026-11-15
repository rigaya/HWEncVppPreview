using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Tests;

public sealed class ExceptionDiagnosticViewModelTests
{
    [AvaloniaTheory]
    [InlineData("ja", "例外の詳細:")]
    [InlineData("en", "Exception details:")]
    public async Task DeepChainSurvivesStderrTailInBothDisplays(string language, string label)
    {
        string previous = UiLanguage.Preference;
        try
        {
            UiLanguage.Set(language);
            Exception exception;
            try { throw new IOException("native root message"); }
            catch (IOException root) { exception = root; }
            for (int index = 0; index < 9; index++) exception = new InvalidOperationException($"wrapper {index}", exception);
            string diagnostics = string.Join("\n", Enumerable.Range(0, 12).Select(index => $"stderr {index}"));
            await using var vm = FailureViewModelFixture.Create(diagnostics, -1, exception);
            await FailureViewModelFixture.RunAsync(vm);
            Assert.Contains(label, vm.ErrorText);
            Assert.Contains(label, vm.LogText);
            foreach (string line in ExceptionDiagnostics.Lines(exception))
            {
                Assert.Contains(line, vm.ErrorText);
                Assert.Contains(line, vm.LogText);
            }
            Assert.Contains(diagnostics, vm.LogText);
            Assert.Contains("stderr 11", vm.ErrorText);
            Assert.DoesNotContain("stderr 0", vm.ErrorText);
        }
        finally { UiLanguage.Set(previous); }
    }
}

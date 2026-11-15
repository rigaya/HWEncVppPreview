using Avalonia.Headless.XUnit;
using HWEncVppPreview.App.ViewModels;
namespace HWEncVppPreview.App.Tests;
public sealed class WindowSeekDiagnosticTests
{
    [AvaloniaTheory]
    [InlineData("avqsv: ", "preview window keyframe buffer exceeds 256 MiB.", -4)]
    [InlineData("avqsv: ", "preview window keyframe not found within 96 seconds; falling back to legacy seek (scanned 5862 packets).", -1)]
    [InlineData("avhw: ", "preview window keyframe buffer exceeds 256 MiB.", -4)]
    [InlineData("avhw: ", "preview window keyframe not found within 96 seconds; falling back to legacy seek (scanned 5862 packets).", -1)]
    public async Task KeyframeReasonSurvivesCleanupLines(string prefix, string warning, int exitCode)
    {
        string reason=prefix+warning;
        string diagnostics=reason+"\n"+string.Join("\n",Enumerable.Repeat("reader: closed",12))+$"\nRGYPREVIEW END id=1 rc={exitCode}";
        await using var vm = FailureViewModelFixture.Create(diagnostics, exitCode);
        await FailureViewModelFixture.RunAsync(vm);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains(reason,vm.ErrorText);
        Assert.Contains($"RGYPREVIEW END id=1 rc={exitCode}",vm.ErrorText);
        Assert.Equal(diagnostics,vm.LogText);
    }
}

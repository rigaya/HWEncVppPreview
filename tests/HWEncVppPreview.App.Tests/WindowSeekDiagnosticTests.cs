using System.Reflection;
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
        await using var vm=new MainWindowViewModel();
        string reason=prefix+warning;
        string diagnostics=reason+"\n"+string.Join("\n",Enumerable.Repeat("reader: closed",12))+$"\nRGYPREVIEW END id=1 rc={exitCode}";
        typeof(MainWindowViewModel).GetMethod("ShowError",BindingFlags.NonPublic|BindingFlags.Instance)!
            .Invoke(vm,[$"エンコーダがエラー終了しました: {exitCode}",diagnostics]);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains(reason,vm.ErrorText);
        Assert.Contains($"RGYPREVIEW END id=1 rc={exitCode}",vm.ErrorText);
        Assert.Equal(diagnostics,vm.LogText);
    }
}

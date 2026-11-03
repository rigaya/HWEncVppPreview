using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using HWEncParamViewer.App.ViewModels;
using HWEncParamViewer.App.Views;
using static HWEncParamViewer.App.Tests.CompletionViewTests;

namespace HWEncParamViewer.App.Tests;

/// <summary>
/// パラメータのフォームと追加の一覧の View (設計書 23.2、23.3)。help は H1 の書式の実 help を偽エンコーダから出す。
/// 値の解釈は ViewModel のテスト (FilterFormTests) で確かめているので、ここは操作が行のテキストに届くことと、
/// 表示しただけでは行を書き換えないことを見る。
/// </summary>
public sealed class FormViewTests
{
    [AvaloniaFact]
    public async Task TheEmptyStateListAddsAFilterFromTheKeyboard()
    {
        await using var session = await Session.StartAsync(textListMode: false);
        var catalog = session.Window.FindControl<FilterCatalogView>("InlineCatalog")!;
        Assert.True(catalog.IsEffectivelyVisible);
        var search = catalog.GetVisualDescendants().OfType<TextBox>().First();
        search.Focus();
        session.Window.KeyTextInput("nlmeans");
        await Flush();
        session.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Flush();

        var line = Assert.Single(session.ViewModel.FilterLines);
        Assert.Equal("--vpp-nlmeans", line.Text);
        Assert.Same(line, session.ViewModel.SelectedFilterLine);
        Assert.False(catalog.IsEffectivelyVisible);
        Assert.Contains(ParameterNames(session), name => name == "sigma");
    }

    [AvaloniaFact]
    public async Task TheAddListLeavesOutOtherSettingsButCompletionKeepsThem()
    {
        await using var session = await Session.StartAsync(textListMode: false);
        var names = session.ViewModel.FilterCatalog.Select(item => item.OptionName).ToList();
        Assert.Contains("--vpp-nlmeans", names);
        // [other] は追加の一覧に出さない
        Assert.DoesNotContain("--vpp-resize-mode", names);
        Assert.DoesNotContain("--vpp-rtgmc-primitive", names);
        // 補完 (テキストでの入力) では使える
        Assert.Contains("--vpp-resize-mode", session.ViewModel.HelpSchema!.OptionNames);
    }

    [AvaloniaFact]
    public async Task ShowingAndSwitchingFormsDoesNotRewriteLines()
    {
        await using var session = await Session.StartAsync(textListMode: false);
        string[] texts = ["--vpp-kfm mode=24,thswitch=0.6,  past_cycles=12", "--vpp-nlmeans sigma=0.010"];
        foreach (var text in texts) session.ViewModel.FilterLines.Add(new FilterLineViewModel { Text = text });
        foreach (int index in new[] { 0, 1, 0, 1 })
        {
            session.ViewModel.SelectedFilterLine = session.ViewModel.FilterLines[index];
            await Flush();
        }
        Assert.Equal(texts, session.ViewModel.FilterLines.Select(line => line.Text));
    }

    [AvaloniaFact]
    public async Task TheSliderWritesOnlyWhileTheUserOperatesIt()
    {
        await using var session = await Session.StartAsync(textListMode: false);
        var line = await session.SelectAsync("--vpp-kfm mode=24");
        var slider = Field<Slider>(session, "thswitch");

        // 利用者の操作でない値の変化 (初期化・補正) は書き戻さない
        slider.Value = 0.8;
        await Flush();
        Assert.Equal("--vpp-kfm mode=24", line.Text);

        slider.Focus();
        slider.Value = 0.7;
        await Flush();
        Assert.Equal("--vpp-kfm mode=24,thswitch=0.7", line.Text);
    }

    [AvaloniaFact]
    public async Task ChoosingAndResettingAChoiceRewritesOnlyThatParameter()
    {
        await using var session = await Session.StartAsync(textListMode: false);
        var line = await session.SelectAsync("--vpp-kfm thswitch=0.6");
        var combo = Field<ComboBox>(session, "mode");
        combo.SelectedItem = "60";
        await Flush();
        Assert.Equal("--vpp-kfm thswitch=0.6,mode=60", line.Text);

        var reset = session.Window.GetVisualDescendants().OfType<Button>()
            .First(button => button.Content as string == "↺" && button.DataContext is FilterParameterViewModel { Name: "mode" });
        reset.Command!.Execute(null);
        await Flush();
        Assert.Equal("--vpp-kfm thswitch=0.6", line.Text);
    }

    [AvaloniaFact]
    public async Task TheWheelOverANumberFieldStepsFromTheDefault()
    {
        await using var session = await Session.StartAsync(textListMode: false);
        var line = await session.SelectAsync("--vpp-nlmeans");
        var box = Field<TextBox>(session, "patch");
        var center = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), session.Window)!.Value;
        session.Window.MouseWheel(center, new Vector(0, 1));
        await Flush();
        Assert.Equal("--vpp-nlmeans patch=6", line.Text);
    }

    [AvaloniaFact]
    public async Task ACommaInAValueIsRejectedWithAMessage()
    {
        await using var session = await Session.StartAsync(textListMode: false);
        var line = await session.SelectAsync("--vpp-nlmeans sigma=0.01");
        var box = Field<TextBox>(session, "sigma");
        box.Text = "0.02,h=1";
        await Flush();
        Assert.Equal("--vpp-nlmeans sigma=0.01", line.Text);
        var message = session.Window.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("paramError") && text.DataContext is FilterParameterViewModel { Name: "sigma" });
        Assert.True(message.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task AWordOrNumberFieldPicksOneOfThemWithButtons()
    {
        await using var session = await Session.StartAsync(textListMode: false);
        var line = await session.SelectAsync("--vpp-kfm mode=24");
        Button Segment(string text) => session.Window.GetVisualDescendants().OfType<Button>()
            .First(button => button.IsEffectivelyVisible && button.Content as string == text
                && (button.DataContext is FilterParameterViewModel { Name: "search_early_sad" }
                    || button.FindAncestorOfType<ItemsControl>()?.DataContext is FilterParameterViewModel { Name: "search_early_sad" }));
        var number = session.Window.GetVisualDescendants().OfType<TextBox>()
            .First(box => box.Classes.Contains("numberChoice") && box.DataContext is FilterParameterViewModel { Name: "search_early_sad" });

        // 未指定では既定の語 (auto) が選ばれた表示で、数値の欄は使えない
        Assert.Contains("active", Segment("auto").Classes);
        Assert.False(number.IsEnabled);

        Segment("off").Command!.Execute(null);
        await Flush();
        Assert.Equal("--vpp-kfm mode=24,search_early_sad=off", line.Text);

        // 「数値」を押しただけでは行を変えず、数値の欄が使えるようになる
        Segment("数値").Command!.Execute(null);
        await Flush();
        Assert.Equal("--vpp-kfm mode=24,search_early_sad=off", line.Text);
        Assert.True(number.IsEnabled);
        Assert.DoesNotContain("active", Segment("off").Classes);

        number.Text = "16";
        await Flush();
        Assert.Equal("--vpp-kfm mode=24,search_early_sad=16", line.Text);
        Assert.Contains("active", Segment("数値").Classes);
    }

    [AvaloniaFact]
    public async Task TheResetButtonKeepsItsPlaceWhetherOrNotTheValueIsWritten()
    {
        await using var session = await Session.StartAsync(textListMode: false);
        var line = await session.SelectAsync("--vpp-kfm mode=24");
        var slider = Field<Slider>(session, "thswitch");
        double width = slider.Bounds.Width;
        line.Text = "--vpp-kfm mode=24,thswitch=0.7";
        await Flush();
        Assert.Equal(width, Field<Slider>(session, "thswitch").Bounds.Width);
    }

    private static IEnumerable<string> ParameterNames(Session session) =>
        session.Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("paramName") && text.IsEffectivelyVisible)
            .Select(text => text.Text ?? "");

    /// <summary>名前が name のパラメータの、いま表示されている欄 (種類に応じて1つだけ見える)。</summary>
    private static T Field<T>(Session session, string name) where T : Control =>
        session.Window.GetVisualDescendants().OfType<T>()
            .First(control => control.IsEffectivelyVisible && control.DataContext is FilterParameterViewModel parameter && parameter.Name == name);
}

internal static class FormSessionExtensions
{
    /// <summary>1行を足して選び、フォームを表示する。</summary>
    public static async Task<FilterLineViewModel> SelectAsync(this Session session, string text)
    {
        var line = new FilterLineViewModel { Text = text };
        session.ViewModel.FilterLines.Add(line);
        session.ViewModel.SelectedFilterLine = line;
        await Flush();
        return line;
    }
}

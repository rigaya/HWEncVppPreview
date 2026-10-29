using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.Services;

/// <summary>OSのファイル選択とクリップボードをViewModelから分離する。</summary>
public interface IViewModelServices
{
    string SettingsPath { get; }
    Task<string?> BrowseEncoderAsync();
    Task<string?> BrowseInputAsync();
    Task<string?> ChooseSaveFiltersAsync();
    Task<string?> ChooseLoadFiltersAsync();
    Task CopyTextAsync(string text);
}

/// <summary>表示中のウィンドウに対して非同期のOS操作を行う。</summary>
public sealed class AvaloniaViewModelServices : IViewModelServices
{
    public string SettingsPath => AppSettings.DefaultPath;
    private static Window? Window => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    public Task<string?> BrowseEncoderAsync() => OpenAsync("エンコーダを選択");
    public Task<string?> BrowseInputAsync() => OpenAsync("入力ファイルを選択");
    public Task<string?> ChooseLoadFiltersAsync() => OpenAsync("フィルタのテキストを読み込み");
    private static async Task<string?> OpenAsync(string title)
    {
        if (Window is not { } window) return null;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
    public async Task<string?> ChooseSaveFiltersAsync()
    {
        if (Window is not { } window) return null;
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = "フィルタのテキストを保存", SuggestedFileName = "filters.txt", DefaultExtension = "txt" });
        return file?.TryGetLocalPath();
    }
    public async Task CopyTextAsync(string text)
    {
        if (Window?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}

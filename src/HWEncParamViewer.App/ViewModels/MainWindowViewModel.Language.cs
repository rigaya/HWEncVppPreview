using Avalonia.Threading;
using HWEncParamViewer.Core;

namespace HWEncParamViewer.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>保存する言語選択。autoではOSのUI言語を使う。</summary>
    public string Language
    {
        get => UiLanguage.Preference;
        set => UiLanguage.Set(value);
    }

    private void OnLanguageChanged()
    {
        void Refresh()
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(Language));
            OnPropertyChanged(nameof(EncoderDescription));
            foreach (var line in FilterLines) line.RefreshLanguage();
            RefreshFilterCatalog();
            RefreshColorInfo();
        }
        // UIの完了を同期的に待たず、言語の変更元が別スレッドでも安全に反映する。
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }
}

using Avalonia.Threading;
using HWEncVppPreview.App.ViewModels;
using HWEncVppPreview.Core;

namespace HWEncVppPreview.App.Localization;

/// <summary>画面全体の文言を、言語の変更時にまとめて差し替える。</summary>
public sealed class Localizer : ViewModelBase
{
    public static Localizer Instance { get; } = new();
    private UiText _text = new();
    private Localizer() => UiLanguage.Changed += LanguageChanged;
    public UiText Text => _text;
    public string Language { get => UiLanguage.Preference; set => UiLanguage.Set(value); }

    private void LanguageChanged()
    {
        _text = new UiText();
        void Notify()
        {
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(Language));
        }
        // Viewの購読先も含めて、通知はUIスレッドへ非同期に戻す。
        if (Dispatcher.UIThread.CheckAccess()) Notify();
        else Dispatcher.UIThread.Post(Notify);
    }
}

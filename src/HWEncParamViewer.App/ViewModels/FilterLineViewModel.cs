namespace HWEncParamViewer.App.ViewModels;

/// <summary>フィルタ編集欄の1行 (設計書 17.5)。</summary>
public sealed class FilterLineViewModel : ViewModelBase
{
    private bool _isEnabled = true;
    private string _text = "";

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value)) OnPropertyChanged(nameof(OptionName));
        }
    }

    /// <summary>行頭の `--vpp-xxx` の部分。ヘルプの検索に使う。</summary>
    public string OptionName
    {
        get
        {
            var t = _text.TrimStart();
            int end = t.IndexOfAny([' ', '\t']);
            return end < 0 ? t : t[..end];
        }
    }

    /// <summary>
    /// caret 位置の数値を増減する (設計書 4.2)。direction は +1/-1、coarse は Shift (10倍)。
    /// 変更したら Text を書き換えて新しいカーソル位置を返す。数値がなければ null。
    /// TODO(airi): Core の NumberNudge で実装する。
    /// </summary>
    public int? Nudge(int caret, int direction, bool coarse) => null;
}

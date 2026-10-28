using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HWEncParamViewer.App.ViewModels;

/// <summary>INotifyPropertyChanged の最小限の基底クラス (外部の MVVM パッケージは使わない)。</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>値が変わったときだけ代入して通知する。変わったら true。</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

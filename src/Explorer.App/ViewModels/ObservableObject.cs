using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Explorer.App.ViewModels;

/// <summary>
/// 값이 바뀌면 화면에 알리는 뷰모델의 바탕입니다.
/// MVVM 라이브러리를 따로 쓰지 않는 이유는 필요한 것이 이 두 개뿐이기 때문입니다.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Explorer.App.Converters;

/// <summary>빈 문자열이면 요소를 숨깁니다. 가운뎃점 같은 구분 기호를 지울 때 씁니다.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

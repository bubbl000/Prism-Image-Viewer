using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ImageBrowser.Converters;

/// <summary>
/// 布尔值转边框厚度转换器
/// </summary>
public class BoolToThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isSelected && isSelected)
        {
            return new Thickness(2);
        }
        return new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

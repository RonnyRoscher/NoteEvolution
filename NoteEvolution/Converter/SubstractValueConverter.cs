using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace NoteEvolution.Converter
{
    public class SubstractValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double width && double.TryParse(parameter?.ToString(), out var sub))
                return width - sub;
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value;
        }
    }
}

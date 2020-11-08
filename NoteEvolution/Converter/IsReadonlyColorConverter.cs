using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NoteEvolution.Converter
{
    public class IsReadonlyColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isReadonly && isReadonly)
                return Brushes.LightGray;
            return Brushes.Black;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value;
        }
    }
}

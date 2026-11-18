using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace Brows.Windows.Controls;

internal sealed class RecordingConverter : IValueConverter {
    public List<object> Values { get; } = [];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        Values.Add(value);
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Brows.Windows.Controls;

internal sealed class LocalWidthHeaderGrid : Grid {
    public LocalWidthHeaderGrid() {
        ColumnDefinitions.Add(new ColumnDefinition { SharedSizeGroup = "Name", Width = new GridLength(120) });
        var data = new ColumnDefinition { SharedSizeGroup = "Data" };
        BindingOperations.SetBinding(data, ColumnDefinition.WidthProperty,
                                     new Binding { Source = new GridLength(200) });
        ColumnDefinitions.Add(data);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Brows.Windows.Controls;

internal sealed class OperationInlinePanel : Decorator {
    private const double LayoutTolerance = 0.5;

    private double _AvailableWidth = double.PositiveInfinity;
    private bool _CheckingWidth;

    private OperatorControl Owner {
        get {
            for (var parent = VisualTreeHelper.GetParent(this); parent is not null;
                 parent = VisualTreeHelper.GetParent(parent)) {
                if (parent is OperatorControl owner) {
                    return owner;
                }
            }
            return null;
        }
    }

    private double AvailableWidth(OperatorControl owner) {
        return System.Math.Max(0, owner?.AvailableRowWidth(this, ViewportWidth) ?? ViewportWidth);
    }

    private void OperationInlinePanel_LayoutUpdated(object sender, EventArgs e) {
        /*
         * Positions from the previous layout are used while measuring, and an ancestor's inset can
         * change without changing this panel's constraint, so WPF would not measure it again. Check
         * after each layout while the panel is limited to a viewport, and measure again only if the
         * width available to this row changed. The check stops outside a viewport and resumes on the
         * next measure.
         */
        var owner = Owner;
        if (owner is null || double.IsPositiveInfinity(ViewportWidth)) {
            LayoutUpdated -= OperationInlinePanel_LayoutUpdated;
            _CheckingWidth = false;
            return;
        }
        if (System.Math.Abs(AvailableWidth(owner) - _AvailableWidth) > LayoutTolerance) {
            InvalidateMeasure();
        }
    }

    internal static readonly DependencyProperty ViewportWidthProperty = DependencyProperty.Register(
        name: nameof(ViewportWidth),
        propertyType: typeof(double),
        ownerType: typeof(OperationInlinePanel),
        typeMetadata: new FrameworkPropertyMetadata(
            defaultValue: double.PositiveInfinity,
            flags: FrameworkPropertyMetadataOptions.AffectsMeasure));

    internal double ViewportWidth {
        get => (double)GetValue(ViewportWidthProperty);
        set => SetValue(ViewportWidthProperty, value);
    }

    protected override Size MeasureOverride(Size constraint) {
        /*
         * Limit the child to the viewport less this panel's own inset, and to any width limits around it,
         * so that inline values wrap and root rows keep their commands within the viewport even when the
         * scroll viewer measures its content without a width limit. An unbounded viewport, as in Default
         * mode, leaves the child unlimited.
         */
        if (double.IsPositiveInfinity(ViewportWidth)) {
            _AvailableWidth = double.PositiveInfinity;
        }
        else {
            _AvailableWidth = AvailableWidth(Owner);
            if (!_CheckingWidth) {
                _CheckingWidth = true;
                LayoutUpdated += OperationInlinePanel_LayoutUpdated;
            }
        }
        if (Child is null) {
            return default;
        }
        Child.Measure(new Size(System.Math.Min(constraint.Width, _AvailableWidth), constraint.Height));
        return Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeSize) {
        Child?.Arrange(new Rect(new Size(System.Math.Min(arrangeSize.Width, _AvailableWidth), arrangeSize.Height)));
        return arrangeSize;
    }
}

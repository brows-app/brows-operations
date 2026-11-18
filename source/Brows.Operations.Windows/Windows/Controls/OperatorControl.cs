using Brows.Operations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Brows.Windows.Controls;

/// <summary>
/// Displays operations from an <see cref="IOperator"/> as a hierarchy.
/// </summary>
/// <remarks>
/// Supports the built-in operators created by <c>IOperatorFactory</c>. Relevant operations and
/// their ancestor paths are displayed with progress, errors, and cancellation or removal commands.
/// Create the operator on the WPF dispatcher, or pass its dispatcher context to
/// <c>IOperatorFactory.Create(SynchronizationContext)</c>. Root and child collection changes are
/// posted asynchronously to that dispatcher. Operations and progress may be updated on workers;
/// property-change notifications run on the updating thread and WPF bindings marshal bound values.
/// Built-in Cancel and Remove commands deliver CanExecuteChanged on their creating dispatcher;
/// changes raised on workers are queued asynchronously to that dispatcher.
/// </remarks>
public sealed partial class OperatorControl : Control {
    static OperatorControl() {
        DefaultStyleKeyProperty.OverrideMetadata(
            forType: typeof(OperatorControl),
            typeMetadata: new FrameworkPropertyMetadata(typeof(OperatorControl)));
    }

    private const double DefaultProgressBarMinimumWidth = 100;
    private const double LayoutTolerance = 0.5;
    private const string OperationHeaderGridTemplatePartName = "PART_OperationHeaderGrid";
    private const string OperationListScrollViewerTemplatePartName = "PART_OperationListScrollViewer";
    private const string OperationProgressBarProbeHostTemplatePartName = "PART_OperationProgressBarProbeHost";
    private const string OperationRowGridName = "_OperationRowGrid";

    private double _AvailableWidth = double.PositiveInfinity;
    private OperationDisplayStage _DisplayStage;
    private bool _HeaderColumnWidthUpdatePending;
    private ResponsiveHeaderColumn[] _OperationHeaderColumns = [];
    private Grid _OperationHeaderGrid;
    private TranslateTransform _OperationHeaderTransform;
    private ScrollViewer _OperationListScrollViewer;
    private double _OperationRowWidth;
    private Decorator _ProgressBarProbeHost;
    private FrameworkElement _VerticalScrollBar;
    private double _VerticalScrollBarWidth = double.NaN;

    /*
     * In Responsive mode, a hidden progress bar probe in the template's probe host resolves
     * OperationProgressBarStyle in the same resource scope as the operation rows. Its effective
     * width constraints and margin are bound to these properties so that changes to dynamic resources
     * reevaluate the display stage.
     */
    private static readonly DependencyProperty ProgressBarProbeWidthProperty = DependencyProperty.Register(
        name: nameof(ProgressBarProbeWidth),
        propertyType: typeof(double),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: double.NaN,
            propertyChangedCallback: (s, _) => (s as OperatorControl)?.UpdateDisplayStage()));

    private static readonly DependencyProperty ProgressBarProbeMinWidthProperty = DependencyProperty.Register(
        name: nameof(ProgressBarProbeMinWidth),
        propertyType: typeof(double),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: 0d,
            propertyChangedCallback: (s, _) => (s as OperatorControl)?.UpdateDisplayStage()));

    private static readonly DependencyProperty ProgressBarProbeMaxWidthProperty = DependencyProperty.Register(
        name: nameof(ProgressBarProbeMaxWidth),
        propertyType: typeof(double),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: double.PositiveInfinity,
            propertyChangedCallback: (s, _) => (s as OperatorControl)?.UpdateDisplayStage()));

    private static readonly DependencyProperty ProgressBarProbeMarginProperty = DependencyProperty.Register(
        name: nameof(ProgressBarProbeMargin),
        propertyType: typeof(Thickness),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: default(Thickness),
            propertyChangedCallback: (s, _) => (s as OperatorControl)?.UpdateDisplayStage()));

    private static readonly (DependencyProperty Target, string Path)[] ProgressBarProbeBindings = [
        (ProgressBarProbeWidthProperty, nameof(Width)),
        (ProgressBarProbeMinWidthProperty, nameof(MinWidth)),
        (ProgressBarProbeMaxWidthProperty, nameof(MaxWidth)),
        (ProgressBarProbeMarginProperty, nameof(Margin))
    ];

    private static readonly DependencyPropertyKey OperationCollectionKey = DependencyProperty.RegisterReadOnly(
        name: nameof(OperationCollection),
        propertyType: typeof(OperationCollection),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    private static readonly DependencyPropertyKey IsProgressBarCollapsedKey = DependencyProperty.RegisterReadOnly(
        name: nameof(IsProgressBarCollapsed),
        propertyType: typeof(bool),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: false));

    private static readonly DependencyPropertyKey AreProgressRatesInlineKey = DependencyProperty.RegisterReadOnly(
        name: nameof(AreProgressRatesInline),
        propertyType: typeof(bool),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: false));

    private static readonly DependencyPropertyKey IsProgressInlineKey = DependencyProperty.RegisterReadOnly(
        name: nameof(IsProgressInline),
        propertyType: typeof(bool),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: false));

    private static readonly DependencyPropertyKey IsDataInlineKey = DependencyProperty.RegisterReadOnly(
        name: nameof(IsDataInline),
        propertyType: typeof(bool),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: false));

    private static readonly DependencyPropertyKey InlineViewportWidthKey = DependencyProperty.RegisterReadOnly(
        name: nameof(InlineViewportWidth),
        propertyType: typeof(double),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: double.PositiveInfinity));

    private static readonly DependencyPropertyKey RowViewportWidthKey = DependencyProperty.RegisterReadOnly(
        name: nameof(RowViewportWidth),
        propertyType: typeof(double),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: double.PositiveInfinity));

    private double ProgressBarProbeWidth => (double)GetValue(ProgressBarProbeWidthProperty);

    private double ProgressBarProbeMinWidth => (double)GetValue(ProgressBarProbeMinWidthProperty);

    private double ProgressBarProbeMaxWidth => (double)GetValue(ProgressBarProbeMaxWidthProperty);

    private Thickness ProgressBarProbeMargin => (Thickness)GetValue(ProgressBarProbeMarginProperty);

    private double ProgressBarMinimumWidth {
        get {
            /*
             * Resolve the width as WPF layout does: an explicit width, or the minimum width
             * (100 DIP when unset), limited by the maximum width, which yields to the minimum.
             */
            var minimumWidth = ProgressBarProbeMinWidth;
            var preferredWidth = double.IsNaN(ProgressBarProbeWidth)
                ? (minimumWidth > 0 ? minimumWidth : DefaultProgressBarMinimumWidth)
                : ProgressBarProbeWidth;
            var width = System.Math.Max(minimumWidth, System.Math.Min(preferredWidth, ProgressBarProbeMaxWidth));
            var margin = ProgressBarProbeMargin;
            return width + margin.Left + margin.Right;
        }
    }

    private void UpdateProgressBarProbe() {
        if (_ProgressBarProbeHost is null || OperationDisplay != OperationDisplay.Responsive) {
            /*
             * Default mode applies the progress bar style only to operation rows,
             * so the probe exists only while the responsive layout needs it.
             */
            foreach (var (target, _) in ProgressBarProbeBindings) {
                BindingOperations.ClearBinding(this, target);
            }
            if (_ProgressBarProbeHost is not null) {
                _ProgressBarProbeHost.Child = null;
            }
            return;
        }
        if (_ProgressBarProbeHost.Child is ProgressBar) {
            return;
        }
        var probe = new ProgressBar {
            DataContext = null, // Keep the host's data context from operation-row style bindings.
            Focusable = false,
            IsTabStop = false
        };
        probe.SetBinding(StyleProperty, new Binding(nameof(OperationProgressBarStyle)) { Source = this });
        _ProgressBarProbeHost.Child = probe;
        foreach (var (target, path) in ProgressBarProbeBindings) {
            BindingOperations.SetBinding(this, target, new Binding(path) { Source = probe });
        }
    }

    private static double RootOperationRowWidth(DependencyObject element, UIElement content, double viewportWidth) {
        /*
         * Search the visible root operations for their rows. Child operations are skipped
         * because only root rows hold commands, and the narrowest row limits the shared columns.
         */
        if (element is UIElement { Visibility: not Visibility.Visible } ||
            element is FrameworkElement { DataContext: OperationBase { Parent: not null } }) {
            return double.PositiveInfinity;
        }
        if (element is FrameworkElement { Name: OperationRowGridName, DataContext: OperationBase } row) {
            return OperationRowWidth(row, content, viewportWidth);
        }
        var width = double.PositiveInfinity;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) {
            width = System.Math.Min(
                width,
                RootOperationRowWidth(VisualTreeHelper.GetChild(element, i), content, viewportWidth));
        }
        return width;
    }

    private double OperationRowWidth(double viewportWidth) {
        var width = _OperationListScrollViewer?.Content is UIElement content
            ? RootOperationRowWidth(content, content, viewportWidth)
            : double.PositiveInfinity;
        return System.Math.Min(width, viewportWidth);
    }

    private void OperatorControl_LayoutUpdated(object sender, EventArgs e) {
        /*
         * An items template can change its inset, and a header column its width, without changing the
         * scroll viewer's extent or viewport, so recheck both after layout and update only on change.
         */
        if (OperationDisplay != OperationDisplay.Responsive ||
            _OperationListScrollViewer is not { ViewportWidth: > 0 } scrollViewer) {
            return;
        }
        var width = ReservedViewportWidth(scrollViewer.ViewportWidth);
        if (System.Math.Abs(width - RowViewportWidth) > LayoutTolerance || HeaderColumnWidthsChanged() ||
            System.Math.Abs(OperationRowWidth(width) - _OperationRowWidth) > LayoutTolerance) {
            UpdateDisplayStage();
        }
    }

    private static double OperationRowWidth(FrameworkElement row, UIElement content, double viewportWidth) {
        /*
         * Measure the width available to an operation row: the viewport less the horizontal space that
         * the items template and its containers reserve around the row, limited by any width or maximum
         * width along the way. At each level, the inset sums the space a parent keeps on either side of
         * the slot it offers its child, plus the child's margins. Slots are used instead of positions and
         * actual sizes because alignment can move a container within its slot or shrink it to its content.
         * A container's width limit applies to the space inside it, so it is reduced by the inset
         * accumulated within it. The space that an inline panel leaves beside the child it limits is the
         * result of this width, not an inset.
         */
        var inset = 0d;
        var width = double.PositiveInfinity;
        DependencyObject child = row;
        var parent = VisualTreeHelper.GetParent(child);
        while (parent is not null && !ReferenceEquals(child, content)) {
            if (child is FrameworkElement element) {
                var explicitWidth = double.IsNaN(element.Width) ? double.PositiveInfinity : element.Width;
                var limit = System.Math.Max(element.MinWidth, System.Math.Min(explicitWidth, element.MaxWidth));
                width = System.Math.Min(width, limit - inset);
                if (parent is FrameworkElement container) {
                    var slot = LayoutInformation.GetLayoutSlot(element);
                    var trailing = container is OperationInlinePanel
                        ? 0
                        : System.Math.Max(0, container.ActualWidth - slot.Right);
                    inset += System.Math.Max(0, slot.Left) + element.Margin.Left + trailing + element.Margin.Right;
                }
            }
            child = parent;
            parent = VisualTreeHelper.GetParent(child);
        }
        return System.Math.Min(width, viewportWidth - inset);
    }

    private static double ViewportOverlap(Rect bounds, double width) =>
        System.Math.Max(0, System.Math.Min(bounds.Right, width) - System.Math.Max(0, bounds.Left));

    private double ReservedViewportWidth(double viewportWidth) {
        /*
         * A stage change can make the rows taller or shorter, and so change whether they fit vertically.
         * Reserve an automatic scrollbar's width when hidden. The visible viewport already accounts for a
         * docked scrollbar, but an overlaid scrollbar also needs its overlapping width reserved when shown.
         */
        var scrollViewer = _OperationListScrollViewer;
        var scrollBar = scrollViewer.Template?.FindName("PART_VerticalScrollBar", scrollViewer) as FrameworkElement;
        if (!ReferenceEquals(scrollBar, _VerticalScrollBar)) {
            _VerticalScrollBar = scrollBar;
            _VerticalScrollBarWidth = double.NaN;
        }
        var presenter = scrollViewer.Template?.FindName("PART_ScrollContentPresenter", scrollViewer)
            as FrameworkElement;
        var margin = scrollBar is null ? 0 : scrollBar.Margin.Left + scrollBar.Margin.Right;
        if (scrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible) {
            if (scrollBar is not null) {
                _VerticalScrollBarWidth = scrollBar.ActualWidth;
                if (presenter is not null && scrollBar.IsDescendantOf(scrollViewer) &&
                    presenter.IsDescendantOf(scrollViewer)) {
                    var outerWidth = System.Math.Max(0, _VerticalScrollBarWidth + margin);
                    var bounds = scrollBar.TransformToVisual(presenter).TransformBounds(
                        new Rect(-scrollBar.Margin.Left, 0, outerWidth, scrollBar.ActualHeight));
                    return System.Math.Max(0, viewportWidth - ViewportOverlap(bounds, presenter.ActualWidth));
                }
            }
            return viewportWidth;
        }
        if (scrollViewer.VerticalScrollBarVisibility != ScrollBarVisibility.Auto) {
            return viewportWidth;
        }
        var fallback = double.IsNaN(_VerticalScrollBarWidth)
            ? SystemParameters.VerticalScrollBarWidth
            : _VerticalScrollBarWidth;
        var width = scrollBar is null || double.IsNaN(scrollBar.Width) ? fallback : scrollBar.Width;
        if (scrollBar is not null) {
            width = System.Math.Max(scrollBar.MinWidth, System.Math.Min(width, scrollBar.MaxWidth));
        }
        var reserve = System.Math.Max(0, width + margin);
        if (scrollBar is not null && presenter is not null &&
            VisualTreeHelper.GetParent(scrollBar) is Visual parent && presenter.IsDescendantOf(scrollViewer)) {
            /*
             * A fixed gutter can still occupy space when its scrollbar is collapsed.
             * Credit the portion of the bar's layout slot already outside the presenter.
             */
            var slot = LayoutInformation.GetLayoutSlot(scrollBar);
            if (slot.Width > 0 && parent.IsDescendantOf(scrollViewer)) {
                var spare = System.Math.Max(0, slot.Width - reserve);
                slot.X += scrollBar.HorizontalAlignment switch {
                    HorizontalAlignment.Left => 0,
                    HorizontalAlignment.Right => spare,
                    _ => spare / 2
                };
                slot.Width -= spare;
                var bounds = parent.TransformToVisual(presenter).TransformBounds(slot);
                var gutter = bounds.Width - ViewportOverlap(bounds, presenter.ActualWidth);
                reserve = System.Math.Max(0, reserve - gutter);
            }
        }
        return System.Math.Max(0, viewportWidth - reserve);
    }

    private static bool IsHeaderColumnCollapsed(string sharedSizeGroup, OperationDisplayStage stage) {
        return sharedSizeGroup switch {
            "ProgressRate" or "ProgressRateAverage" => stage >= OperationDisplayStage.ProgressRatesInline,
            "Progress" or "Target" => stage >= OperationDisplayStage.ProgressInline,
            "Data" => stage >= OperationDisplayStage.DataInline,
            _ => false
        };
    }

    private void OperationListScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) {
        if (!ReferenceEquals(e.OriginalSource, _OperationListScrollViewer)) {
            return;
        }
        if (_OperationHeaderTransform is not null && e.HorizontalChange != 0) {
            _OperationHeaderTransform.X = -_OperationListScrollViewer.HorizontalOffset;
        }
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0 ||
            e.ExtentWidthChange != 0 || e.ExtentHeightChange != 0) {
            UpdateDisplayStage();
        }
    }

    private double RequiredWidth(OperationDisplayStage stage) {
        var width = stage == OperationDisplayStage.Full
            ? ProgressBarMinimumWidth
            : 0;
        foreach (var column in _OperationHeaderColumns) {
            if (!IsHeaderColumnCollapsed(column.SharedSizeGroup, stage)) {
                width += column.RequiredWidth;
            }
        }
        return width;
    }

    private OperationDisplayStage SelectDisplayStage(double availableWidth) {
        for (var stage = OperationDisplayStage.Full; stage < OperationDisplayStage.DataInline; stage++) {
            if (RequiredWidth(stage) - availableWidth <= LayoutTolerance) {
                return stage;
            }
        }
        return OperationDisplayStage.DataInline;
    }

    private bool HeaderColumnWidthsChanged() {
        foreach (var column in _OperationHeaderColumns) {
            if (column.NeedsMeasure) {
                return true;
            }
        }
        return false;
    }

    private void QueueHeaderColumnWidthUpdate() {
        /*
         * A resource or binding can change under an override without changing the measured width.
         * Recheck the declared widths after WPF finishes evaluating the dependency property.
         */
        if (_HeaderColumnWidthUpdatePending) {
            return;
        }
        _HeaderColumnWidthUpdatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new System.Action(() => {
            _HeaderColumnWidthUpdatePending = false;
            if (OperationDisplay == OperationDisplay.Responsive && HeaderColumnWidthsChanged()) {
                UpdateDisplayStage();
            }
        }));
    }

    private void ApplyHeaderColumnWidths() {
        foreach (var column in _OperationHeaderColumns) {
            ApplyHeaderColumnWidth(column);
        }
    }

    private void ApplyHeaderColumnWidth(ResponsiveHeaderColumn column) {
        if (IsHeaderColumnCollapsed(column.SharedSizeGroup, _DisplayStage)) {
            column.Override(0);
        }
        else if (_DisplayStage == OperationDisplayStage.DataInline &&
                 column.SharedSizeGroup == "Name" &&
                 column.IsPixelWidth) {
            /*
             * In the narrowest stage, give the name only the width that remains beside
             * the other columns so that root commands stay inside the viewport.
             */
            var remainingWidth = _AvailableWidth -
                (RequiredWidth(OperationDisplayStage.DataInline) - column.RequiredWidth);
            if (remainingWidth < column.RequiredWidth) {
                column.Override(System.Math.Max(0, remainingWidth));
            }
            else {
                column.Restore();
            }
        }
        else {
            column.Restore();
        }
    }

    private void SetDisplayStage(OperationDisplayStage stage) {
        if (_DisplayStage == stage) {
            return;
        }
        _DisplayStage = stage;
        IsProgressBarCollapsed = stage >= OperationDisplayStage.ProgressBarCollapsed;
        AreProgressRatesInline = stage >= OperationDisplayStage.ProgressRatesInline;
        IsProgressInline = stage >= OperationDisplayStage.ProgressInline;
        IsDataInline = stage >= OperationDisplayStage.DataInline;
    }

    private void UpdateDisplayStage() {
        if (OperationDisplay != OperationDisplay.Responsive) {
            InlineViewportWidth = double.PositiveInfinity;
            RowViewportWidth = double.PositiveInfinity;
            if (_DisplayStage != OperationDisplayStage.Full) {
                SetDisplayStage(OperationDisplayStage.Full);
                ApplyHeaderColumnWidths();
            }
            return;
        }
        var viewportWidth = _OperationListScrollViewer?.ViewportWidth ?? 0;
        if (viewportWidth <= 0) {
            return; // The list has not been measured yet.
        }
        foreach (var column in _OperationHeaderColumns) {
            column.Measure();
        }
        var reservedWidth = ReservedViewportWidth(viewportWidth);
        _OperationRowWidth = OperationRowWidth(reservedWidth);
        _AvailableWidth = System.Math.Max(0, _OperationRowWidth);
        InlineViewportWidth = reservedWidth; // Each panel limits the width further for its own row.
        RowViewportWidth = reservedWidth;
        SetDisplayStage(SelectDisplayStage(_AvailableWidth));
        ApplyHeaderColumnWidths();
    }

    internal static readonly DependencyProperty OperationCollectionProperty =
        OperationCollectionKey.DependencyProperty;

    internal static readonly DependencyProperty IsProgressBarCollapsedProperty =
        IsProgressBarCollapsedKey.DependencyProperty;

    internal static readonly DependencyProperty AreProgressRatesInlineProperty =
        AreProgressRatesInlineKey.DependencyProperty;

    internal static readonly DependencyProperty IsProgressInlineProperty =
        IsProgressInlineKey.DependencyProperty;

    internal static readonly DependencyProperty IsDataInlineProperty =
        IsDataInlineKey.DependencyProperty;

    internal static readonly DependencyProperty InlineViewportWidthProperty =
        InlineViewportWidthKey.DependencyProperty;

    internal static readonly DependencyProperty RowViewportWidthProperty =
        RowViewportWidthKey.DependencyProperty;

    internal OperationCollection OperationCollection {
        get => GetValue(OperationCollectionProperty) as OperationCollection;
        private set => SetValue(OperationCollectionKey, value);
    }

    internal bool IsProgressBarCollapsed {
        get => (bool)GetValue(IsProgressBarCollapsedProperty);
        private set => SetValue(IsProgressBarCollapsedKey, value);
    }

    internal bool AreProgressRatesInline {
        get => (bool)GetValue(AreProgressRatesInlineProperty);
        private set => SetValue(AreProgressRatesInlineKey, value);
    }

    internal bool IsProgressInline {
        get => (bool)GetValue(IsProgressInlineProperty);
        private set => SetValue(IsProgressInlineKey, value);
    }

    internal bool IsDataInline {
        get => (bool)GetValue(IsDataInlineProperty);
        private set => SetValue(IsDataInlineKey, value);
    }

    internal double InlineViewportWidth {
        get => (double)GetValue(InlineViewportWidthProperty);
        private set => SetValue(InlineViewportWidthKey, value);
    }

    internal double RowViewportWidth {
        get => (double)GetValue(RowViewportWidthProperty);
        private set => SetValue(RowViewportWidthKey, value);
    }

    internal double AvailableRowWidth(FrameworkElement element, double viewportWidth) {
        return _OperationListScrollViewer?.Content is UIElement content && content.IsAncestorOf(element)
            ? OperationRowWidth(element, content, viewportWidth)
            : viewportWidth;
    }

    /// <summary>
    /// Identifies the <see cref="Operator"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperatorProperty = DependencyProperty.Register(
        name: nameof(Operator),
        propertyType: typeof(IOperator),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null,
            propertyChangedCallback: (s, e) => {
                if (s is OperatorControl self) {
                    self.OperationCollection = e.NewValue is IOperator @operator
                        ? @operator.Operations as OperationCollection
                        : null;
                }
            }));

    /// <summary>
    /// Gets or sets the operator whose operations are displayed.
    /// </summary>
    /// <value>
    /// The operator to display, or null to clear the display.
    /// </value>
    public IOperator Operator {
        get => GetValue(OperatorProperty) as IOperator;
        set => SetValue(OperatorProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationDisplay"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationDisplayProperty = DependencyProperty.Register(
        name: nameof(OperationDisplay),
        propertyType: typeof(OperationDisplay),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: OperationDisplay.Default,
            propertyChangedCallback: (s, _) => {
                if (s is OperatorControl self) {
                    self.LayoutUpdated -= self.OperatorControl_LayoutUpdated;
                    if (self.OperationDisplay == OperationDisplay.Responsive) {
                        self.LayoutUpdated += self.OperatorControl_LayoutUpdated;
                    }
                    self.UpdateProgressBarProbe();
                    self.UpdateDisplayStage();
                }
            }),
        validateValueCallback: value =>
            value is OperationDisplay.Default or OperationDisplay.Responsive);

    /// <summary>
    /// Gets or sets how each operation's values are arranged.
    /// </summary>
    /// <value>
    /// <see cref="Controls.OperationDisplay.Default"/> to display every value in its own column, or
    /// <see cref="Controls.OperationDisplay.Responsive"/> to adapt the layout to the available width.
    /// The default is <see cref="Controls.OperationDisplay.Default"/>.
    /// </value>
    /// <remarks>
    /// In <see cref="Controls.OperationDisplay.Responsive"/> mode, the control compares the width available to
    /// a root operation row, the viewport less any horizontal inset from <see cref="OperationItemsTemplate"/>
    /// and limited by any width or maximum width set within it, with the widths of the column headers. While an
    /// automatic vertical scrollbar is hidden, its width is still reserved. A visible scrollbar's overlap is
    /// also reserved if the scroll template places it over the content. Each root row uses the resulting
    /// width budget, so operations nested more deeply do not move its commands out of view. While every column
    /// fits, the layout matches
    /// <see cref="Controls.OperationDisplay.Default"/>. When it does not fit, the control first collapses the
    /// progress bar. The progress bar requires its width, resolved from its width, minimum width, and maximum
    /// width, plus its horizontal margins; without a width or minimum width it requires 100 device-independent
    /// pixels. The control reads these from one
    /// hidden progress bar that uses <see cref="OperationProgressBarStyle"/> without an operation data context,
    /// so sizing that depends on an operation's values is not reflected. Space required by elements inside the
    /// progress bar's control template is not detected either; a template that needs a minimum width should
    /// have that width also set as <see cref="FrameworkElement.MinWidth"/> on the style. If the columns still do
    /// not fit, the rate and average values are shown beneath the other values. Next, the progress and target values
    /// are also shown beneath the other values while the data remains in its column. Finally, the data is also
    /// shown beneath the operation name, the column headers are hidden, and the name column narrows as needed
    /// so that the Cancel and Remove commands remain in the viewport. Values shown beneath the operation
    /// use the <c>OperationInline*Template</c> properties. Errors are always shown beneath the operation in this
    /// mode so that long messages can wrap.
    /// </remarks>
    public OperationDisplay OperationDisplay {
        get => (OperationDisplay)GetValue(OperationDisplayProperty);
        set => SetValue(OperationDisplayProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationItemsTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationItemsTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationItemsTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display each collection of root or child operations.
    /// </summary>
    /// <value>
    /// A collection template, or null to use the default <c>ItemsControl</c> presentation.
    /// </value>
    /// <remarks>
    /// The template's data context is the operation collection source. Bind the chosen items
    /// control's ItemsSource to that context. Operation rows use the control's built-in item template.
    /// </remarks>
    public DataTemplate OperationItemsTemplate {
        get => GetValue(OperationItemsTemplateProperty) as DataTemplate;
        set => SetValue(OperationItemsTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationErrorTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationErrorTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationErrorTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display an operation error.
    /// </summary>
    /// <value>
    /// A template whose data context is the exception, or null to use the default message presentation.
    /// </value>
    public DataTemplate OperationErrorTemplate {
        get => GetValue(OperationErrorTemplateProperty) as DataTemplate;
        set => SetValue(OperationErrorTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationProgressBarStyle"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationProgressBarStyleProperty = DependencyProperty.Register(
        name: nameof(OperationProgressBarStyle),
        propertyType: typeof(Style),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the style applied to operation progress bars.
    /// </summary>
    public Style OperationProgressBarStyle {
        get => GetValue(OperationProgressBarStyleProperty) as Style;
        set => SetValue(OperationProgressBarStyleProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationScrollViewerStyle"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationScrollViewerStyleProperty = DependencyProperty.Register(
        name: nameof(OperationScrollViewerStyle),
        propertyType: typeof(Style),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the style applied to the operation list's scroll viewer.
    /// </summary>
    public Style OperationScrollViewerStyle {
        get => GetValue(OperationScrollViewerStyleProperty) as Style;
        set => SetValue(OperationScrollViewerStyleProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationDepthHeaderTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationDepthHeaderTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationDepthHeaderTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display the depth column header.
    /// </summary>
    /// <value>
    /// A template whose data context is the header text, or null to use the default label.
    /// </value>
    public DataTemplate OperationDepthHeaderTemplate {
        get => GetValue(OperationDepthHeaderTemplateProperty) as DataTemplate;
        set => SetValue(OperationDepthHeaderTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationDepthContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationDepthContentTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationDepthContentTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display the depth value for each operation.
    /// </summary>
    /// <value>
    /// A template whose data context is the operation's depth string, or null to use the default text display.
    /// </value>
    public DataTemplate OperationDepthContentTemplate {
        get => GetValue(OperationDepthContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationDepthContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationNameHeaderTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationNameHeaderTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationNameHeaderTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display the name column header.
    /// </summary>
    /// <value>
    /// A template whose data context is the header text, or null to use the default label.
    /// </value>
    public DataTemplate OperationNameHeaderTemplate {
        get => GetValue(OperationNameHeaderTemplateProperty) as DataTemplate;
        set => SetValue(OperationNameHeaderTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationNameContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationNameContentTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationNameContentTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display each operation's name.
    /// </summary>
    /// <value>
    /// A template whose data context is the operation's name, or null to use the default text display.
    /// </value>
    public DataTemplate OperationNameContentTemplate {
        get => GetValue(OperationNameContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationNameContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationDataHeaderTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationDataHeaderTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationDataHeaderTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display the data column header.
    /// </summary>
    /// <value>
    /// A template whose data context is the header text, or null to use the default label.
    /// </value>
    public DataTemplate OperationDataHeaderTemplate {
        get => GetValue(OperationDataHeaderTemplateProperty) as DataTemplate;
        set => SetValue(OperationDataHeaderTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationDataContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationDataContentTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationDataContentTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display each operation's data.
    /// </summary>
    /// <value>
    /// A template whose data context is the operation's data, or null to use the default text display.
    /// </value>
    public DataTemplate OperationDataContentTemplate {
        get => GetValue(OperationDataContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationDataContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationProgressHeaderTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationProgressHeaderTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationProgressHeaderTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display the progress column header.
    /// </summary>
    /// <value>
    /// A template whose data context is the header text, or null to use the default label.
    /// </value>
    public DataTemplate OperationProgressHeaderTemplate {
        get => GetValue(OperationProgressHeaderTemplateProperty) as DataTemplate;
        set => SetValue(OperationProgressHeaderTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationProgressContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationProgressContentTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationProgressContentTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display each operation's progress value.
    /// </summary>
    /// <value>
    /// A template whose data context is the operation's progress string, or null to use the default text display.
    /// </value>
    public DataTemplate OperationProgressContentTemplate {
        get => GetValue(OperationProgressContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationProgressContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationProgressRateHeaderTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationProgressRateHeaderTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationProgressRateHeaderTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display the progress rate column header.
    /// </summary>
    /// <value>
    /// A template whose data context is the header text, or null to use the default label.
    /// </value>
    public DataTemplate OperationProgressRateHeaderTemplate {
        get => GetValue(OperationProgressRateHeaderTemplateProperty) as DataTemplate;
        set => SetValue(OperationProgressRateHeaderTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationProgressRateContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationProgressRateContentTemplateProperty =
        DependencyProperty.Register(
            name: nameof(OperationProgressRateContentTemplate),
            propertyType: typeof(DataTemplate),
            ownerType: typeof(OperatorControl),
            typeMetadata: new PropertyMetadata(
                defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display each operation's progress rate.
    /// </summary>
    /// <value>
    /// A template whose data context is the formatted progress-rate string, or null to use the default text
    /// display.
    /// </value>
    public DataTemplate OperationProgressRateContentTemplate {
        get => GetValue(OperationProgressRateContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationProgressRateContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationProgressRateAverageHeaderTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationProgressRateAverageHeaderTemplateProperty =
        DependencyProperty.Register(
            name: nameof(OperationProgressRateAverageHeaderTemplate),
            propertyType: typeof(DataTemplate),
            ownerType: typeof(OperatorControl),
            typeMetadata: new PropertyMetadata(
                defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display the average progress rate column header.
    /// </summary>
    /// <value>
    /// A template whose data context is the header text, or null to use the default label.
    /// </value>
    public DataTemplate OperationProgressRateAverageHeaderTemplate {
        get => GetValue(OperationProgressRateAverageHeaderTemplateProperty) as DataTemplate;
        set => SetValue(OperationProgressRateAverageHeaderTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationProgressRateAverageContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationProgressRateAverageContentTemplateProperty =
        DependencyProperty.Register(
            name: nameof(OperationProgressRateAverageContentTemplate),
            propertyType: typeof(DataTemplate),
            ownerType: typeof(OperatorControl),
            typeMetadata: new PropertyMetadata(
                defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display each operation's average progress rate.
    /// </summary>
    /// <value>
    /// A template whose data context is the formatted average progress-rate string, or null to use the default
    /// text display.
    /// </value>
    public DataTemplate OperationProgressRateAverageContentTemplate {
        get => GetValue(OperationProgressRateAverageContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationProgressRateAverageContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationTargetHeaderTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationTargetHeaderTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationTargetHeaderTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display the target column header.
    /// </summary>
    /// <value>
    /// A template whose data context is the header text, or null to use the default label.
    /// </value>
    public DataTemplate OperationTargetHeaderTemplate {
        get => GetValue(OperationTargetHeaderTemplateProperty) as DataTemplate;
        set => SetValue(OperationTargetHeaderTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationTargetContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationTargetContentTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationTargetContentTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display each operation's target value.
    /// </summary>
    /// <value>
    /// A template whose data context is the operation's target string, or null to use the default text display.
    /// </value>
    public DataTemplate OperationTargetContentTemplate {
        get => GetValue(OperationTargetContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationTargetContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationInlineHeaderTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationInlineHeaderTemplateProperty = DependencyProperty.Register(
        name: nameof(OperationInlineHeaderTemplate),
        propertyType: typeof(DataTemplate),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display the header beside each value shown beneath an operation.
    /// </summary>
    /// <value>
    /// A template whose data context is the header text, or null to use the default text display.
    /// </value>
    /// <remarks>
    /// Used when <see cref="OperationDisplay"/> is <see cref="Controls.OperationDisplay.Responsive"/> and the
    /// available width requires values to be shown beneath the operation instead of in columns.
    /// </remarks>
    public DataTemplate OperationInlineHeaderTemplate {
        get => GetValue(OperationInlineHeaderTemplateProperty) as DataTemplate;
        set => SetValue(OperationInlineHeaderTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationInlineDataContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationInlineDataContentTemplateProperty =
        DependencyProperty.Register(
            name: nameof(OperationInlineDataContentTemplate),
            propertyType: typeof(DataTemplate),
            ownerType: typeof(OperatorControl),
            typeMetadata: new PropertyMetadata(
                defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display an operation's data beneath the operation.
    /// </summary>
    /// <value>
    /// A template whose data context is the operation's data, or null to use the default wrapping text display.
    /// </value>
    /// <remarks>
    /// Used when <see cref="OperationDisplay"/> is <see cref="Controls.OperationDisplay.Responsive"/> and the
    /// available width requires data to be shown beneath the operation. The column uses
    /// <see cref="OperationDataContentTemplate"/>.
    /// </remarks>
    public DataTemplate OperationInlineDataContentTemplate {
        get => GetValue(OperationInlineDataContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationInlineDataContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationInlineProgressContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationInlineProgressContentTemplateProperty =
        DependencyProperty.Register(
            name: nameof(OperationInlineProgressContentTemplate),
            propertyType: typeof(DataTemplate),
            ownerType: typeof(OperatorControl),
            typeMetadata: new PropertyMetadata(
                defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display an operation's progress value beneath the operation.
    /// </summary>
    /// <value>
    /// A template whose data context is the operation's progress string, or null to use the default wrapping
    /// text display.
    /// </value>
    /// <remarks>
    /// Used when <see cref="OperationDisplay"/> is <see cref="Controls.OperationDisplay.Responsive"/> and the
    /// available width requires progress to be shown beneath the operation. The column uses
    /// <see cref="OperationProgressContentTemplate"/>.
    /// </remarks>
    public DataTemplate OperationInlineProgressContentTemplate {
        get => GetValue(OperationInlineProgressContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationInlineProgressContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationInlineProgressRateContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationInlineProgressRateContentTemplateProperty =
        DependencyProperty.Register(
            name: nameof(OperationInlineProgressRateContentTemplate),
            propertyType: typeof(DataTemplate),
            ownerType: typeof(OperatorControl),
            typeMetadata: new PropertyMetadata(
                defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display an operation's progress rate beneath the operation.
    /// </summary>
    /// <value>
    /// A template whose data context is the formatted progress-rate string, or null to use the default wrapping
    /// text display.
    /// </value>
    /// <remarks>
    /// Used when <see cref="OperationDisplay"/> is <see cref="Controls.OperationDisplay.Responsive"/> and the
    /// available width requires progress rates to be shown beneath the operation. The column uses
    /// <see cref="OperationProgressRateContentTemplate"/>.
    /// </remarks>
    public DataTemplate OperationInlineProgressRateContentTemplate {
        get => GetValue(OperationInlineProgressRateContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationInlineProgressRateContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationInlineProgressRateAverageContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationInlineProgressRateAverageContentTemplateProperty =
        DependencyProperty.Register(
            name: nameof(OperationInlineProgressRateAverageContentTemplate),
            propertyType: typeof(DataTemplate),
            ownerType: typeof(OperatorControl),
            typeMetadata: new PropertyMetadata(
                defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display an operation's average progress rate beneath the operation.
    /// </summary>
    /// <value>
    /// A template whose data context is the formatted average progress-rate string, or null to use the default
    /// wrapping text display.
    /// </value>
    /// <remarks>
    /// Used when <see cref="OperationDisplay"/> is <see cref="Controls.OperationDisplay.Responsive"/> and the
    /// available width requires progress rates to be shown beneath the operation. The column uses
    /// <see cref="OperationProgressRateAverageContentTemplate"/>.
    /// </remarks>
    public DataTemplate OperationInlineProgressRateAverageContentTemplate {
        get => GetValue(OperationInlineProgressRateAverageContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationInlineProgressRateAverageContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationInlineTargetContentTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationInlineTargetContentTemplateProperty =
        DependencyProperty.Register(
            name: nameof(OperationInlineTargetContentTemplate),
            propertyType: typeof(DataTemplate),
            ownerType: typeof(OperatorControl),
            typeMetadata: new PropertyMetadata(
                defaultValue: null));

    /// <summary>
    /// Gets or sets the template used to display an operation's target value beneath the operation.
    /// </summary>
    /// <value>
    /// A template whose data context is the operation's target string, or null to use the default wrapping text
    /// display.
    /// </value>
    /// <remarks>
    /// Used when <see cref="OperationDisplay"/> is <see cref="Controls.OperationDisplay.Responsive"/> and the
    /// available width requires the target to be shown beneath the operation. The column uses
    /// <see cref="OperationTargetContentTemplate"/>.
    /// </remarks>
    public DataTemplate OperationInlineTargetContentTemplate {
        get => GetValue(OperationInlineTargetContentTemplateProperty) as DataTemplate;
        set => SetValue(OperationInlineTargetContentTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationCancelButtonStyle"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationCancelButtonStyleProperty = DependencyProperty.Register(
        name: nameof(OperationCancelButtonStyle),
        propertyType: typeof(Style),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the style applied to the Cancel button for running root operations.
    /// </summary>
    /// <value>
    /// A style targeting <see cref="System.Windows.Controls.Button"/>, or null to use the default Cancel button style.
    /// </value>
    /// <remarks>
    /// The default style supplies the button's content. Set Content in a custom style to specify
    /// the button's label or other content. The control supplies the cancellation command.
    /// </remarks>
    public Style OperationCancelButtonStyle {
        get => GetValue(OperationCancelButtonStyleProperty) as Style;
        set => SetValue(OperationCancelButtonStyleProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="OperationRemoveButtonStyle"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty OperationRemoveButtonStyleProperty = DependencyProperty.Register(
        name: nameof(OperationRemoveButtonStyle),
        propertyType: typeof(Style),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    /// <summary>
    /// Gets or sets the style applied to the Remove button for root operations that are no longer progressing.
    /// </summary>
    /// <value>
    /// A style targeting <see cref="System.Windows.Controls.Button"/>, or null to use the default Remove button style.
    /// </value>
    /// <remarks>
    /// The default style supplies the button's content. Set Content in a custom style to specify
    /// the button's label or other content. The control supplies the removal command.
    /// </remarks>
    public Style OperationRemoveButtonStyle {
        get => GetValue(OperationRemoveButtonStyleProperty) as Style;
        set => SetValue(OperationRemoveButtonStyleProperty, value);
    }

    /// <summary>
    /// Initializes a new operation display control.
    /// </summary>
    public OperatorControl() {
    }

    /// <inheritdoc/>
    public override void OnApplyTemplate() {
        if (_OperationListScrollViewer is not null) {
            _OperationListScrollViewer.ScrollChanged -= OperationListScrollViewer_ScrollChanged;
        }
        foreach (var column in _OperationHeaderColumns) {
            column.Restore();
        }
        base.OnApplyTemplate();
        _OperationHeaderGrid = GetTemplateChild(OperationHeaderGridTemplatePartName) as Grid;
        var columns = _OperationHeaderGrid?.ColumnDefinitions;
        _OperationHeaderColumns = new ResponsiveHeaderColumn[columns?.Count ?? 0];
        for (var i = 0; i < _OperationHeaderColumns.Length; i++) {
            _OperationHeaderColumns[i] = new ResponsiveHeaderColumn(columns[i], QueueHeaderColumnWidthUpdate);
        }
        if (_ProgressBarProbeHost is not null) {
            _ProgressBarProbeHost.Child = null;
        }
        _ProgressBarProbeHost = GetTemplateChild(OperationProgressBarProbeHostTemplatePartName) as Decorator;
        UpdateProgressBarProbe();
        _OperationHeaderTransform = null;
        _OperationListScrollViewer = GetTemplateChild(OperationListScrollViewerTemplatePartName) as ScrollViewer;
        if (_OperationListScrollViewer is not null) {
            if (_OperationHeaderGrid is not null) {
                _OperationHeaderTransform = new TranslateTransform {
                    X = -_OperationListScrollViewer.HorizontalOffset
                };
                _OperationHeaderGrid.RenderTransform = _OperationHeaderTransform;
                BindingOperations.SetBinding(
                    _OperationHeaderGrid,
                    FrameworkElement.MinWidthProperty,
                    new Binding(nameof(ScrollViewer.ExtentWidth)) {
                        Source = _OperationListScrollViewer
                    });
            }
            _OperationListScrollViewer.ScrollChanged += OperationListScrollViewer_ScrollChanged;
        }
        UpdateDisplayStage();
    }
}

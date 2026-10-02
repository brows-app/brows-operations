using Brows.Operations;
using System.Windows;

namespace Brows.Windows.Controls;

/// <summary>
/// Displays operations from an <see cref="IOperator"/> as a hierarchy.
/// </summary>
/// <remarks>
/// Supports the built-in operators created by <c>IOperatorFactory</c>. Relevant operations and
/// their ancestor paths are displayed with progress, errors, and cancellation or removal commands.
/// Access the control and start operations on the WPF dispatcher so Operate captures its
/// synchronization context for root and child collection changes. Progress reports may run on
/// worker threads; property-change notifications are not dispatched by the operations library.
/// </remarks>
sealed partial class OperatorControl {
    static OperatorControl() {
        DefaultStyleKeyProperty.OverrideMetadata(
            forType: typeof(OperatorControl),
            typeMetadata: new FrameworkPropertyMetadata(typeof(OperatorControl)));
    }

    private static readonly DependencyPropertyKey OperationCollectionKey = DependencyProperty.RegisterReadOnly(
        name: nameof(OperationCollection),
        propertyType: typeof(OperationCollection),
        ownerType: typeof(OperatorControl),
        typeMetadata: new PropertyMetadata(
            defaultValue: null));

    internal static readonly DependencyProperty OperationCollectionProperty =
        OperationCollectionKey.DependencyProperty;

    internal OperationCollection OperationCollection {
        get => GetValue(OperationCollectionProperty) as OperationCollection;
        private set => SetValue(OperationCollectionKey, value);
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
        InitializeComponent();
    }
}

namespace Brows.Windows.Controls;

/// <summary>
/// Specifies how an <see cref="OperatorControl"/> arranges each operation's values.
/// </summary>
public enum OperationDisplay {
    /// <summary>
    /// Displays every value in its own column and scrolls horizontally when the columns do not fit.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Displays every value in its own column when the columns fit. When they do not fit, collapses the
    /// progress bar, then shows the progress rates beneath the other values, then shows the progress and
    /// target values beneath them, and finally shows the data beneath the operation name as well.
    /// </summary>
    Responsive = 1
}

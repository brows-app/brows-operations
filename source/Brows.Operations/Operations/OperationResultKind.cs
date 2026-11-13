namespace Brows.Operations;

/// <summary>
/// Describes the terminal work outcome of an operation.
/// </summary>
public enum OperationResultKind {
    /// <summary>
    /// The operation and its registered descendants completed without failure or cancellation.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The operation or a descendant failed with an ordinary exception.
    /// </summary>
    Failed,

    /// <summary>
    /// The operation or a descendant was canceled without an ordinary failure.
    /// </summary>
    Canceled
}

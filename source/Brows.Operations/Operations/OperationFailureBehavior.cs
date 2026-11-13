namespace Brows.Operations;

/// <summary>
/// Controls how child work failures are reported to the caller.
/// </summary>
public enum OperationFailureBehavior {
    /// <summary>
    /// Returns the child's terminal outcome while preserving failures on the operation tree.
    /// </summary>
    Record,

    /// <summary>
    /// Propagates work failures after the child and its descendants finish.
    /// </summary>
    Throw
}

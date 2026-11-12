namespace Brows.Operations;

/// <summary>Specifies how numeric progress, target, and rate values are displayed.</summary>
public enum OperationProgressKind {
    /// <summary>Displays values using the caller-defined unit without scaling.</summary>
    None = 0,

    /// <summary>Displays byte counts using binary file-size units.</summary>
    FileSize
}

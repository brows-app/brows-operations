namespace Brows.Operations;

/// <summary>Describes a child operation to be started by <see cref="IOperationProgress.Children{T}"/>.</summary>
public sealed class OperationChild {
    /// <summary>Gets the child's initial display name.</summary>
    public string Name { get; }

    /// <summary>Gets the delegate that performs the child's work.</summary>
    public OperationDelegate Task { get; }

    /// <summary>Initializes a child operation description.</summary>
    /// <param name="name">The child's initial display name.</param>
    /// <param name="task">The child's work delegate; it must be non-null when the child is started.</param>
    /// <remarks>This constructor stores the supplied values; delegate validation occurs when the child is started.</remarks>
    public OperationChild(string name, OperationDelegate task) {
        Name = name;
        Task = task;
    }
}

using System.Collections.Generic;

namespace Brows.Operations;

/// <summary>Provides access to the root operations tracked by an operator.</summary>
/// <remarks>
/// Root additions, removals, and notifications run on the operator's synchronization context; see
/// <see cref="IOperator"/>. <see cref="Count"/> and <see cref="Snapshot"/> can be used from any thread.
/// </remarks>
public interface IOperationCollection {
    /// <summary>Gets the number of root operations currently in the collection.</summary>
    int Count { get; }

    /// <summary>Returns a stable copy of the root operations currently in the collection.</summary>
    /// <returns>A list whose membership does not change when roots are added or removed.</returns>
    IReadOnlyList<IOperation> Snapshot();

    /// <summary>Attempts to remove a completed root operation from the collection.</summary>
    /// <param name="item">The operation to remove.</param>
    /// <returns>
    /// <see langword="true"/> if the operation was removed; <see langword="false"/> if it is null,
    /// still running, or not in this collection.
    /// </returns>
    /// <remarks>
    /// Removal runs synchronously on the operator's context, sending to it when called from another thread.
    /// </remarks>
    bool Remove(IOperation item);

    /// <summary>
    /// Removes completed root operations from the collection.
    /// </summary>
    /// <param name="withError">
    /// <see langword="true"/> to remove only operations completed with an error;
    /// <see langword="false"/> to remove only operations completed without an error;
    /// or <see langword="null"/> to remove all completed operations.
    /// </param>
    /// <returns>The number of operations removed.</returns>
    /// <remarks>
    /// Selection and removal run synchronously on the operator's context, sending to it when called
    /// from another thread.
    /// </remarks>
    int RemoveComplete(bool? withError = null);
}

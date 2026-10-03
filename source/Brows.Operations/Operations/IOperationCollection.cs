using System.Collections.Generic;

namespace Brows.Operations;

/// <summary>Provides access to the root operations tracked by an operator.</summary>
/// <remarks>
/// Root membership changes synchronously, while changes to the observable source are posted
/// asynchronously to the context captured when the operator was created. With no captured context,
/// observable changes run on the thread making each change. Property-change notifications run on
/// the thread updating state. <see cref="Snapshot"/> can be enumerated while roots are added or
/// removed.
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
    /// Membership is updated before this method returns. The observable removal is posted
    /// asynchronously to the context captured by the operator, or runs on the calling thread when
    /// no context was captured.
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
    /// Membership changes before this method returns. Observable removals are posted asynchronously
    /// to the context captured by the operator, or run on the calling thread when no context was
    /// captured. Completed operations are selected from a snapshot of the collection.
    /// </remarks>
    int RemoveComplete(bool? withError = null);
}

using System.Collections;
using System.Collections.Generic;

namespace Brows.Operations;

/// <summary>Provides access to the root operations tracked by an operator.</summary>
/// <remarks>
/// Root additions and removals use the synchronization context captured by each root's Operate call.
/// Without a captured context, mutations run on the calling thread. Property-change notifications
/// are not dispatched. Enumerate live views on the owning UI context and avoid concurrent mutations.
/// </remarks>
public interface IOperationCollection {
    /// <summary>Gets the number of root operations currently in the collection.</summary>
    int Count { get; }

    /// <summary>Gets the live collection source for enumeration and UI binding.</summary>
    /// <remarks>
    /// The built-in implementation provides a read-only observable collection.
    /// Use <see cref="Remove"/> to remove completed operations.
    /// </remarks>
    IEnumerable Source { get; }

    /// <summary>Returns an enumerator over the root operations currently in the collection.</summary>
    /// <returns>An enumerator over the collection's operations.</returns>
    IEnumerator<IOperation> GetEnumerator();

    /// <summary>Returns an enumerable view of the root operations.</summary>
    /// <returns>A live view of the collection, rather than a snapshot.</returns>
    IEnumerable<IOperation> AsEnumerable();

    /// <summary>Attempts to remove a completed root operation from the collection.</summary>
    /// <param name="item">The operation to remove.</param>
    /// <returns>
    /// <see langword="true"/> if the operation was removed; <see langword="false"/> if it is null,
    /// still running, or not in this collection.
    /// </returns>
    /// <remarks>Removal synchronously dispatches the collection mutation to the operation's captured context.</remarks>
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
    /// Each removal synchronously dispatches the collection mutation to the operation's captured context.
    /// The selection of completed operations enumerates the live collection on the calling thread;
    /// avoid concurrent additions or removals during selection.
    /// </remarks>
    int RemoveComplete(bool? withError = null);
}

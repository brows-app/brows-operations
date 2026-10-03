namespace Brows.Operations;

/// <summary>Starts and tracks asynchronous work as a hierarchy of operations.</summary>
/// <remarks>
/// An operator is affine to the synchronization context that was current when it was created.
/// Operation state changes, property-change notifications, events, and observable collection changes
/// all run on that context. <see cref="Operate"/>, <see cref="IOperationProgress"/> members, and
/// cancellation can be called from any thread; calls from other threads are posted to the context.
/// Progress reports posted from other threads are coalesced per operation. Read
/// <see cref="IOperation"/> state on the operator's context; only
/// <see cref="IOperationCollection.Snapshot"/> and <see cref="IOperationCollection.Count"/> are safe
/// to call from any thread. The operator must not outlive its context: work posted after a WPF
/// dispatcher shuts down is dropped. When no context is current at creation, changes run on the
/// calling thread under a single operator lock, and notifications are raised while that lock is held.
/// </remarks>
public interface IOperator {
    /// <summary>Gets the collection of tracked root operations.</summary>
    /// <remarks>Successful roots are removed automatically; roots with errors remain until removed.</remarks>
    IOperationCollection Operations { get; }

    /// <summary>Starts a root operation with the supplied name and delegate.</summary>
    /// <param name="name">The display name of the operation.</param>
    /// <param name="task">The asynchronous work to perform.</param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is null.</exception>
    /// <remarks>
    /// This method does not await completion. On the operator's context, the operation is added and
    /// its delegate starts immediately and may finish synchronously. From another thread, the
    /// operation is added on the operator's context and its delegate then starts on the thread pool.
    /// Delegate errors are recorded on the operation, whose lifetime includes all
    /// registered descendants. Requested cancellation is treated as completion without an error
    /// unless the delegate or a descendant records another failure.
    /// </remarks>
    void Operate(string name, OperationDelegate task);
}
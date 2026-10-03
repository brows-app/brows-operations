namespace Brows.Operations;

/// <summary>Starts and tracks asynchronous work as a hierarchy of operations.</summary>
/// <remarks>
/// The operator captures a synchronization context when it is created and uses it for observable
/// collection changes in every operation and descendant. Progress reports, child registration,
/// and property-change notifications may run on other threads. WPF bindings marshal property
/// updates to their dispatcher.
/// </remarks>
public interface IOperator {
    /// <summary>Gets the collection of tracked root operations.</summary>
    /// <remarks>Successful roots are removed automatically; roots with errors remain until removed.</remarks>
    IOperationCollection Operations { get; }

    /// <summary>Starts a root operation with the supplied name and delegate.</summary>
    /// <param name="name">The display name of the operation.</param>
    /// <param name="task">The asynchronous work to perform.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="task"/> is null.
    /// </exception>
    /// <remarks>
    /// The delegate starts immediately and may finish synchronously. This method does not await
    /// completion. Root membership changes immediately. With a captured context, observable root
    /// changes are posted asynchronously to that context. Without one, the producer that starts a
    /// serialized queue drain projects changes synchronously; concurrent or reentrant producers can
    /// return before their queued change is projected. See <see cref="IOperationCollection"/> for
    /// observable-delivery details. Progress state changes are synchronized by the operation tree,
    /// though property-change notifications run on the thread making each update. This method does
    /// not change the delegate's execution context or normal await context capture.
    /// Delegate errors are recorded on the operation, whose lifetime includes all
    /// registered descendants. Requested cancellation is treated as completion without an error
    /// unless the delegate or a descendant records another failure.
    /// </remarks>
    void Operate(string name, OperationDelegate task);
}

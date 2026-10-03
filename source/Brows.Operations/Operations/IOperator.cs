namespace Brows.Operations;

/// <summary>Starts and tracks asynchronous work as a hierarchy of operations.</summary>
/// <remarks>
/// By default, each Operate call captures the current synchronization context for observable
/// collection changes in that operation and its descendants. Context capture can be disabled
/// per call with synchronizeWithCurrentContext. Progress reports and property-change
/// notifications may run on other threads; callers must still serialize operation state updates.
/// </remarks>
public interface IOperator {
    /// <summary>Gets the collection of tracked root operations.</summary>
    /// <remarks>Successful roots are removed automatically; roots with errors remain until removed.</remarks>
    IOperationCollection Operations { get; }

    /// <summary>Starts a root operation with the supplied name and delegate.</summary>
    /// <param name="name">The display name of the operation.</param>
    /// <param name="task">The asynchronous work to perform.</param>
    /// <param name="synchronizeWithCurrentContext">
    /// <see langword="true"/> (the default) to capture the current synchronization context for
    /// observable collection changes in the root and all descendants; <see langword="false"/>
    /// to perform those changes directly on the thread making each change.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is null.</exception>
    /// <remarks>
    /// The delegate starts immediately and may finish synchronously. This method does not await
    /// completion. When synchronization is enabled, the current synchronization context is captured
    /// for root and child collection additions and root removals. Changes from another context use
    /// synchronous dispatch. When synchronization is disabled or no context is available, collection
    /// changes run on the thread making each change. This option does not change the delegate's
    /// execution context or normal await context capture. Property-change notifications are not
    /// dispatched. When binding to a UI, call this method on that UI's context and keep
    /// synchronization enabled.
    /// Delegate errors are recorded on the operation, whose lifetime includes all
    /// registered descendants. Requested cancellation is treated as completion without an error
    /// unless the delegate or a descendant records another failure.
    /// </remarks>
    void Operate(string name, OperationDelegate task, bool synchronizeWithCurrentContext = true);
}

namespace Brows.Operations;

/// <summary>Starts and tracks asynchronous work as a hierarchy of operations.</summary>
/// <remarks>
/// Start operations on a Windows UI synchronization context and keep progress reports and
/// continuations on that context. The implementation expects sequential access.
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
    /// The delegate starts immediately and may finish synchronously. This method does not await
    /// completion. Delegate errors are recorded on the operation, whose lifetime includes all
    /// registered descendants. Requested cancellation is treated as completion without an error
    /// unless the delegate or a descendant records another failure.
    /// </remarks>
    void Operate(string name, OperationDelegate task);
}

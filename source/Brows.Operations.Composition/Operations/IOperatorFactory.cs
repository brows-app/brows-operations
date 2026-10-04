using Brows.Composition;
using System.Threading;

namespace Brows.Operations;

/// <summary>Creates operators through Brows.Composition.</summary>
public interface IOperatorFactory : IExport {
    /// <summary>Creates an operator and captures the current synchronization context.</summary>
    /// <returns>A new operator for starting and tracking operations.</returns>
    /// <remarks>
    /// In a WPF application, call this method on the dispatcher thread so observable collection
    /// changes are posted back to that dispatcher.
    /// </remarks>
    IOperator Create();

    /// <summary>Creates an operator bound to the supplied synchronization context.</summary>
    /// <param name="synchronizationContext">
    /// The context used for observable collection changes, typically a WPF dispatcher context.
    /// </param>
    /// <returns>A new operator for starting and tracking operations.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="synchronizationContext"/> is null.
    /// </exception>
    /// <remarks>
    /// Collection changes are posted asynchronously to this context. The operator does not change
    /// the execution context of operation delegates.
    /// </remarks>
    IOperator Create(SynchronizationContext synchronizationContext);
}

using Brows.Composition;
using System.Threading;

namespace Brows.Operations;

/// <summary>Creates operators through Brows.Composition.</summary>
public interface IOperatorFactory : IExport {
    /// <summary>Creates an operator and captures the current synchronization context.</summary>
    /// <returns>A new operator for starting and tracking operations.</returns>
    IOperator Create();

    /// <summary>Creates an operator bound to the supplied synchronization context.</summary>
    /// <param name="synchronizationContext">
    /// The UI context used for observable collection changes.
    /// </param>
    /// <returns>A new operator for starting and tracking operations.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="synchronizationContext"/> is null.
    /// </exception>
    IOperator Create(SynchronizationContext synchronizationContext);
}

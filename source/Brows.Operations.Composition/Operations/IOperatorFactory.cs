using Brows.Composition;

namespace Brows.Operations;

/// <summary>Creates operators through Brows.Composition.</summary>
public interface IOperatorFactory : IExport {
    /// <summary>Creates an operator with its own operation collection.</summary>
    /// <returns>A new operator for starting and tracking operations.</returns>
    /// <remarks>
    /// The operator captures the synchronization context current when this method runs. Its state
    /// changes and notifications run on that context. For UI binding, call this method on the UI thread.
    /// See <see cref="IOperator"/> for the threading contract.
    /// </remarks>
    IOperator Create();
}

using Brows.Composition;

namespace Brows.Operations;

/// <summary>Creates operators through Brows.Composition.</summary>
public interface IOperatorFactory : IExport {
    /// <summary>Creates an operator with its own operation collection.</summary>
    /// <returns>A new operator for starting and tracking operations.</returns>
    /// <remarks>
    /// Creation does not capture a synchronization context. By default, each call to Operate captures
    /// the current context for observable collection mutations. Passing synchronizeWithCurrentContext
    /// as false disables capture for that root and its descendants. Progress and property-change
    /// notifications may run on worker threads. For UI binding, call Operate on the UI's
    /// synchronization context and keep synchronization enabled.
    /// </remarks>
    IOperator Create();
}

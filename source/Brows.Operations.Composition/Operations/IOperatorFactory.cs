using Brows.Composition;

namespace Brows.Operations;

/// <summary>Creates operators through Brows.Composition.</summary>
public interface IOperatorFactory : IExport {
    /// <summary>Creates an operator with its own operation collection.</summary>
    /// <returns>A new operator for starting and tracking operations.</returns>
    /// <remarks>Use the returned operator and its progress reporters on a Windows UI synchronization context.</remarks>
    IOperator Create();
}

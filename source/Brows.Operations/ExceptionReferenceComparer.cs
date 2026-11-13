using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Brows;

internal sealed class ExceptionReferenceComparer : IEqualityComparer<Exception> {
    public static ExceptionReferenceComparer Instance { get; } = new();

    public bool Equals(Exception x, Exception y) =>
        ReferenceEquals(x, y);

    public int GetHashCode(Exception obj) =>
        RuntimeHelpers.GetHashCode(obj);
}

using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace Brows.Operations;

internal sealed class OperationExceptionRecord {
    public ExceptionDispatchInfo Failure { get; init; }
    public CancellationToken CancellationToken { get; init; }
    public bool IsCancellation { get; init; }
    public bool IsInfrastructure { get; init; }
    public IReadOnlyList<ExceptionDispatchInfo> AggregateFailures { get; init; }
    public IReadOnlyList<ExceptionDispatchInfo> AggregateInfrastructure { get; init; }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;

namespace Brows.Operations;

internal sealed class OperationSummary {
    private static ReadOnlyCollection<T> Snapshot<T>(IEnumerable<T> values) =>
        new([.. values]);

    internal IReadOnlyList<ExceptionDispatchInfo> OperationFailures { get; }

    internal IReadOnlyList<ExceptionDispatchInfo> InfrastructureFailures { get; }

    internal bool IsCanceled { get; }

    internal ExceptionDispatchInfo Cancellation { get; }

    internal Exception CancellationException =>
        Cancellation?.SourceException;

    internal CancellationToken CancellationToken { get; }

    internal bool IsAmbientCanceled { get; }

    internal CancellationToken AmbientCancellationToken { get; }

    internal bool OperationCancellationRequested { get; }

    internal CancellationToken OperationCancellationToken { get; }

    internal OperationSummary(IEnumerable<ExceptionDispatchInfo> failures,
                              IEnumerable<ExceptionDispatchInfo> infrastructureFailures,
                              bool isCanceled,
                              ExceptionDispatchInfo cancellation,
                              CancellationToken cancellationToken,
                              bool isAmbientCanceled,
                              CancellationToken ambientCancellationToken,
                              bool operationCancellationRequested,
                              CancellationToken operationCancellationToken) {
        OperationFailures = Snapshot(failures);
        InfrastructureFailures = Snapshot(infrastructureFailures);
        IsCanceled = isCanceled;
        Cancellation = cancellation;
        CancellationToken = cancellationToken;
        IsAmbientCanceled = isAmbientCanceled;
        AmbientCancellationToken = ambientCancellationToken;
        OperationCancellationRequested = operationCancellationRequested;
        OperationCancellationToken = operationCancellationToken;
    }
}

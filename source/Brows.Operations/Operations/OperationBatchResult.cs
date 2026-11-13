using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Brows.Operations;

/// <summary>
/// Contains source-ordered outcomes for the children started by a batch.
/// </summary>
public sealed class OperationBatchResult {
    private readonly ReadOnlyCollection<OperationResult> Snapshot;

    private static bool IsDefined(OperationResultKind resultKind) =>
        resultKind is OperationResultKind.Succeeded or 
                      OperationResultKind.Failed or 
                      OperationResultKind.Canceled;

    /// <summary>
    /// Gets the terminal results in source order.
    /// </summary>
    public IReadOnlyList<OperationResult> Results => Snapshot;

    /// <summary>
    /// Gets the number of successful child outcomes.
    /// </summary>
    public int SucceededCount { get; }

    /// <summary>
    /// Gets the number of failed child outcomes.
    /// </summary>
    public int FailedCount { get; }

    /// <summary>
    /// Gets the number of canceled child outcomes.
    /// </summary>
    public int CanceledCount { get; }

    /// <summary>
    /// Creates a batch result from a sequence of child outcomes.
    /// </summary>
    /// <param name="results">
    /// The outcomes to copy into this result.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="results"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The sequence contains a null result or a result with an undefined outcome.
    /// </exception>
    public OperationBatchResult(IEnumerable<OperationResult> results) {
        if (results is null) {
            throw new ArgumentNullException(nameof(results));
        }

        var snapshot = new List<OperationResult>();
        var succeededCount = 0;
        var failedCount = 0;
        var canceledCount = 0;
        foreach (var result in results) {
            if (result is null) {
                throw new ArgumentException("A batch result cannot contain a null operation result.", nameof(results));
            }
            if (!IsDefined(result.Kind)) {
                throw new ArgumentException("A batch result contains an undefined operation result kind.", nameof(results));
            }
            snapshot.Add(result);
            switch (result.Kind) {
                case OperationResultKind.Succeeded:
                    succeededCount++;
                    break;
                case OperationResultKind.Failed:
                    failedCount++;
                    break;
                case OperationResultKind.Canceled:
                    canceledCount++;
                    break;
            }
        }

        Snapshot = snapshot.AsReadOnly();
        SucceededCount = succeededCount;
        FailedCount = failedCount;
        CanceledCount = canceledCount;
    }
}

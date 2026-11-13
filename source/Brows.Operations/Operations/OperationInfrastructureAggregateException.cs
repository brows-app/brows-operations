using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace Brows.Operations;

internal sealed class OperationInfrastructureAggregateException : AggregateException {
    private OperationInfrastructureAggregateException(IReadOnlyList<ExceptionDispatchInfo> failures)
    : base("Multiple operation infrastructure notifications failed.", failures.Select(failure => failure.SourceException)) {
        Failures = new ReadOnlyCollection<ExceptionDispatchInfo>([.. failures]);
    }

    private static void AddDistinct(List<ExceptionDispatchInfo> failures,
                                    HashSet<Exception> seen,
                                    ExceptionDispatchInfo failure) {
        if (failure.SourceException is OperationInfrastructureAggregateException aggregate) {
            foreach (var nested in aggregate.Failures) {
                AddDistinct(failures, seen, nested);
            }
        }
        else if (seen.Add(failure.SourceException)) {
            failures.Add(failure);
        }
    }

    private static ExceptionDispatchInfo Freeze(List<ExceptionDispatchInfo> failures) =>
        failures.Count == 1
            ? failures[0]
            : ExceptionDispatchInfo.Capture(new OperationInfrastructureAggregateException(failures));

    internal IReadOnlyList<ExceptionDispatchInfo> Failures { get; }

    internal static ExceptionDispatchInfo Combine(ExceptionDispatchInfo first,
                                                  ExceptionDispatchInfo second) {
        var failures = new List<ExceptionDispatchInfo>();
        var seen = new HashSet<Exception>(ExceptionReferenceComparer.Instance);
        AddDistinct(failures, seen, first);
        AddDistinct(failures, seen, second);
        return Freeze(failures);
    }

    internal static ExceptionDispatchInfo Combine(ExceptionDispatchInfo first,
                                                  IEnumerable<ExceptionDispatchInfo> additional) {
        var failures = new List<ExceptionDispatchInfo>();
        var seen = new HashSet<Exception>(ExceptionReferenceComparer.Instance);
        AddDistinct(failures, seen, first);
        foreach (var failure in additional) {
            AddDistinct(failures, seen, failure);
        }
        return Freeze(failures);
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Brows.Operations;

internal sealed class OperationTreeState {
    private readonly
#if NET9_0_OR_GREATER
        Lock
#else
        object
#endif
        Gate = new();

    private readonly Dictionary<Exception, OperationExceptionRecord> Entries =
        new(ExceptionReferenceComparer.Instance);

    private readonly HashSet<CancellationToken> KnownTokens = [];

    private void Add(Exception exception, OperationExceptionRecord provenance) {
        lock (Gate) {
#if NET
            _ = Entries.TryAdd(exception, provenance);
#else
            if (Entries.ContainsKey(exception) != true) {
                Entries.Add(exception, provenance);
            }
#endif
        }
    }

    internal void AddKnownToken(CancellationToken token) {
        if (!token.CanBeCanceled) {
            return;
        }
        lock (Gate) {
            _ = KnownTokens.Add(token);
        }
    }

    internal bool IsKnownCanceledToken(CancellationToken token) {
        if (!token.IsCancellationRequested) {
            return false;
        }
        lock (Gate) {
            return KnownTokens.Contains(token);
        }
    }

    internal bool IsInfrastructure(Exception exception) {
        lock (Gate) {
            return Entries.TryGetValue(exception, out var provenance) && provenance.IsInfrastructure;
        }
    }

    internal bool TryGetWorkFailure(Exception exception, out ExceptionDispatchInfo failure) {
        lock (Gate) {
            if (Entries.TryGetValue(exception, out var provenance) &&
                !provenance.IsInfrastructure && !provenance.IsCancellation && provenance.Failure is not null) {
                failure = provenance.Failure;
                return true;
            }
        }
        failure = null;
        return false;
    }

    internal bool TryGetCancellation(Exception exception,
                                     out ExceptionDispatchInfo cancellation,
                                     out CancellationToken token) {
        lock (Gate) {
            if (Entries.TryGetValue(exception, out var provenance) && provenance.IsCancellation) {
                cancellation = provenance.Failure;
                token = provenance.CancellationToken;
                return true;
            }
        }
        cancellation = null;
        token = default;
        return false;
    }

    internal bool TryExpandWorkAggregate(Exception exception, out IReadOnlyList<ExceptionDispatchInfo> failures) {
        lock (Gate) {
            if (Entries.TryGetValue(exception, out var provenance) && provenance.AggregateFailures is not null) {
                failures = provenance.AggregateFailures;
                return true;
            }
        }
        failures = null;
        return false;
    }

    internal bool TryExpandInfrastructureAggregate(
        Exception exception,
        out IReadOnlyList<ExceptionDispatchInfo> failures) {
        lock (Gate) {
            if (Entries.TryGetValue(exception, out var provenance) && provenance.AggregateInfrastructure is not null) {
                failures = provenance.AggregateInfrastructure;
                return true;
            }
        }
        failures = null;
        return false;
    }

    internal void MarkWorkFailure(ExceptionDispatchInfo failure) =>
        Add(failure.SourceException, new OperationExceptionRecord { Failure = failure });

    internal void MarkCancellation(ExceptionDispatchInfo cancellation, CancellationToken token) =>
        Add(cancellation.SourceException, new OperationExceptionRecord {
            Failure = cancellation,
            IsCancellation = true,
            CancellationToken = token
        });

    internal void MarkInfrastructure(ExceptionDispatchInfo failure) {
        if (failure.SourceException is OperationInfrastructureAggregateException aggregate) {
            foreach (var item in aggregate.Failures) {
                MarkInfrastructure(item);
            }
            lock (Gate) {
                if (Entries.TryGetValue(failure.SourceException, out var existing) &&
                    existing.IsInfrastructure && existing.AggregateInfrastructure is not null) {
                    return;
                }
                Entries[failure.SourceException] = new OperationExceptionRecord {
                    Failure = failure,
                    IsInfrastructure = true,
                    AggregateInfrastructure = aggregate.Failures
                };
            }
            return;
        }

        lock (Gate) {
            if (Entries.TryGetValue(failure.SourceException, out var existing) && existing.IsInfrastructure) {
                return;
            }
            Entries[failure.SourceException] = new OperationExceptionRecord {
                Failure = failure,
                IsInfrastructure = true
            };
        }
    }

    internal void MarkLibraryWorkAggregate(AggregateException aggregate,
                                           IReadOnlyList<ExceptionDispatchInfo> failures) =>
        Add(aggregate, new OperationExceptionRecord { AggregateFailures = failures });

    internal void MarkLibraryInfrastructureAggregate(AggregateException aggregate,
                                                     IReadOnlyList<ExceptionDispatchInfo> failures) {
        lock (Gate) {
            Entries[aggregate] = new OperationExceptionRecord {
                AggregateInfrastructure = failures,
                IsInfrastructure = true
            };
        }
    }
}

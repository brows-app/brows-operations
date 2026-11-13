using Brows.Shims;
using Domore.Logs;
using Domore.Notification;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using TASK = System.Threading.Tasks.Task;

namespace Brows.Operations;

internal class OperationBase : Notifier, IOperation {
    private static readonly ILog Log = Logging.For(typeof(OperationBase));
    private static readonly PropertyChangedEventArgs CancelingEvent = new(nameof(Canceling));
    private static readonly PropertyChangedEventArgs CompleteEvent = new(nameof(Complete));
    private static readonly PropertyChangedEventArgs CompleteWithErrorEvent = new(nameof(CompleteWithError));
    private static readonly PropertyChangedEventArgs DataEvent = new(nameof(Data));
    private static readonly PropertyChangedEventArgs ErrorEvent = new(nameof(Error));
    private static readonly PropertyChangedEventArgs NameEvent = new(nameof(Name));
    private static readonly PropertyChangedEventArgs ProgressEvent = new(nameof(Progress));
    private static readonly PropertyChangedEventArgs ProgressingEvent = new(nameof(Progressing));
    private static readonly PropertyChangedEventArgs ProgressPercentEvent = new(nameof(ProgressPercent));
    private static readonly PropertyChangedEventArgs ProgressRateEvent = new(nameof(ProgressRate));
    private static readonly PropertyChangedEventArgs ProgressRateAverageEvent = new(nameof(ProgressRateAverage));
    private static readonly PropertyChangedEventArgs ProgressRateStringEvent = new(nameof(ProgressRateString));
    private static readonly PropertyChangedEventArgs ProgressRateAverageStringEvent =
        new(nameof(ProgressRateAverageString));
    private static readonly PropertyChangedEventArgs ProgressStringEvent = new(nameof(ProgressString));
    private static readonly PropertyChangedEventArgs ProgressKindEvent = new(nameof(ProgressKind));
    private static readonly PropertyChangedEventArgs RelevantEvent = new(nameof(Relevant));
    private static readonly PropertyChangedEventArgs TargetEvent = new(nameof(Target));
    private static readonly PropertyChangedEventArgs TargetStringEvent = new(nameof(TargetString));
    private static readonly PropertyChangedEventArgs[] ProgressDependents = [ProgressPercentEvent, ProgressStringEvent];
    private static readonly PropertyChangedEventArgs[] TargetDependents = [ProgressPercentEvent, TargetStringEvent];

    private string _TargetFormat;
    private string _ProgressFormat;
    private string _ProgressRateFormat;
    private string _ProgressRateAverageFormat;
    private IFormatProvider _FormatProvider;

    private bool MakingRelevant;
    private bool Started;
    private bool ChildCollectionClosed;
    private bool CancellationRequested;
    private bool RateSamplingClosed;
    private bool RateSamplingDisabled;
    private bool RateSamplerRefreshing;
    private int TokenSourceCancellationCount;
    private bool TokenSourceDisposalPending;
    private Stopwatch Stopwatch;
    private CancellationTokenSource TokenSource;
    private CancellationTokenRegistration[] TokenSourceCancellationRegistrations;
    private CancellationTokenSource RateSamplerTokenSource;
    private Task RateSamplerTask;
    private Exception RateSamplerError;
    private ExceptionDispatchInfo LocalCancellation;
    private CancellationToken LocalCancellationToken;
    private CancellationToken ParentCancellationToken;
    private OperationSummary PreliminarySummary;
    private OperationSummary FinalSummary;
    private readonly List<ExceptionDispatchInfo> WorkFailures = [];
    private readonly List<ExceptionDispatchInfo> InfrastructureFailures = [];
    private readonly OperationRate Rate = new();
    private readonly OperationSynchronization Synchronization;
    private readonly OperationTreeState TreeState;
    private readonly ProgressChangeContext ProgressChange;
    private readonly OperationBaseCollection ChildCollection;
    private readonly CancellationToken SuppliedCancellationToken;
    private readonly TaskCompletionSource<bool> CompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool IsCancellationRequested => Synchronization.Read(() => GetCancellationStateUnsafe().Canceled);

    private OperationBase(
        string name,
        OperationBase parent,
        OperationDelegate task,
        OperationSynchronization synchronization,
        CancellationToken suppliedCancellationToken) {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        Parent = parent;
        Synchronization = synchronization ?? throw new ArgumentNullException(nameof(synchronization));
        TreeState = parent?.TreeState ?? new OperationTreeState();
        SuppliedCancellationToken = suppliedCancellationToken;
        TreeState.AddKnownToken(suppliedCancellationToken);
        ProgressChange = Parent?.ProgressChange ?? new ProgressChangeContext();
        Depth = Parent == null ? 0 : (Parent.Depth + 1);
        ChildCollection = new OperationBaseCollection(Synchronization);
        Name = name;
    }

    private (bool Canceled, CancellationToken Token) GetCancellationStateUnsafe() {
        if (PreliminarySummary is { } preliminary) {
            if (preliminary.OperationCancellationRequested) {
                return (true, preliminary.OperationCancellationToken);
            }
            if (preliminary.IsAmbientCanceled) {
                return (true, preliminary.AmbientCancellationToken);
            }
            if (LocalCancellation is not null) {
                return (true, preliminary.CancellationToken);
            }
            return (false, default);
        }

        var operationToken = TokenSource?.Token ?? default;
        if (CancellationRequested || operationToken.IsCancellationRequested) {
            return (true, LocalCancellationToken != default ? LocalCancellationToken : operationToken);
        }

        if (Parent is not null) {
            if (Parent.CancellationRequested || ParentCancellationToken.IsCancellationRequested) {
                var token = ParentCancellationToken;
                if (!token.CanBeCanceled) {
                    token = Parent.TokenSource?.Token ?? Parent.PreliminarySummary?.OperationCancellationToken ?? token;
                }
                return (true, token);
            }

            for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent) {
                if (ancestor.PreliminarySummary is { } ancestorPreliminary) {
                    if (ancestorPreliminary.OperationCancellationRequested) {
                        return (true, ancestorPreliminary.OperationCancellationToken);
                    }
                    if (ancestorPreliminary.IsAmbientCanceled) {
                        return (true, ancestorPreliminary.AmbientCancellationToken);
                    }
                    break;
                }

                var ancestorToken = ancestor.TokenSource?.Token ?? default;
                if (ancestor.CancellationRequested || ancestorToken.IsCancellationRequested) {
                    return (true, ancestorToken);
                }
                if (ancestor.SuppliedCancellationToken.IsCancellationRequested) {
                    return (true, ancestor.SuppliedCancellationToken);
                }
            }
        }

        if (SuppliedCancellationToken.IsCancellationRequested) {
            return (true, SuppliedCancellationToken);
        }
        if (LocalCancellation is not null) {
            return (true, LocalCancellationToken);
        }
        return (false, default);
    }

    private (bool Canceled, CancellationToken Token) GetParentCancellationStateUnsafe() {
        if (Parent is null) {
            return (false, default);
        }
        if (Parent.CancellationRequested || ParentCancellationToken.IsCancellationRequested) {
            var token = ParentCancellationToken;
            if (!token.CanBeCanceled) {
                token = Parent.TokenSource?.Token ?? Parent.PreliminarySummary?.OperationCancellationToken ?? token;
            }
            return (true, token);
        }
        return Parent.GetCancellationStateUnsafe();
    }

    private CancellationTokenRegistration RegisterLinkedCancellation(CancellationToken cancellationToken,
                                                                     CancellationTokenSource tokenSource) {
        if (ExecutionContext.IsFlowSuppressed()) {
            return cancellationToken.Register(
                () => CancelLinkedTokenSource(tokenSource),
                useSynchronizationContext: false);
        }
        using (ExecutionContext.SuppressFlow()) {
            return cancellationToken.Register(
                () => CancelLinkedTokenSource(tokenSource),
                useSynchronizationContext: false);
        }
    }

    private CancellationTokenRegistration[] RegisterTokenSourceCancellation(CancellationTokenSource tokenSource) {
        var registrations = new List<CancellationTokenRegistration>(2);
        try {
            if (ParentCancellationToken.CanBeCanceled) {
                registrations.Add(RegisterLinkedCancellation(ParentCancellationToken, tokenSource));
            }
            if (SuppliedCancellationToken.CanBeCanceled) {
                registrations.Add(RegisterLinkedCancellation(SuppliedCancellationToken, tokenSource));
            }
        }
        catch {
            foreach (var registration in registrations) {
                registration.Dispose();
            }
            throw;
        }
        return registrations.ToArray();
    }

    private void CancelLinkedTokenSource(CancellationTokenSource tokenSource) {
        var leaseAcquired = Synchronization.Update(() => {
            var sourceIsOwned = ReferenceEquals(TokenSource, tokenSource) ||
                (TokenSource is null && TokenSourceDisposalPending && TokenSourceCancellationCount > 0);
            if (!sourceIsOwned) {
                return false;
            }
            TokenSourceCancellationCount++;
            return true;
        });
        if (!leaseAcquired) {
            return;
        }
        try {
            tokenSource.Cancel();
        }
        finally {
            ReleaseTokenSourceCancellation(tokenSource);
        }
    }

    private void DisposeTokenSource(CancellationTokenSource tokenSource,
                                    CancellationTokenRegistration[] registrations) {
        if (registrations is not null) {
            foreach (var registration in registrations) {
                registration.Dispose();
            }
        }
        tokenSource.Dispose();
    }

    private void ReleaseTokenSourceCancellation(CancellationTokenSource tokenSource) {
        var registrationsToDispose = (CancellationTokenRegistration[])null;
        var disposeTokenSource = false;
        Synchronization.Update(() => {
            TokenSourceCancellationCount--;
            if (TokenSourceCancellationCount == 0 && TokenSourceDisposalPending) {
                TokenSourceDisposalPending = false;
                registrationsToDispose = TokenSourceCancellationRegistrations;
                TokenSourceCancellationRegistrations = null;
                disposeTokenSource = true;
            }
        });
        if (disposeTokenSource) {
            DisposeTokenSource(tokenSource, registrationsToDispose);
        }
    }

    private void CancelTokenSource(CancellationTokenSource tokenSource,
                                   IEnumerable<(OperationBase Owner,
                                                CancellationTokenSource TokenSource)> leasedTokenSources = null) {
        var failures = new List<Exception>();
        var callbackFailureObserved = false;
        try {
            cancel(tokenSource);
            if (leasedTokenSources is not null) {
                foreach (var lease in leasedTokenSources) {
                    if (!ReferenceEquals(lease.TokenSource, tokenSource)) {
                        cancel(lease.TokenSource);
                    }
                }
            }
        }
        finally {
            if (leasedTokenSources is null) {
                if (tokenSource is not null) {
                    try {
                        ReleaseTokenSourceCancellation(tokenSource);
                    }
                    catch (Exception ex) {
                        failures.Add(ex);
                    }
                }
            }
            else {
                foreach (var lease in leasedTokenSources) {
                    try {
                        lease.Owner.ReleaseTokenSourceCancellation(lease.TokenSource);
                    }
                    catch (Exception ex) {
                        failures.Add(ex);
                    }
                }
            }
        }
        if (failures.Count > 0 || callbackFailureObserved) {
            throw new AggregateException($"Cancellation of '{Name}' encountered callback failures.", failures)
                .Flatten();
        }
        void cancel(CancellationTokenSource source) {
            if (source is null) {
                return;
            }
            try {
                source.Cancel();
            }
            catch (AggregateException ex) {
                callbackFailureObserved = true;
                failures.AddRange(ex.Flatten().InnerExceptions);
            }
            catch (Exception ex) {
                failures.Add(ex);
            }
        }
    }

    private void RecordInfrastructure(ExceptionDispatchInfo failure) {
        TreeState.MarkInfrastructure(failure);
        if (TreeState.TryExpandInfrastructureAggregate(failure.SourceException, out var aggregateFailures)) {
            foreach (var aggregateFailure in aggregateFailures) {
                RecordInfrastructure(aggregateFailure);
            }
            return;
        }

        Synchronization.Update(() => {
            if (InfrastructureFailures.All(item => !ReferenceEquals(item.SourceException, failure.SourceException))) {
                InfrastructureFailures.Add(failure);
            }
        });
    }

    private void RecordFailure(ExceptionDispatchInfo failure, ref HashSet<Exception> descendantFailures) {
        var exception = failure.SourceException;
        if (TreeState.TryExpandWorkAggregate(exception, out var aggregateFailures)) {
            foreach (var aggregateFailure in aggregateFailures) {
                RecordOrdinaryFailure(aggregateFailure, ref descendantFailures);
            }
            return;
        }
        if (TreeState.TryExpandInfrastructureAggregate(exception, out var aggregateInfrastructure)) {
            foreach (var item in aggregateInfrastructure) {
                RecordInfrastructure(item);
            }
            return;
        }
        if (TreeState.IsInfrastructure(exception)) {
            if (!IsInfrastructureInDescendants(exception)) {
                RecordInfrastructure(failure);
            }
            return;
        }
        if (TreeState.TryGetCancellation(exception, out var classifiedCancellation, out var classifiedToken)) {
            if (!IsCancellationInDescendants(exception)) {
                RecordCancellation(classifiedCancellation ?? failure, classifiedToken);
            }
            return;
        }
        if (TreeState.TryGetWorkFailure(exception, out var classifiedFailure)) {
            RecordOrdinaryFailure(classifiedFailure, ref descendantFailures);
            return;
        }
        if (exception is OperationCanceledException canceled &&
            (TreeState.IsKnownCanceledToken(canceled.CancellationToken) ||
             Synchronization.Read(() => TokenSource?.IsCancellationRequested == true))) {
            var token = TreeState.IsKnownCanceledToken(canceled.CancellationToken)
                ? canceled.CancellationToken
                : TokenSource?.Token ?? default;
            RecordCancellation(failure, token);
            return;
        }
        RecordOrdinaryFailure(failure, ref descendantFailures);
    }

    private void RecordOrdinaryFailure(ExceptionDispatchInfo failure, ref HashSet<Exception> descendantFailures) {
        if (IsFailureInDescendants(failure.SourceException, ref descendantFailures)) {
            return;
        }
        if (!TreeState.TryGetWorkFailure(failure.SourceException, out var originalFailure)) {
            TreeState.MarkWorkFailure(failure);
            originalFailure = failure;
        }
        var publishLog = Synchronization.Update(() => {
            if (WorkFailures.All(item => !ReferenceEquals(item.SourceException, originalFailure.SourceException))) {
                WorkFailures.Add(originalFailure);
            }
            var shouldLog = Error is null;
            if (shouldLog) {
                Error = originalFailure.SourceException;
            }
            Relevant = true;
            return shouldLog;
        });
        if (publishLog) {
            try {
                if (Log.Warn()) {
                    Log.Warn(originalFailure.SourceException);
                }
            }
            catch (Exception loggingError) {
                RecordInfrastructure(ExceptionDispatchInfo.Capture(loggingError));
            }
        }
    }

    private void RecordCancellation(ExceptionDispatchInfo cancellation, CancellationToken token) {
        Synchronization.Update(() => {
            if (LocalCancellation is null) {
                LocalCancellation = cancellation;
                LocalCancellationToken = token;
            }
        });
        TreeState.MarkCancellation(cancellation, token);
    }

    private bool IsFailureInDescendants(Exception exception, ref HashSet<Exception> descendantFailures) {
        if (descendantFailures is null) {
            descendantFailures = new HashSet<Exception>(ExceptionReferenceComparer.Instance);
            foreach (var child in ChildCollection) {
                if (child.FinalSummary is { } summary) {
                    foreach (var failure in summary.OperationFailures) {
                        descendantFailures.Add(failure.SourceException);
                    }
                }
            }
        }
        return descendantFailures.Contains(exception);
    }

    private bool IsCancellationInDescendants(Exception exception) =>
        ChildCollection.ToArray().Any(child => child.FinalSummary is { } summary &&
            ReferenceEquals(summary.CancellationException, exception));

    private bool IsInfrastructureInDescendants(Exception exception) =>
        ChildCollection.ToArray().Any(child => child.FinalSummary is { } summary &&
            summary.InfrastructureFailures.Any(failure => ReferenceEquals(failure.SourceException, exception)));

    private static List<ExceptionDispatchInfo> DistinctFailures(IEnumerable<ExceptionDispatchInfo> failures) {
        var result = new List<ExceptionDispatchInfo>();
        var seen = new HashSet<Exception>(ExceptionReferenceComparer.Instance);
        foreach (var failure in failures) {
            if (seen.Add(failure.SourceException)) {
                result.Add(failure);
            }
        }
        return result;
    }

    private void ValidateChildRegistration() {
        var cancellation = GetCancellationStateUnsafe();
        if (cancellation.Canceled) {
            throw new OperationCanceledException("The parent operation has been canceled.", cancellation.Token);
        }
        if (ChildCollectionClosed) {
            throw new InvalidOperationException("Child operations can only be added while the parent is running.");
        }
    }

    private OperationBase RegisterChild(string name,
                                        OperationDelegate task,
                                        CancellationToken cancellationToken,
                                        out ExceptionDispatchInfo publicationFailure) {
        if (task is null) {
            throw new ArgumentNullException(nameof(task));
        }
        if (Log.Info()) {
            Log.Info(nameof(Child) + " > " + name);
        }
        var child = new OperationBase(name, this, task, Synchronization, cancellationToken);
        var committed = false;
        publicationFailure = null;
        try {
            ChildCollection.Add(child, ValidateChildRegistration, () => committed = true);
        }
        catch (Exception ex) {
            if (!committed) {
                MarkBoundaryFailure(ex);
                throw;
            }
            publicationFailure = ExceptionDispatchInfo.Capture(ex);
            TreeState.MarkInfrastructure(publicationFailure);
        }
        if (committed) {
            try {
                child.Start();
            }
            catch (Exception ex) {
                var failure = ExceptionDispatchInfo.Capture(ex);
                publicationFailure = CombineInfrastructure(publicationFailure, failure);
                TreeState.MarkInfrastructure(failure);
            }
        }
        return child;
    }

    private static ExceptionDispatchInfo CombineInfrastructure(ExceptionDispatchInfo first,
                                                               ExceptionDispatchInfo second) {
        if (first is null) {
            return second;
        }
        return OperationInfrastructureAggregateException.Combine(first, second);
    }

    private async Task<OperationResult> Child(string name,
                                              OperationDelegate task,
                                              OperationFailureBehavior failureBehavior,
                                              CancellationToken cancellationToken) {
        try {
            if (task is null) {
                throw new ArgumentNullException(nameof(task));
            }
            ValidateFailureBehavior(failureBehavior);
        }
        catch (Exception ex) {
            MarkBoundaryFailure(ex);
            throw;
        }
        OperationBase child;
        ExceptionDispatchInfo publicationFailure;
        try {
            child = RegisterChild(name, task, cancellationToken, out publicationFailure);
        }
        catch (Exception ex) {
            MarkBoundaryFailure(ex);
            throw;
        }
        var infrastructureFailures = new List<ExceptionDispatchInfo>();
        if (publicationFailure is not null) {
            infrastructureFailures.Add(publicationFailure);
        }
        try {
            await child.Completion;
        }
        catch (Exception ex) {
            if (child.FinalSummary is null) {
                infrastructureFailures.Add(ExceptionDispatchInfo.Capture(ex));
            }
        }
        if (child.FinalSummary is { } summary) {
            infrastructureFailures.AddRange(summary.InfrastructureFailures);
        }
        ThrowInfrastructure(infrastructureFailures);
        return child.CreateResult(failureBehavior);
    }

    private async Task<OperationBatchResult> Children<T>(IEnumerable<T> source,
                                                         Func<T, OperationChild> childFactory,
                                                         OperationFailureBehavior failureBehavior,
                                                         CancellationToken cancellationToken) {
        try {
            if (source is null) {
                throw new ArgumentNullException(nameof(source));
            }
            if (childFactory is null) {
                throw new ArgumentNullException(nameof(childFactory));
            }
            ValidateFailureBehavior(failureBehavior);
        }
        catch (Exception ex) {
            MarkBoundaryFailure(ex);
            throw;
        }
        TreeState.AddKnownToken(cancellationToken);
        var started = new List<(OperationBase Child, ExceptionDispatchInfo PublicationFailure)>();
        var constructionFailures = new List<ExceptionDispatchInfo>();
        var ambientFailures = new List<ExceptionDispatchInfo>();
        IEnumerator<T> enumerator = null;
        var stopScheduling = false;

        if (TryGetBatchCancellation(cancellationToken, out var initialCancellation)) {
            ambientFailures.Add(initialCancellation);
            stopScheduling = true;
        }
        if (!stopScheduling) {
            try {
                enumerator = source.GetEnumerator();
                if (enumerator is null) {
                    throw new InvalidOperationException("The source returned a null enumerator.");
                }
            }
            catch (Exception ex) {
                ClassifyBatchException(ex, cancellationToken, constructionFailures, ambientFailures);
                stopScheduling = true;
            }
        }
        while (!stopScheduling && enumerator is not null) {
            if (TryGetBatchCancellation(cancellationToken, out var beforeMoveCancellation)) {
                ambientFailures.Add(beforeMoveCancellation);
                break;
            }
            bool moved;
            try {
                moved = enumerator.MoveNext();
            }
            catch (Exception ex) {
                ClassifyBatchException(ex, cancellationToken, constructionFailures, ambientFailures);
                break;
            }
            if (!moved) {
                break;
            }
            if (TryGetBatchCancellation(cancellationToken, out var beforeFactoryCancellation)) {
                ambientFailures.Add(beforeFactoryCancellation);
                break;
            }
            OperationChild description;
            try {
                description = childFactory(enumerator.Current);
            }
            catch (Exception ex) {
                ClassifyBatchException(ex, cancellationToken, constructionFailures, ambientFailures);
                break;
            }
            if (description is null) {
                continue;
            }
            if (description.Task is null) {
                ClassifyBatchException(
                    new ArgumentNullException("task", "A child description has no operation delegate."),
                    cancellationToken,
                    constructionFailures,
                    ambientFailures);
                break;
            }
            if (TryGetBatchCancellation(cancellationToken, out var beforeRegistrationCancellation)) {
                ambientFailures.Add(beforeRegistrationCancellation);
                break;
            }
            try {
                var child = RegisterChild(description.Name,
                                          description.Task,
                                          cancellationToken,
                                          out var publicationFailure);
                started.Add((child, publicationFailure));
                if (publicationFailure is not null) {
                    constructionFailures.Add(publicationFailure);
                    break;
                }
            }
            catch (Exception ex) {
                ClassifyBatchException(ex, cancellationToken, constructionFailures, ambientFailures);
                break;
            }
        }
        if (enumerator is IDisposable disposable) {
            try {
                disposable.Dispose();
            }
            catch (Exception ex) {
                ClassifyBatchException(ex, cancellationToken, constructionFailures, ambientFailures);
            }
        }
        var results = new List<OperationResult>(started.Count);
        var infrastructureFailures = new List<ExceptionDispatchInfo>(constructionFailures);
        var workFailures = new List<ExceptionDispatchInfo>();
        var individualCancellations = new List<ExceptionDispatchInfo>();
        foreach (var entry in started) {
            if (entry.PublicationFailure is not null) {
                infrastructureFailures.Add(entry.PublicationFailure);
            }
            try {
                await entry.Child.Completion;
            }
            catch (Exception ex) {
                if (entry.Child.FinalSummary is null) {
                    infrastructureFailures.Add(ExceptionDispatchInfo.Capture(ex));
                }
            }
            if (entry.Child.FinalSummary is not { } summary) {
                continue;
            }
            infrastructureFailures.AddRange(summary.InfrastructureFailures);
            workFailures.AddRange(summary.OperationFailures);
            if (summary.IsCanceled && summary.Cancellation is not null) {
                individualCancellations.Add(summary.Cancellation);
            }
            results.Add(entry.Child.CreateResult());
        }
        if (TryGetBatchCancellation(cancellationToken, out var finalCancellation)) {
            ambientFailures.Add(finalCancellation);
        }
        if (infrastructureFailures.Count > 0) {
            ThrowInfrastructure(infrastructureFailures);
        }
        if (ambientFailures.Count > 0) {
            var cancellation = SelectBatchCancellation(cancellationToken, ambientFailures);
            cancellation.Throw();
        }
        if (workFailures.Count > 0 && failureBehavior == OperationFailureBehavior.Throw) {
            CombineFailures(workFailures, infrastructure: false).Throw();
        }
        if (workFailures.Count == 0 && individualCancellations.Count > 0 &&
            failureBehavior == OperationFailureBehavior.Throw) {
            individualCancellations[0].Throw();
        }
        return new OperationBatchResult(results);
    }

    private bool TryGetBatchCancellation(
        CancellationToken cancellationToken,
        out ExceptionDispatchInfo cancellation) {
        var state = Synchronization.Read(() => GetCancellationStateUnsafe());
        if (state.Canceled) {
            cancellation = ExceptionDispatchInfo.Capture(new OperationCanceledException(
                "The parent operation has been canceled.", state.Token));
            TreeState.MarkCancellation(cancellation, state.Token);
            return true;
        }
        if (cancellationToken.IsCancellationRequested) {
            cancellation = ExceptionDispatchInfo.Capture(new OperationCanceledException(
                "The batch was canceled.", cancellationToken));
            TreeState.MarkCancellation(cancellation, cancellationToken);
            return true;
        }
        cancellation = null;
        return false;
    }

    private void ClassifyBatchException(
        Exception exception,
        CancellationToken cancellationToken,
        List<ExceptionDispatchInfo> infrastructureFailures,
        List<ExceptionDispatchInfo> ambientFailures) {
        var failure = ExceptionDispatchInfo.Capture(exception);
        if (TreeState.TryExpandInfrastructureAggregate(exception, out var aggregateInfrastructure)) {
            infrastructureFailures.AddRange(aggregateInfrastructure);
            return;
        }
        if (TreeState.IsInfrastructure(exception)) {
            infrastructureFailures.Add(failure);
            return;
        }
        if (TreeState.TryGetCancellation(exception, out var classifiedCancellation, out _)) {
            ambientFailures.Add(classifiedCancellation ?? failure);
            return;
        }
        if (TreeState.TryExpandWorkAggregate(exception, out var aggregateWork)) {
            foreach (var item in aggregateWork) {
                TreeState.MarkInfrastructure(item);
                infrastructureFailures.Add(item);
            }
            return;
        }
        if (TreeState.TryGetWorkFailure(exception, out var classifiedWork)) {
            TreeState.MarkInfrastructure(classifiedWork);
            infrastructureFailures.Add(classifiedWork);
            return;
        }
        if (exception is OperationCanceledException canceled &&
            (TreeState.IsKnownCanceledToken(canceled.CancellationToken) ||
             TryGetBatchCancellation(cancellationToken, out _))) {
            if (TryGetBatchCancellation(cancellationToken, out var ambient)) {
                var ambientToken = ((OperationCanceledException)ambient.SourceException).CancellationToken;
                if (canceled.CancellationToken == ambientToken) {
                    TreeState.MarkCancellation(failure, canceled.CancellationToken);
                    ambientFailures.Add(failure);
                }
                else {
                    ambientFailures.Add(ambient);
                }
            }
            else {
                TreeState.MarkCancellation(failure, canceled.CancellationToken);
                ambientFailures.Add(failure);
            }
            return;
        }
        infrastructureFailures.Add(failure);
        TreeState.MarkInfrastructure(failure);
    }

    private ExceptionDispatchInfo SelectBatchCancellation(CancellationToken cancellationToken,
                                                          IReadOnlyList<ExceptionDispatchInfo> cancellations) {
        if (TryGetBatchCancellation(cancellationToken, out var ambient)) {
            var token = ((OperationCanceledException)ambient.SourceException).CancellationToken;
            var original = cancellations.FirstOrDefault(cancellation =>
                cancellation.SourceException is OperationCanceledException exception &&
                exception.CancellationToken == token);
            if (original is not null) {
                return original;
            }
            return ambient;
        }
        return cancellations[0];
    }

    private OperationResult CreateResult(OperationFailureBehavior failureBehavior) {
        var summary = FinalSummary ?? throw new InvalidOperationException("The operation has not finished.");
        ThrowInfrastructure(summary.InfrastructureFailures);

        if (summary.IsAmbientCanceled) {
            var cancellation = CancellationFor(summary, summary.AmbientCancellationToken);
            TreeState.MarkCancellation(cancellation, summary.AmbientCancellationToken);
            cancellation.Throw();
        }
        if (failureBehavior == OperationFailureBehavior.Throw && summary.OperationFailures.Count > 0) {
            CombineFailures(summary.OperationFailures, infrastructure: false).Throw();
        }
        if (failureBehavior == OperationFailureBehavior.Throw && summary.IsCanceled) {
            var cancellation = CancellationFor(summary, summary.CancellationToken);
            TreeState.MarkCancellation(cancellation, summary.CancellationToken);
            cancellation.Throw();
        }
        return CreateResult();
    }

    private OperationResult CreateResult() {
        var summary = FinalSummary ?? throw new InvalidOperationException("The operation has not finished.");
        if (summary.OperationFailures.Count > 0) {
            var failure = CombineFailures(summary.OperationFailures, infrastructure: false);
            return new OperationResult(OperationResultKind.Failed, failure.SourceException);
        }
        if (summary.IsCanceled) {
            var cancellation = CancellationFor(summary, summary.CancellationToken);
            TreeState.MarkCancellation(cancellation, summary.CancellationToken);
            return new OperationResult(OperationResultKind.Canceled, cancellation.SourceException);
        }
        return new OperationResult(OperationResultKind.Succeeded, null);
    }

    private static ExceptionDispatchInfo CancellationFor(OperationSummary summary, CancellationToken token) =>
        summary.Cancellation is { SourceException: OperationCanceledException cancellation } &&
        cancellation.CancellationToken == token
            ? summary.Cancellation
            : ExceptionDispatchInfo.Capture(new OperationCanceledException("The operation was canceled.", token));

    private ExceptionDispatchInfo CombineFailures(IEnumerable<ExceptionDispatchInfo> failures,
                                                  bool infrastructure) {
        var distinct = DistinctFailures(failures);
        if (distinct.Count == 1) {
            return distinct[0];
        }
        var aggregate = new AggregateException(distinct.Select(failure => failure.SourceException));
        if (infrastructure) {
            TreeState.MarkLibraryInfrastructureAggregate(aggregate, distinct.AsReadOnly());
        }
        else {
            TreeState.MarkLibraryWorkAggregate(aggregate, distinct.AsReadOnly());
        }
        return ExceptionDispatchInfo.Capture(aggregate);
    }

    private void ThrowInfrastructure(IEnumerable<ExceptionDispatchInfo> failures) {
        var expanded = new List<ExceptionDispatchInfo>();
        foreach (var failure in failures) {
            TreeState.MarkInfrastructure(failure);
            if (TreeState.TryExpandInfrastructureAggregate(failure.SourceException, out var aggregateFailures)) {
                expanded.AddRange(aggregateFailures);
            }
            else {
                expanded.Add(failure);
            }
        }
        var distinct = DistinctFailures(expanded);
        if (distinct.Count == 0) {
            return;
        }
        foreach (var failure in distinct) {
            TreeState.MarkInfrastructure(failure);
        }
        CombineFailures(distinct, infrastructure: true).Throw();
    }

    private static void ValidateFailureBehavior(OperationFailureBehavior failureBehavior) {
        if (failureBehavior is not OperationFailureBehavior.Record and not OperationFailureBehavior.Throw) {
            throw new ArgumentOutOfRangeException(
                nameof(failureBehavior),
                failureBehavior,
                "The failure behavior is undefined.");
        }
    }

    private void MarkBoundaryFailure(Exception exception) {
        if (TreeState.IsInfrastructure(exception)) {
            return;
        }
        else if (exception is OperationCanceledException canceled &&
                 (TreeState.IsKnownCanceledToken(canceled.CancellationToken) || IsCancellationRequested)) {
            TreeState.MarkCancellation(ExceptionDispatchInfo.Capture(canceled), canceled.CancellationToken);
        }
        else {
            TreeState.MarkInfrastructure(ExceptionDispatchInfo.Capture(exception));
        }
    }

    private void MarkInfrastructureBoundaryFailure(Exception exception) {
        TreeState.MarkInfrastructure(ExceptionDispatchInfo.Capture(exception));
    }

    private async void MakeRelevant() {
        var token = default(CancellationToken);
        var delay = default(Task);
        var start = Synchronization.Update(() => {
            if (Relevant || MakingRelevant || !Progressing) {
                return false;
            }
            MakingRelevant = true;
            token = TokenSource?.Token ?? default;
            delay = TASK.Delay(1000, token);
            return true;
        });
        if (!start) {
            return;
        }
        try {
            try {
                await delay;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) {
            }
            Synchronization.Update(() => {
                if (Progressing) {
                    Relevant = true;
                }
            });
        }
        finally {
            Synchronization.Update(() => MakingRelevant = false);
        }
    }

    private void NotifyTargetChanged() {
        NotifyPropertyChanged(TargetEvent, TargetDependents);
        Synchronization.Notify(() => TargetChanged?.Invoke(this, EventArgs.Empty));
        MakeRelevant();
    }

    private void NotifyProgressChanged() {
        NotifyPropertyChanged(ProgressEvent, ProgressDependents);
        Synchronization.Notify(() => ProgressChanged?.Invoke(this, EventArgs.Empty));
        MakeRelevant();
    }

    private async Task SampleRates(CancellationToken token) {
        try {
            while (true) {
                await TASK.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                var refresh = Synchronization.Update(() => {
                    if (RateSamplingClosed || Complete) {
                        return false;
                    }
                    RateSamplerRefreshing = true;
                    return true;
                });
                if (!refresh) {
                    return;
                }
                try {
                    RefreshRates();
                }
                catch (Exception ex) {
                    RateSamplerError = ex;
                    return;
                }
                finally {
                    Synchronization.Update(() => {
                        RateSamplerRefreshing = false;
                    });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {
        }
        catch (Exception ex) {
            RateSamplerError = ex;
        }
    }

    private void StartRateSampler() {
        RateSamplerTokenSource = new CancellationTokenSource();
        RateSamplerTask = SampleRates(RateSamplerTokenSource.Token);
    }

    private void RequestRateSampler() {
        var root = this;
        while (root.Parent is not null) {
            root = root.Parent;
        }
        root.Synchronization.Update(() => {
            if (!root.RateSamplingDisabled && root.Progressing && !root.Complete && !root.RateSamplingClosed) {
                if (root.RateSamplerTokenSource is null) {
                    root.StartRateSampler();
                }
            }
        });
    }

    private async Task StopRateSampler() {
        var source = RateSamplerTokenSource;
        if (source is null) {
            return;
        }
        var task = RateSamplerTask;
        try {
            source.Cancel();
            var refreshing = Synchronization.Read(() => RateSamplerRefreshing);
            /*
             * The root closes sampling before shutdown. An idle sampler can only observe cancellation or
             * that closed state, so do not block its caller on a queued continuation that may need the
             * same thread-pool worker. A listener can complete the operation inline during refresh;
             * awaiting an active refresh lets it return before finalization reads any error it raised.
             */
            if (refreshing) {
                await task.ConfigureAwait(false);
            }
        }
        finally {
            source.Dispose();
            RateSamplerTokenSource = null;
            RateSamplerTask = null;
        }
    }

    private void RefreshRates() => RefreshRates(operation => operation.Stopwatch?.Elapsed ?? TimeSpan.Zero);

    private void CompleteRate() {
        var previousRate = Rate.ProgressRate;
        var previousAverage = Rate.ProgressRateAverage;
        Rate.Complete(Progress, Stopwatch?.Elapsed ?? TimeSpan.Zero);
        if (Rate.ProgressRate != previousRate) {
            NotifyPropertyChanged(ProgressRateEvent);
            NotifyPropertyChanged(ProgressRateStringEvent);
        }
        if (Rate.ProgressRateAverage != previousAverage) {
            NotifyPropertyChanged(ProgressRateAverageEvent);
            NotifyPropertyChanged(ProgressRateAverageStringEvent);
        }
    }

    private void ChangeProgress(long? setProgress = null,
                                long? addProgress = null,
                                long? setTarget = null,
                                long? addTarget = null) {
        Synchronization.Update(() => {
            /*
             * Defer notifications until the child and all its ancestors have committed the update.
             */
            var
            change = ProgressChange;
            change.Depth++;
            try {
                core();
            }
            finally {
                change.Depth--;
                if (change.Depth == 0) {
                    Flush(change);
                }
            }
            void core() {
                var changed = false;
                var changedTarget = false;
                var changedProgress = false;
                if (setProgress.HasValue) {
                    var
                    value = setProgress.Value;
                    changed = true;
                    changedProgress = true;
                    if (Parent is not null) {
                        Parent.ChangeProgress(setProgress: Parent.Progress + (value - Progress));
                    }
                    Progress = value;
                }
                if (addProgress.HasValue) {
                    var
                    value = addProgress.Value;
                    changed = true;
                    changedProgress = true;
                    if (Parent is not null) {
                        Parent.ChangeProgress(addProgress: value);
                    }
                    Progress += value;
                }
                if (setTarget.HasValue) {
                    var
                    value = setTarget.Value;
                    changed = true;
                    changedTarget = true;
                    if (Parent is not null) {
                        Parent.ChangeProgress(setTarget: Parent.Target + (value - Target));
                    }
                    Target = value;
                }
                if (addTarget.HasValue) {
                    var
                    value = addTarget.Value;
                    changed = true;
                    changedTarget = true;
                    if (Parent is not null) {
                        Parent.ChangeProgress(addTarget: value);
                    }
                    Target += value;
                }
                if (changed) {
                    ProgressPercent = Target == 0
                        ? 0
                        : (double)Progress / Target * 100;
                }
                if (changedTarget || changedProgress) {
                    ProgressChange.Notifications.Enqueue((this, changedTarget, changedProgress));
                }
            }
        });
    }

    private static void Flush(ProgressChangeContext change) {
        if (change.Dispatching) {
            return;
        }
        change.Dispatching = true;
        try {
            while (change.Notifications.TryDequeue(out var notification)) {
                if (notification.Target) {
                    notification.Operation.NotifyTargetChanged();
                }
                if (notification.Progress) {
                    notification.Operation.NotifyProgressChanged();
                }
            }
        }
        finally {
            change.Dispatching = false;
        }
    }

    private async TASK Operate() {
        if (Log.Info()) {
            Log.Info(nameof(Operate));
        }
        var tokenSource = default(CancellationTokenSource);
        var token = default(CancellationToken);
        var tokenSourceReady = false;
        var cancellationLeaseAcquired = false;
        try {
            ParentCancellationToken = Parent is null
                ? default
                : Synchronization.Read(() =>
                    Parent.TokenSource?.Token ??
                    Parent.PreliminarySummary?.OperationCancellationToken ??
                    default);
            tokenSource = new CancellationTokenSource();
            token = tokenSource.Token;
            TreeState.AddKnownToken(token);
            var cancelToken = Synchronization.Update(() => {
                TokenSource = tokenSource;
                Stopwatch = Stopwatch.StartNew();
                Rate.Start(Progress, TimeSpan.Zero);
                Progressing = true;
                if (!CancellationRequested) {
                    return false;
                }
                TokenSourceCancellationCount++;
                cancellationLeaseAcquired = true;
                return true;
            });
            if (cancelToken) {
                cancellationLeaseAcquired = false;
                CancelTokenSource(tokenSource);
            }
            var registrations = RegisterTokenSourceCancellation(tokenSource);
            Synchronization.Update(() => TokenSourceCancellationRegistrations = registrations);
            tokenSourceReady = true;
        }
        catch (Exception ex) {
            var failure = ExceptionDispatchInfo.Capture(ex);
            if (cancellationLeaseAcquired) {
                cancellationLeaseAcquired = false;
                try {
                    CancelTokenSource(tokenSource);
                }
                catch (Exception cancellationFailure) {
                    failure = OperationInfrastructureAggregateException.Combine(
                        failure,
                        ExceptionDispatchInfo.Capture(cancellationFailure));
                }
            }
            RecordInfrastructure(failure);
        }

        if (tokenSourceReady && !IsCancellationRequested) {
            var work = default(Task);
            var taskFailures = new List<ExceptionDispatchInfo>();
            try {
                work = Task(new ProgressWrapper(this), token);
                if (work is null) {
                    throw new InvalidOperationException("The operation delegate returned a null task.");
                }
                await work;
            }
            catch (Exception ex) {
                if (work?.IsFaulted == true && work.Exception is { } taskException) {
                    foreach (var taskFailure in taskException.InnerExceptions) {
                        taskFailures.Add(ExceptionDispatchInfo.Capture(taskFailure));
                    }
                }
                else {
                    taskFailures.Add(ExceptionDispatchInfo.Capture(ex));
                }
            }
            finally {
                CloseChildRegistration();
            }
            CaptureWorkFailures(taskFailures);
        }
        CloseChildRegistration();

        var children = default(OperationBase[]);
        try {
            children = [.. ChildCollection];
        }
        catch (Exception ex) {
            RecordInfrastructure(ExceptionDispatchInfo.Capture(ex));
        }
        children ??= [];
        foreach (var child in children) {
            try {
                await child.Completion;
            }
            catch (Exception ex) {
                if (child.FinalSummary is null) {
                    RecordInfrastructure(ExceptionDispatchInfo.Capture(ex));
                }
            }
        }
        if (Parent is null) {
            try {
                Synchronization.Update(() => {
                    RateSamplingClosed = true;
                    Stopwatch?.Stop();
                });
            }
            catch (Exception ex) {
                RecordInfrastructure(ExceptionDispatchInfo.Capture(ex));
            }
            try {
                await StopRateSampler();
            }
            catch (Exception ex) {
                RecordInfrastructure(ExceptionDispatchInfo.Capture(ex));
            }
            if (RateSamplerError is not null) {
                RecordInfrastructure(ExceptionDispatchInfo.Capture(RateSamplerError));
            }
        }
        var frozen = FreezeSummary(children);
        var tokenSourceToDispose = default(CancellationTokenSource);
        var registrationsToDispose = default(CancellationTokenRegistration[]);
        try {
            Synchronization.Update(() => {
                Stopwatch?.Stop();
                try {
                    CompleteRate();
                }
                catch (Exception ex) {
                    RecordInfrastructure(ExceptionDispatchInfo.Capture(ex));
                }
                var source = TokenSource;
                TokenSource = null;
                if (source is not null) {
                    if (TokenSourceCancellationCount == 0) {
                        tokenSourceToDispose = source;
                        registrationsToDispose = TokenSourceCancellationRegistrations;
                        TokenSourceCancellationRegistrations = null;
                    }
                    else {
                        TokenSourceDisposalPending = true;
                    }
                }
                Progressing = false;
                var completeWithError = frozen.OperationFailures.Count > 0 || Error is not null;
                CompleteWithError = completeWithError;
                if (completeWithError) {
                    Relevant = true;
                }
                else if (Target == 0) {
                    ProgressPercent = 100;
                    NotifyPropertyChanged(ProgressPercentEvent);
                }
                Complete = true;
            });
        }
        catch (Exception ex) {
            RecordInfrastructure(ExceptionDispatchInfo.Capture(ex));
            try {
                Synchronization.Update(() => {
                    Progressing = false;
                    CompleteWithError = frozen.OperationFailures.Count > 0 || Error is not null;
                    Complete = true;
                });
            }
            catch (Exception finalizationError) {
                RecordInfrastructure(ExceptionDispatchInfo.Capture(finalizationError));
            }
        }
        finally {
            if (tokenSourceToDispose is not null) {
                try {
                    DisposeTokenSource(tokenSourceToDispose, registrationsToDispose);
                }
                catch (Exception ex) {
                    RecordInfrastructure(ExceptionDispatchInfo.Capture(ex));
                }
            }
        }
        try {
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) {
            RecordInfrastructure(ExceptionDispatchInfo.Capture(ex));
        }
        FinalSummary = new OperationSummary(
            frozen.OperationFailures,
            DistinctFailures(frozen.InfrastructureFailures.Concat(InfrastructureFailures)),
            frozen.IsCanceled,
            frozen.Cancellation,
            frozen.CancellationToken,
            frozen.IsAmbientCanceled,
            frozen.AmbientCancellationToken,
            frozen.OperationCancellationRequested,
            frozen.OperationCancellationToken);
        ThrowInfrastructure(FinalSummary.InfrastructureFailures);
    }

    private void CaptureWorkFailures(IEnumerable<ExceptionDispatchInfo> failures) {
        var descendantFailures = default(HashSet<Exception>);
        foreach (var failure in failures) {
            try {
                RecordFailure(failure, ref descendantFailures);
            }
            catch (Exception infrastructureError) {
                RecordInfrastructure(ExceptionDispatchInfo.Capture(infrastructureError));
            }
        }
    }

    private void CloseChildRegistration() {
        try {
            Synchronization.Update(() => ChildCollectionClosed = true);
        }
        catch (Exception ex) {
            RecordInfrastructure(ExceptionDispatchInfo.Capture(ex));
        }
    }

    private OperationSummary FreezeSummary(OperationBase[] children) {
        return Synchronization.Update(() => {
            var failures = DistinctFailures(WorkFailures.Concat(children
                .Where(child => child.FinalSummary is not null)
                .SelectMany(child => child.FinalSummary.OperationFailures)));
            var infrastructure = DistinctFailures(InfrastructureFailures.Concat(children
                .Where(child => child.FinalSummary is not null)
                .SelectMany(child => child.FinalSummary.InfrastructureFailures)));
            var parentCancellation = GetParentCancellationStateUnsafe();
            var parentCanceled = parentCancellation.Canceled;
            var suppliedCanceled = SuppliedCancellationToken.IsCancellationRequested;
            var operationToken = TokenSource?.Token ?? default;
            var operationCancellationRequested = CancellationRequested || operationToken.IsCancellationRequested;
            var ambientCanceled = parentCanceled || suppliedCanceled;
            var ambientToken = parentCanceled ? parentCancellation.Token : SuppliedCancellationToken;
            var ownCanceled = operationCancellationRequested || LocalCancellation is not null;
            var descendantCancellation = children
                .Select(child => child.FinalSummary)
                .FirstOrDefault(summary => summary is { IsCanceled: true });
            var isCanceled = ambientCanceled || ownCanceled || descendantCancellation is not null;
            var cancellationToken = ambientCanceled
                ? ambientToken
                : ownCanceled
                    ? LocalCancellationToken != default
                        ? LocalCancellationToken
                        : operationToken
                    : descendantCancellation?.CancellationToken ?? default;
            var cancellation = LocalCancellation is { SourceException: OperationCanceledException ownException } &&
                               ownException.CancellationToken == cancellationToken
                ? LocalCancellation
                : descendantCancellation?.Cancellation is {
                    SourceException: OperationCanceledException childException
                } && childException.CancellationToken == cancellationToken
                    ? descendantCancellation.Cancellation
                    : null;
            if (isCanceled && cancellation is null) {
                cancellation = ExceptionDispatchInfo.Capture(
                    new OperationCanceledException("The operation was canceled.", cancellationToken));
                TreeState.MarkCancellation(cancellation, cancellationToken);
            }
            return PreliminarySummary = new OperationSummary(
                failures,
                infrastructure,
                isCanceled,
                cancellation,
                cancellationToken,
                ambientCanceled,
                ambientToken,
                operationCancellationRequested,
                operationToken);
        });
    }

    private async Task RunOperation() {
        try {
            await Operate();
            CompletionSource.TrySetResult(true);
        }
        catch (Exception ex) {
            CompletionSource.TrySetException(ex);
        }
    }

    private static async void ObserveCompletion(Task completion) {
        /*
         * Surface unexpected root failures through the observer's captured synchronization context.
         */
        await completion;
    }

    private static void ObserveCompletion(Task completion, SynchronizationContext synchronizationContext) {
        if (completion.IsCompleted && !completion.IsFaulted && !completion.IsCanceled) {
            return;
        }
        if (synchronizationContext is null || SynchronizationContext.Current == synchronizationContext) {
            ObserveCompletion(completion);
        }
        else {
            synchronizationContext.Post(_ => ObserveCompletion(completion), null);
        }
    }

    internal event EventHandler RelevantCommitted;

    internal void Start() {
        Synchronization.Update(() => {
            if (Started) {
                throw new InvalidOperationException("The operation has already started.");
            }
            Started = true;
        });
        _ = RunOperation();
        if (Parent is null) {
            ObserveCompletion(CompletionSource.Task, Synchronization.SynchronizationContext);
        }
    }

    internal void RefreshRates(Func<OperationBase, TimeSpan> elapsedTime) {
        if (elapsedTime is null) {
            throw new ArgumentNullException(nameof(elapsedTime));
        }
        Synchronization.Update(() => {
            if (RateSamplingClosed || Complete) {
                return;
            }
            refresh(this);

            void refresh(OperationBase operation) {
                if (operation.Progressing && !operation.Complete) {
                    var previousRate = operation.Rate.ProgressRate;
                    var previousAverage = operation.Rate.ProgressRateAverage;
                    if (operation.Rate.Sample(operation.Progress, elapsedTime(operation))) {
                        if (operation.Rate.ProgressRate != previousRate) {
                            operation.NotifyPropertyChanged(ProgressRateEvent);
                            operation.NotifyPropertyChanged(ProgressRateStringEvent);
                        }
                        if (operation.Rate.ProgressRateAverage != previousAverage) {
                            operation.NotifyPropertyChanged(ProgressRateAverageEvent);
                            operation.NotifyPropertyChanged(ProgressRateAverageStringEvent);
                        }
                    }
                }
                foreach (var child in operation.ChildCollection.ToArray()) {
                    refresh(child);
                }
            }
        });
    }

    internal void DisableRateSampling() {
        Synchronization.Update(() => {
            if (Started) {
                throw new InvalidOperationException("Rate sampling must be disabled before the operation starts.");
            }
            RateSamplingDisabled = true;
        });
    }

    protected sealed override void OnPropertyChanged(PropertyChangedEventArgs e) {
        Synchronization.Notify(() => PublishPropertyChanged(e));
    }

    protected virtual void PublishPropertyChanged(PropertyChangedEventArgs e) {
        base.OnPropertyChanged(e);
    }

    protected void Cancel() {
        var tokenSource = default(CancellationTokenSource);
        var leasedTokenSources = new List<(OperationBase Owner, CancellationTokenSource TokenSource)>();
        var failure = default(ExceptionDispatchInfo);
        try {
            Synchronization.Update(() => {
                if (CancellationRequested) {
                    return;
                }
                if (Log.Info()) {
                    Log.Info($"Cancel: {Name}");
                }
                /*
                 * Mark the tree before cancellation callbacks can complete it synchronously.
                 */
                cancel(this);
                tokenSource = TokenSource;
                void cancel(OperationBase op) {
                    if (op.CancellationRequested) {
                        return;
                    }
                    op.CancellationRequested = true;
                    op.Canceling = true;
                    if (op.TokenSource is not null) {
                        op.TokenSourceCancellationCount++;
                        leasedTokenSources.Add((op, op.TokenSource));
                    }
                    foreach (var child in op.ChildCollection.ToArray()) {
                        cancel(child);
                    }
                }
            });
        }
        catch (Exception ex) {
            failure = ExceptionDispatchInfo.Capture(ex);
        }
        try {
            CancelTokenSource(tokenSource, leasedTokenSources);
        }
        catch (Exception ex) {
            var cancellationFailure = ExceptionDispatchInfo.Capture(ex);
            if (failure is null) {
                failure = cancellationFailure;
            }
            else if (ex is AggregateException aggregate) {
                failure = OperationInfrastructureAggregateException.Combine(
                    failure,
                    aggregate.Flatten().InnerExceptions.Select(ExceptionDispatchInfo.Capture));
            }
            else {
                failure = OperationInfrastructureAggregateException.Combine(failure, cancellationFailure);
            }
        }
        if (failure is not null) {
            TreeState.MarkInfrastructure(failure);
        }
        failure?.Throw();
    }

    public event EventHandler Completed;
    public event EventHandler ProgressChanged;
    public event EventHandler RelevantChanged;
    public event EventHandler TargetChanged;

    public long Target {
        get => Synchronization.Read(() => _Target);
        private set => Synchronization.Update(() => _Target = value);
    }
    private long _Target;

    public long Progress {
        get => Synchronization.Read(() => _Progress);
        private set => Synchronization.Update(() => _Progress = value);
    }
    private long _Progress;

    public double ProgressRate => Synchronization.Read(() => Rate.ProgressRate);

    public double ProgressRateAverage => Synchronization.Read(() => Rate.ProgressRateAverage);

    public string ProgressRateString => Synchronization.Read(() => _ProgressKind switch {
        OperationProgressKind.FileSize =>
            OperationFileSize.ToString(Rate.ProgressRate, _ProgressRateFormat, _FormatProvider),
        _ => Rate.ProgressRate.ToString(_ProgressRateFormat, _FormatProvider)
    });

    public string ProgressRateAverageString => Synchronization.Read(() => _ProgressKind switch {
        OperationProgressKind.FileSize =>
            OperationFileSize.ToString(Rate.ProgressRateAverage, _ProgressRateAverageFormat, _FormatProvider),
        _ => Rate.ProgressRateAverage.ToString(_ProgressRateAverageFormat, _FormatProvider)
    });

    public double ProgressPercent {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => field = value);
    }

    public OperationProgressKind ProgressKind => Synchronization.Read(() => _ProgressKind);
    private OperationProgressKind _ProgressKind;

    public string TargetString => Synchronization.Read(() => _TargetString ?? _ProgressKind switch {
        OperationProgressKind.FileSize =>
            OperationFileSize.ToString(_Target,
                                       _TargetFormat,
                                       _FormatProvider),
        _ => _Target.ToString()
    });
    private string _TargetString;

    public string ProgressString => Synchronization.Read(() => _ProgressString ?? _ProgressKind switch {
        OperationProgressKind.FileSize =>
            OperationFileSize.ToString(_Progress,
                                       _ProgressFormat,
                                       _FormatProvider),
        _ => _Progress.ToString()
    });
    private string _ProgressString;

    public string Name {
        get => Synchronization.Read(() => _Name);
        private set => Synchronization.Update(() => Change(ref _Name, value, NameEvent));
    }
    private string _Name;

    public string Data => Synchronization.Read(() => _Data);
    private string _Data;

    public bool Relevant {
        get => Synchronization.Read(() => field);
        private set {
            Synchronization.Update(() => {
                if (Change(ref field, value, RelevantEvent)) {
                    if (value && Parent is not null) {
                        Parent.Relevant = true;
                    }
                    /*
                     * Commit collection relevance in this state transaction before publishing external notifications.
                     */
                    RelevantCommitted?.Invoke(this, EventArgs.Empty);
                    Synchronization.Notify(() => RelevantChanged?.Invoke(this, EventArgs.Empty));
                }
            });
        }
    }

    public bool Canceling {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => Change(ref field, value, CancelingEvent));
    }

    public Exception Error {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => Change(ref field, value, ErrorEvent));
    }

    public bool Progressing {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => Change(ref field, value, ProgressingEvent));
    }

    public object ChildSource => ChildCollection.Source;

    public int Depth { get; }

    public string DepthString =>
        Synchronization.Update(() => field ??= new string('>', Depth));

    public bool CompleteWithError {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => Change(ref field, value, CompleteWithErrorEvent));
    }

    public bool Complete {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => Change(ref field, value, CompleteEvent));
    }

    public TASK Completion => CompletionSource.Task;
    public OperationBase Parent { get; }
    public OperationDelegate Task { get; }

    public OperationBase(string name,
                        OperationBase parent,
                        OperationDelegate task,
                        SynchronizationContext synchronizationContext = null)
    : this(name, parent, task, parent?.Synchronization ?? new OperationSynchronization(
        synchronizationContext ?? SynchronizationContext.Current)) {
    }

    internal OperationBase(string name,
                           OperationBase parent,
                           OperationDelegate task,
                           OperationSynchronization synchronization)
    : this(name, parent, task, synchronization, default) {
    }

    private sealed class ProgressWrapper : IOperationProgress {
        public OperationBase Operation { get; }

        public ProgressWrapper(OperationBase operation) {
            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        }

        public void Change(long? addProgress,
                           long? setProgress,
                           long? addTarget,
                           long? setTarget,
                           string progressString,
                           string targetString,
                           string name,
                           string data,
                           OperationProgressKind? kind,
                           string progressFormat,
                           string targetFormat,
                           string progressRateFormat,
                           string progressRateAverageFormat,
                           IFormatProvider formatProvider) {
            try {
                Operation.Synchronization.Update(() => {
                    if (name is not null) {
                        Operation.Change(ref Operation._Name, name, NameEvent);
                    }
                    if (data is not null) {
                        Operation.Change(ref Operation._Data, data, DataEvent);
                    }
                    var kindChanged = false;
                    if (kind.HasValue) {
                        kindChanged = Operation.Change(
                            ref Operation._ProgressKind, kind.Value, ProgressKindEvent);
                    }
                    var progressFormatChanged = false;
                    if (progressFormat is not null) {
                        progressFormatChanged =
                            !StringComparer.Ordinal.Equals(progressFormat, Operation._ProgressFormat);
                        if (progressFormatChanged) {
                            Operation._ProgressFormat = progressFormat;
                        }
                    }
                    var targetFormatChanged = false;
                    if (targetFormat is not null) {
                        targetFormatChanged =
                            !StringComparer.Ordinal.Equals(targetFormat, Operation._TargetFormat);
                        if (targetFormatChanged) {
                            Operation._TargetFormat = targetFormat;
                        }
                    }
                    var progressRateFormatChanged = false;
                    if (progressRateFormat is not null) {
                        progressRateFormatChanged =
                            !StringComparer.Ordinal.Equals(Operation._ProgressRateFormat, progressRateFormat);
                        if (progressRateFormatChanged) {
                            Operation._ProgressRateFormat = progressRateFormat;
                        }
                    }
                    var progressRateAverageFormatChanged = false;
                    if (progressRateAverageFormat is not null) {
                        progressRateAverageFormatChanged =
                            !StringComparer.Ordinal.Equals(Operation._ProgressRateAverageFormat, progressRateAverageFormat);
                        if (progressRateAverageFormatChanged) {
                            Operation._ProgressRateAverageFormat = progressRateAverageFormat;
                        }
                    }
                    var formatProviderChanged = false;
                    if (formatProvider is not null) {
                        formatProviderChanged =
                            !ReferenceEquals(Operation._FormatProvider, formatProvider);
                        if (formatProviderChanged) {
                            Operation._FormatProvider = formatProvider;
                        }
                    }
                    var notNone = Operation._ProgressKind != OperationProgressKind.None;
                    var progressDisplayFormatChanged =
                        kindChanged || (notNone && (progressFormatChanged || formatProviderChanged));
                    var targetDisplayFormatChanged =
                        kindChanged || (notNone && (targetFormatChanged || formatProviderChanged));
                    var progressUsesDefault = Operation._ProgressString is null && progressString is null;
                    var targetUsesDefault = Operation._TargetString is null && targetString is null;
                    Operation.ChangeProgress(addProgress: addProgress,
                                             setProgress: setProgress,
                                             addTarget: addTarget,
                                             setTarget: setTarget);
                    Operation.Change(ref Operation._ProgressString, progressString, ProgressStringEvent);
                    Operation.Change(ref Operation._TargetString, targetString, TargetStringEvent);
                    var notifyProgressStringChanged = progressDisplayFormatChanged &&
                                                      progressUsesDefault &&
                                                      !setProgress.HasValue &&
                                                      !addProgress.HasValue;
                    if (notifyProgressStringChanged) {
                        Operation.NotifyPropertyChanged(ProgressStringEvent);
                    }
                    var notifyTargetStringChanged = targetDisplayFormatChanged &&
                                                    targetUsesDefault &&
                                                    !setTarget.HasValue &&
                                                    !addTarget.HasValue;
                    if (notifyTargetStringChanged) {
                        Operation.NotifyPropertyChanged(TargetStringEvent);
                    }
                    if (progressRateFormatChanged || formatProviderChanged || kindChanged) {
                        Operation.NotifyPropertyChanged(ProgressRateStringEvent);
                    }
                    if (progressRateAverageFormatChanged || formatProviderChanged || kindChanged) {
                        Operation.NotifyPropertyChanged(ProgressRateAverageStringEvent);
                    }
                    if (addProgress.HasValue || setProgress.HasValue) {
                        Operation.RequestRateSampler();
                    }
                });
            }
            catch (Exception ex) {
                Operation.MarkInfrastructureBoundaryFailure(ex);
                throw;
            }
        }

        public Task<OperationResult> Child(
            string name,
            OperationDelegate task,
            OperationFailureBehavior failureBehavior,
            CancellationToken cancellationToken) =>
            Operation.Child(name, task, failureBehavior, cancellationToken);

        public Task<OperationBatchResult> Children<T>(
            IEnumerable<T> source,
            Func<T, OperationChild> childFactory,
            OperationFailureBehavior failureBehavior,
            CancellationToken cancellationToken) =>
            Operation.Children(source, childFactory, failureBehavior, cancellationToken);
    }

    private sealed class ProgressChangeContext {
        public Queue<(OperationBase Operation, bool Target, bool Progress)> Notifications { get; } = [];
        public int Depth { get; set; }
        public bool Dispatching { get; set; }
    }
}

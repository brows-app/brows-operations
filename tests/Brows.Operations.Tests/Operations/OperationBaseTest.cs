using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Brows.Operations;

[TestFixture]
internal sealed class OperationBaseTest {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static Operation Start(IOperator @operator, OperationDelegate task, Action<Operation> beforeStart = null) {
        Operation operation = null;
        new OperationManager((OperationCollection)@operator.Operations)
            .Operate("root", task, started => {
                operation = started;
                beforeStart?.Invoke(started);
            });
        return operation;
    }

    private static Task CancelThroughSuppliedToken(IOperationProgress progress,
                                                   CancellationTokenSource source,
                                                   OperationCanceledException cancellation) =>
        progress.Child("canceled", (_, _) => {
            source.Cancel();
            return Task.FromException(cancellation);
        }, OperationFailureBehavior.Record, source.Token);

    private static T GetPrivateField<T>(OperationBase operation, string fieldName) {
        var field = typeof(OperationBase).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return (T)field.GetValue(operation);
    }

    private static bool IsDisposed(CancellationTokenSource tokenSource) {
        try {
            _ = tokenSource.Token;
            return false;
        }
        catch (ObjectDisposedException) {
            return true;
        }
    }

#if NET8_0_OR_GREATER
    private static async Task<long> MeasureCancellationFailureAggregation(int count) {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var callbackFailures = Enumerable.Range(0, count)
            .Select(index => new IOException($"callback failure {index}"))
            .ToArray();
        var callbackOrder = new Exception[count];
        var callbacksRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationFailure = new IOException("cancel notification");
        var callbackIndex = 0;
        CancellationToken operationToken = default;
        var operation = Start(new Operator(), (_, token) => {
            operationToken = token;
            for (var index = 0; index < callbackFailures.Length; index++) {
                var callbackFailure = callbackFailures[index];
                token.Register(() => {
                    callbackOrder[callbackIndex++] = callbackFailure;
                    throw callbackFailure;
                });
            }
            callbacksRegistered.TrySetResult();
            return release.Task;
        }, started => ((OperationBase)started).DisableRateSampling());
        operation.PropertyChanged += (_, args) => {
            if (args.PropertyName == nameof(OperationBase.Canceling)) {
                throw notificationFailure;
            }
        };
        await callbacksRegistered.Task.WaitAsync(Timeout);

        Exception observed = null;
        var allocatedBytes = 0L;
        try {
            var before = GC.GetAllocatedBytesForCurrentThread();
            try { operation.Cancel(); }
            catch (Exception ex) { observed = ex; }
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        finally {
            release.TrySetResult();
            await ((OperationBase)operation).Completion.WaitAsync(Timeout);
        }

        Assert.That(observed, Is.InstanceOf<OperationInfrastructureAggregateException>());
        var failures = ((OperationInfrastructureAggregateException)observed).InnerExceptions;
        using (Assert.EnterMultipleScope()) {
            Assert.That(failures, Has.Count.EqualTo(count + 1));
            Assert.That(failures[0], Is.SameAs(notificationFailure));
            Assert.That(operationToken.IsCancellationRequested, Is.True);
            Assert.That(callbackIndex, Is.EqualTo(count));
            for (var index = 0; index < callbackOrder.Length; index++) {
                Assert.That(failures[index + 1], Is.SameAs(callbackOrder[index]));
            }
        }
        return allocatedBytes;
    }

    private static long MeasureWorkFailureCaptureTail(int count, bool useBatch) {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var failures = Enumerable.Range(0, count)
            .Select(index => new IOException($"failure {index}"))
            .ToArray();
        var captureStartBytes = 0L;
        var captureThreadId = 0;
        var operationThreadId = Thread.CurrentThread.ManagedThreadId;
        var operation = Start(new Operator(), (progress, _) => {
            Task task;
            if (useBatch) {
                task = progress.Children(Enumerable.Range(0, count), index => new OperationChild(
                    "child", (_, _) => Task.FromException(failures[index])), OperationFailureBehavior.Throw, default);
            }
            else {
                task = Task.WhenAll(Enumerable.Range(0, count).Select(index => progress.Child(
                    "child", (_, _) => Task.FromException(failures[index]), OperationFailureBehavior.Throw, default)));
            }
            captureThreadId = Thread.CurrentThread.ManagedThreadId;
            captureStartBytes = GC.GetAllocatedBytesForCurrentThread();
            return task;
        }, started => ((OperationBase)started).DisableRateSampling());

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - captureStartBytes;
        var completedSuccessfully = operation.Completion.IsCompletedSuccessfully;
        var completionThreadId = Thread.CurrentThread.ManagedThreadId;
        var childErrors = ((IEnumerable)operation.ChildSource)
            .Cast<OperationBase>()
            .Select(child => child.Error)
            .ToArray();
        using (Assert.EnterMultipleScope()) {
            Assert.That(completedSuccessfully, Is.True);
            Assert.That(captureThreadId, Is.EqualTo(operationThreadId));
            Assert.That(completionThreadId, Is.EqualTo(operationThreadId));
            Assert.That(operation.CompleteWithError, Is.True);
            Assert.That(operation.Error, Is.Null);
            Assert.That(childErrors, Has.Length.EqualTo(failures.Length));
            for (var index = 0; index < failures.Length; index++) {
                Assert.That(childErrors[index], Is.SameAs(failures[index]));
            }
        }
        return allocatedBytes;
    }

    private static void AssertWorkFailureCaptureScales(bool useBatch) {
        _ = MeasureWorkFailureCaptureTail(128, useBatch);
        var smallerBatch = MeasureWorkFailureCaptureTail(512, useBatch);
        var largerBatch = MeasureWorkFailureCaptureTail(1024, useBatch);

        Assert.That(largerBatch, Is.LessThan(smallerBatch * 3),
            $"Doubling the failure count allocated {smallerBatch} bytes then {largerBatch} bytes.");
    }

#endif

    private static async Task<double> WaitForPositiveRate(OperationBase operation) {
        var sampled = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) => {
            if (args.PropertyName == nameof(OperationBase.ProgressRate) && operation.ProgressRate > 0) {
                sampled.TrySetResult(operation.ProgressRate);
            }
        };
        operation.PropertyChanged += handler;
        try {
            if (operation.ProgressRate > 0) {
                sampled.TrySetResult(operation.ProgressRate);
            }
            return await sampled.Task.WaitAsync(TimeSpan.FromSeconds(4));
        }
        finally {
            operation.PropertyChanged -= handler;
        }
    }

    private static async Task<OperationBase> WaitForChild(OperationBase operation) {
        var children = (IList)operation.ChildSource;
        if (children.Count == 0) {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            NotifyCollectionChangedEventHandler handler = (_, _) => {
                if (children.Count > 0) {
                    changed.TrySetResult();
                }
            };
            var source = (INotifyCollectionChanged)children;
            source.CollectionChanged += handler;
            try {
                if (children.Count == 0) {
                    await changed.Task.WaitAsync(Timeout);
                }
            }
            finally {
                source.CollectionChanged -= handler;
            }
        }
        return children.Cast<OperationBase>().Single();
    }

    private static Task ThrowAtOriginalWorkSite(Exception exception) {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        throw new InvalidOperationException("ExceptionDispatchInfo.Throw returned unexpectedly.");
    }

    private static async Task ThrowAtOriginalWorkSiteAfterYield(Exception exception) {
        await Task.Yield();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        throw new InvalidOperationException("ExceptionDispatchInfo.Throw returned unexpectedly.");
    }

#if NET8_0_OR_GREATER
    [Test]
    public void Children_ThrowFailureCaptureAllocationsScaleLinearly() =>
        AssertWorkFailureCaptureScales(useBatch: true);

    [Test]
    public void Child_ThrowTaskWhenAllFailureCaptureAllocationsScaleLinearly() =>
        AssertWorkFailureCaptureScales(useBatch: false);

    [Test]
    public async Task Cancel_NotificationFailureAggregationAllocationsScaleLinearly() {
        _ = await MeasureCancellationFailureAggregation(16);
        var smallerBatch = await MeasureCancellationFailureAggregation(256);
        var largerBatch = await MeasureCancellationFailureAggregation(512);

        Assert.That(largerBatch, Is.LessThan(smallerBatch * 3),
            $"Doubling the callback failure count allocated {smallerBatch} bytes then {largerBatch} bytes.");
    }
#endif

    [TestCase(OperationFailureBehavior.Record)]
    [TestCase(OperationFailureBehavior.Throw)]
    public async Task Child_ReturnsSucceededForSuccessfulWork(OperationFailureBehavior failureBehavior) {
        IOperator @operator = new Operator();
        OperationResult result = null;
        var operation = Start(@operator, async (progress, _) => {
            result = await progress.Child("child", (_, _) => Task.CompletedTask,
                                         failureBehavior, default);
        });

        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(result.Kind, Is.EqualTo(OperationResultKind.Succeeded));
            Assert.That(result.Error, Is.Null);
            Assert.That(operation.CompleteWithError, Is.False);
        }
    }

    [TestCase(OperationFailureBehavior.Record, false)]
    [TestCase(OperationFailureBehavior.Record, true)]
    [TestCase(OperationFailureBehavior.Throw, false)]
    [TestCase(OperationFailureBehavior.Throw, true)]
    public async Task Child_FailurePolicyPreservesExceptionAndWorkStack(
        OperationFailureBehavior failureBehavior,
        bool synchronousFailure) {
        IOperator @operator = new Operator();
        var failure = new IOException("child work");
        OperationResult result = null;
        Exception propagated = null;
        OperationDelegate task = (_, _) => synchronousFailure
            ? ThrowAtOriginalWorkSite(failure)
            : ThrowAtOriginalWorkSiteAfterYield(failure);
        var operation = Start(@operator, async (progress, _) => {
            try {
                result = await progress.Child("child", task, failureBehavior, default);
            }
            catch (Exception ex) {
                propagated = ex;
            }
        });

        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(operation.CompleteWithError, Is.True);
            if (failureBehavior == OperationFailureBehavior.Record) {
                Assert.That(result.Kind, Is.EqualTo(OperationResultKind.Failed));
                Assert.That(result.Error, Is.SameAs(failure));
                Assert.That(propagated, Is.Null);
            }
            else {
                Assert.That(result, Is.Null);
                Assert.That(propagated, Is.SameAs(failure));
            }
            if (synchronousFailure) {
                Assert.That(failure.StackTrace, Does.Contain(nameof(ThrowAtOriginalWorkSite)));
            }
            else {
                Assert.That(failure.StackTrace, Does.Contain(nameof(ThrowAtOriginalWorkSiteAfterYield)));
            }
        }
    }

    [TestCase(OperationFailureBehavior.Record)]
    [TestCase(OperationFailureBehavior.Throw)]
    public async Task ChildOnlyCancellation_UsesPolicyAndPreservesOriginalException(
        OperationFailureBehavior failureBehavior) {
        IOperator @operator = new Operator();
        using var cancellationSource = new CancellationTokenSource();
        var cancellation = new OperationCanceledException("child canceled", cancellationSource.Token);
        OperationResult result = null;
        Exception propagated = null;
        var operation = Start(@operator, async (progress, _) => {
            try {
                result = await progress.Child("child",
                                              (childProgress, _) => CancelThroughSuppliedToken(
                                                  childProgress,
                                                  cancellationSource,
                                                  cancellation),
                                              failureBehavior,
                                              default);
            }
            catch (Exception ex) {
                propagated = ex;
            }
        });

        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(operation.CompleteWithError, Is.False);
            if (failureBehavior == OperationFailureBehavior.Record) {
                Assert.That(result.Kind, Is.EqualTo(OperationResultKind.Canceled));
                Assert.That(result.Error, Is.SameAs(cancellation));
                Assert.That(propagated, Is.Null);
            }
            else {
                Assert.That(result, Is.Null);
                Assert.That(propagated, Is.SameAs(cancellation));
            }
        }
    }

    [TestCase(OperationFailureBehavior.Record)]
    [TestCase(OperationFailureBehavior.Throw)]
    public async Task Child_TimeoutOnUnknownTokenIsAFailure(OperationFailureBehavior failureBehavior) {
        IOperator @operator = new Operator();
        OperationResult result = null;
        Exception propagated = null;
        var operation = Start(@operator, async (progress, _) => {
            try {
                result = await progress.Child("timeout", async (_, _) => {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, timeout.Token);
                }, failureBehavior, default);
            }
            catch (Exception ex) {
                propagated = ex;
            }
        });

        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(operation.CompleteWithError, Is.True);
            if (failureBehavior == OperationFailureBehavior.Record) {
                Assert.That(result.Kind, Is.EqualTo(OperationResultKind.Failed));
                Assert.That(result.Error, Is.InstanceOf<OperationCanceledException>());
                Assert.That(propagated, Is.Null);
            }
            else {
                Assert.That(result, Is.Null);
                Assert.That(propagated, Is.InstanceOf<OperationCanceledException>());
            }
        }
    }

    [Test]
    public async Task Children_TimeoutOnUnknownTokenIsAFailedOutcome() {
        IOperator @operator = new Operator();
        OperationBatchResult result = null;
        var operation = Start(@operator, async (progress, _) => {
            result = await progress.Children(new[] { 1 }, _ => new OperationChild("timeout", async (_, _) => {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, timeout.Token);
            }), OperationFailureBehavior.Record, default);
        });

        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(result.FailedCount, Is.EqualTo(1));
            Assert.That(result.CanceledCount, Is.Zero);
            Assert.That(operation.CompleteWithError, Is.True);
        }
    }

    [Test]
    public async Task Children_FactoryCancellationOnUnknownTokenIsAnError() {
        var @operator = await Task.Run(() => new Operator());
        using var unknownSource = new CancellationTokenSource();
        unknownSource.Cancel();
        var timeout = new OperationCanceledException("factory timeout", unknownSource.Token);
        Operation root = null;
        Exception publicBoundaryObserved = null;
        Exception observed = null;
        try {
            await UiTestThread.Run(async () => {
                root = Start(@operator, async (progress, _) => {
                    try {
                        await progress.Children(new[] { 1 },
                                                _ => throw timeout,
                                                OperationFailureBehavior.Record,
                                                default);
                    }
                    catch (Exception ex) {
                        publicBoundaryObserved = ex;
                        throw;
                    }
                });
                try {
                    await root.Completion.WaitAsync(Timeout);
                }
                catch (Exception) {
                }
            });
        }
        catch (Exception ex) {
            observed = ex;
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(publicBoundaryObserved, Is.SameAs(timeout));
            Assert.That(root.Completion.IsFaulted, Is.True);
            Assert.That(observed, Is.SameAs(timeout));
        }
    }

    [TestCase(OperationFailureBehavior.Record)]
    [TestCase(OperationFailureBehavior.Throw)]
    public async Task Child_SuppliedCancellationBeforeStartupAlwaysPropagates(
        OperationFailureBehavior failureBehavior) {
        IOperator @operator = new Operator();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var childInvoked = false;
        OperationCanceledException propagated = null;
        var operation = Start(@operator, async (progress, _) => {
            try {
                await progress.Child("child", (_, _) => {
                    childInvoked = true;
                    return Task.CompletedTask;
                }, failureBehavior, cancellationSource.Token);
            }
            catch (OperationCanceledException ex) {
                propagated = ex;
            }
        });

        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(childInvoked, Is.False);
            Assert.That(propagated, Is.Not.Null);
            Assert.That(propagated.CancellationToken, Is.EqualTo(cancellationSource.Token));
            Assert.That(operation.CompleteWithError, Is.False);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Child_ThrowIncludesAwaitedAndUnawaitedDescendantFailures(bool awaitDescendant) {
        IOperator @operator = new Operator();
        var failure = new IOException("descendant failure");
        Exception propagated = null;
        var operation = Start(@operator, async (progress, _) => {
            try {
                await progress.Child("parent", async (parentProgress, _) => {
                    var descendant = parentProgress.Child("descendant", (_, _) => Task.FromException(failure),
                        OperationFailureBehavior.Record, default);
                    if (awaitDescendant) {
                        await descendant;
                    }
                }, OperationFailureBehavior.Throw, default);
            }
            catch (Exception ex) {
                propagated = ex;
            }
        });

        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(propagated, Is.SameAs(failure));
            Assert.That(operation.CompleteWithError, Is.True);
            Assert.That(operation.Error, Is.Null);
        }
    }

    [Test]
    public async Task Child_ThrowAggregatesDistinctFailuresAndDeduplicatesRepeatedPropagation() {
        IOperator @operator = new Operator();
        var firstFailure = new IOException("same descendant failure");
        var secondFailure = new IOException("same descendant failure");
        Exception propagated = null;
        var operation = Start(@operator, async (progress, _) => {
            try {
                await progress.Child("parent", async (parentProgress, _) => {
                    await parentProgress.Child("first", (_, _) => Task.FromException(firstFailure),
                        OperationFailureBehavior.Record, default);
                    await parentProgress.Child("second", (_, _) => Task.FromException(secondFailure),
                        OperationFailureBehavior.Record, default);
                    throw firstFailure;
                }, OperationFailureBehavior.Throw, default);
            }
            catch (Exception ex) {
                propagated = ex;
            }
        });

        await operation.Completion.WaitAsync(Timeout);

        Assert.That(propagated, Is.InstanceOf<AggregateException>());
        var aggregate = (AggregateException)propagated;
        using (Assert.EnterMultipleScope()) {
            Assert.That(aggregate.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(aggregate.InnerExceptions[0], Is.SameAs(firstFailure));
            Assert.That(aggregate.InnerExceptions[1], Is.SameAs(secondFailure));
            Assert.That(operation.CompleteWithError, Is.True);
        }
    }

    [Test]
    public async Task Child_PropagatesLibraryAggregatesThroughNestedThrowBoundariesWithoutWrappers() {
        IOperator @operator = new Operator();
        var firstFailure = new IOException("same nested failure");
        var secondFailure = new IOException("same nested failure");
        Exception propagated = null;
        var operation = Start(@operator, async (progress, _) => {
            try {
                await progress.Child("outer", async (outerProgress, _) => {
                    await outerProgress.Child("middle", async (middleProgress, _) => {
                        await middleProgress.Child("first", (_, _) => Task.FromException(firstFailure),
                            OperationFailureBehavior.Record, default);
                        await middleProgress.Child("second", (_, _) => Task.FromException(secondFailure),
                            OperationFailureBehavior.Record, default);
                    }, OperationFailureBehavior.Throw, default);
                }, OperationFailureBehavior.Throw, default);
            }
            catch (Exception ex) {
                propagated = ex;
            }
        });

        await operation.Completion.WaitAsync(Timeout);

        Assert.That(propagated, Is.InstanceOf<AggregateException>());
        var aggregate = (AggregateException)propagated;
        using (Assert.EnterMultipleScope()) {
            Assert.That(aggregate.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(aggregate.InnerExceptions[0], Is.SameAs(firstFailure));
            Assert.That(aggregate.InnerExceptions[1], Is.SameAs(secondFailure));
            Assert.That(aggregate.InnerExceptions, Has.None.InstanceOf<AggregateException>());
        }
    }

    [Test]
    public async Task Children_RecordMarksIndependentSiblingsFailedForReusedException() {
        IOperator @operator = new Operator();
        var failure = new IOException("reused sibling failure");
        OperationBatchResult result = null;
        var operation = Start(@operator, async (progress, _) => {
            result = await progress.Children(new[] { 1, 2 }, item => new OperationChild(
                item.ToString(), (_, _) => Task.FromException(failure)), OperationFailureBehavior.Record, default);
        });

        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(result.FailedCount, Is.EqualTo(2));
            Assert.That(result.Results.Select(item => item.Kind),
                Is.EqualTo(new[] { OperationResultKind.Failed, OperationResultKind.Failed }));
            Assert.That(result.Results.Select(item => item.Error),
                Is.EqualTo(new[] { failure, failure }));
        }
    }

    [Test]
    public async Task Child_RecordPreservesUserAggregateAndCollectsMultipleTaskFaults() {
        IOperator @operator = new Operator();
        var firstFailure = new IOException("first task failure");
        var secondFailure = new IOException("second task failure");
        var userAggregate = new AggregateException("user aggregate", firstFailure, secondFailure);
        OperationResult aggregateResult = null;
        OperationResult multipleFaultResult = null;
        var operation = Start(@operator, async (progress, _) => {
            aggregateResult = await progress.Child("aggregate", (_, _) => Task.FromException(userAggregate),
                OperationFailureBehavior.Record, default);
            multipleFaultResult = await progress.Child("multiple", (_, _) => Task.WhenAll(
                Task.FromException(firstFailure), Task.FromException(secondFailure)),
                OperationFailureBehavior.Record, default);
        });

        await operation.Completion.WaitAsync(Timeout);

        Assert.That(multipleFaultResult.Error, Is.InstanceOf<AggregateException>());
        var multipleFaults = (AggregateException)multipleFaultResult.Error;
        using (Assert.EnterMultipleScope()) {
            Assert.That(aggregateResult.Error, Is.SameAs(userAggregate));
            Assert.That(multipleFaults.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(multipleFaults.InnerExceptions[0], Is.SameAs(firstFailure));
            Assert.That(multipleFaults.InnerExceptions[1], Is.SameAs(secondFailure));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Child_WaitsForAsynchronousCleanupAndUsesItsFinalOutcome(bool cleanupFails) {
        IOperator @operator = new Operator();
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new IOException("cleanup failure");
        Task<OperationResult> childTask = null;
        var operation = Start(@operator, async (progress, _) => {
            childTask = progress.Child("cleanup", async (_, _) => {
                try {
                    cleanupStarted.TrySetResult();
                }
                finally {
                    await releaseCleanup.Task;
                    if (cleanupFails) {
                        throw cleanupFailure;
                    }
                }
            }, OperationFailureBehavior.Record, default);
            await childTask;
        });

        try {
            await cleanupStarted.Task.WaitAsync(Timeout);
            Assert.That(childTask.IsCompleted, Is.False);
        }
        finally {
            releaseCleanup.TrySetResult();
        }

        var result = await childTask.WaitAsync(Timeout);
        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(
                result.Kind,
                Is.EqualTo(cleanupFails ? OperationResultKind.Failed : OperationResultKind.Succeeded));
            Assert.That(result.Error, Is.SameAs(cleanupFails ? cleanupFailure : null));
            Assert.That(operation.CompleteWithError, Is.EqualTo(cleanupFails));
        }
    }

    [TestCase(OperationFailureBehavior.Record)]
    [TestCase(OperationFailureBehavior.Throw)]
    public async Task Child_SuppliedCancellationCancelsOnlyItsSubtree(OperationFailureBehavior failureBehavior) {
        IOperator @operator = new Operator();
        using var suppliedCancellation = new CancellationTokenSource();
        var childStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var grandchildStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGrandchild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingResult = (OperationResult)null;
        var grandchildBoundaryCancellation = (OperationCanceledException)null;
        var childCancellation = (OperationCanceledException)null;
        var operationToken = default(CancellationToken);
        var operation = Start(@operator, async (progress, token) => {
            operationToken = token;
            var child = progress.Child("supplied subtree", async (childProgress, childToken) => {
                childStarted.TrySetResult(childToken);
                try {
                    await childProgress.Child("grandchild", async (_, grandchildToken) => {
                        grandchildStarted.TrySetResult(grandchildToken);
                        await releaseGrandchild.Task;
                    }, OperationFailureBehavior.Record, default);
                }
                catch (OperationCanceledException ex) {
                    grandchildBoundaryCancellation = ex;
                }
            }, failureBehavior, suppliedCancellation.Token);
            siblingResult = await progress.Child("sibling", (_, siblingToken) => {
                siblingStarted.TrySetResult(siblingToken);
                return Task.CompletedTask;
            }, OperationFailureBehavior.Record, default);
            try {
                await child;
            }
            catch (OperationCanceledException ex) {
                childCancellation = ex;
            }
        });

        try {
            var childToken = await childStarted.Task.WaitAsync(Timeout);
            var grandchildToken = await grandchildStarted.Task.WaitAsync(Timeout);
            var siblingToken = await siblingStarted.Task.WaitAsync(Timeout);
            Assert.That(siblingToken.IsCancellationRequested, Is.False);
            suppliedCancellation.Cancel();
            Assert.That(grandchildToken.IsCancellationRequested, Is.True);
            releaseGrandchild.TrySetResult();
            await operation.Completion.WaitAsync(Timeout);

            using (Assert.EnterMultipleScope()) {
                Assert.That(childToken.IsCancellationRequested, Is.True);
                Assert.That(grandchildToken.IsCancellationRequested, Is.True);
                Assert.That(siblingToken.IsCancellationRequested, Is.False);
                Assert.That(operationToken.IsCancellationRequested, Is.False);
                Assert.That(grandchildBoundaryCancellation, Is.Not.Null);
                Assert.That(grandchildBoundaryCancellation.CancellationToken, Is.EqualTo(childToken));
                Assert.That(siblingResult.Kind, Is.EqualTo(OperationResultKind.Succeeded));
                Assert.That(childCancellation, Is.Not.Null);
                Assert.That(childCancellation.CancellationToken, Is.EqualTo(suppliedCancellation.Token));
                Assert.That(operation.CompleteWithError, Is.False);
            }
        }
        finally {
            suppliedCancellation.Cancel();
            releaseGrandchild.TrySetResult();
            try {
                await operation.Completion.WaitAsync(Timeout);
            }
            catch (Exception) {
            }
        }
    }

    [TestCase(OperationFailureBehavior.Record)]
    [TestCase(OperationFailureBehavior.Throw)]
    public async Task ChildRegistration_ObservesSuppliedCancellationBeforeLinkedCallbacks(
        OperationFailureBehavior failureBehavior) {
        IOperator @operator = new Operator();
        using var suppliedCancellation = new CancellationTokenSource();
        var parentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childRuns = 0;
        var factoryCalls = 0;
        var parentToken = default(CancellationToken);
        IOperationProgress parentProgress = null;
        Task<OperationResult> lateChildBeforeLink = null;
        Task<OperationBatchResult> lateBatchBeforeLink = null;
        Task<OperationResult> lateChildAfterLink = null;
        Task<OperationBatchResult> lateBatchAfterLink = null;
        OperationCanceledException childBeforeLinkCancellation = null;
        OperationCanceledException batchBeforeLinkCancellation = null;
        OperationCanceledException childAfterLinkCancellation = null;
        OperationCanceledException batchAfterLinkCancellation = null;
        var operation = Start(@operator, async (progress, _) => {
            suppliedCancellation.Token.Register(() => {
                lateChildAfterLink = parentProgress.Child("late child after link", (_, _) => {
                    childRuns++;
                    return Task.CompletedTask;
                }, failureBehavior, default);
                lateBatchAfterLink = parentProgress.Children(new[] { 1 }, _ => {
                    factoryCalls++;
                    return new OperationChild("late batch child after link", (_, _) => Task.CompletedTask);
                }, failureBehavior, default);
            });
            try {
                await progress.Child("supplied parent", (savedProgress, suppliedToken) => {
                    parentProgress = savedProgress;
                    parentToken = suppliedToken;
                    suppliedCancellation.Token.Register(() => {
                        lateChildBeforeLink = savedProgress.Child("late child before link", (_, _) => {
                            childRuns++;
                            return Task.CompletedTask;
                        }, failureBehavior, default);
                        lateBatchBeforeLink = savedProgress.Children(new[] { 1 }, _ => {
                            factoryCalls++;
                            return new OperationChild("late batch child", (_, _) => Task.CompletedTask);
                        }, failureBehavior, default);
                    });
                    parentStarted.TrySetResult();
                    return releaseParent.Task;
                }, OperationFailureBehavior.Record, suppliedCancellation.Token);
            }
            catch (OperationCanceledException) {
            }
        });

        try {
            await parentStarted.Task.WaitAsync(Timeout);
            suppliedCancellation.Cancel();
            Assert.That(lateChildBeforeLink, Is.Not.Null);
            Assert.That(lateBatchBeforeLink, Is.Not.Null);
            Assert.That(lateChildAfterLink, Is.Not.Null);
            Assert.That(lateBatchAfterLink, Is.Not.Null);
            releaseParent.TrySetResult();
            await operation.Completion.WaitAsync(Timeout);
            try { await lateChildBeforeLink; }
            catch (OperationCanceledException ex) { childBeforeLinkCancellation = ex; }
            try { await lateBatchBeforeLink; }
            catch (OperationCanceledException ex) { batchBeforeLinkCancellation = ex; }
            try { await lateChildAfterLink; }
            catch (OperationCanceledException ex) { childAfterLinkCancellation = ex; }
            try { await lateBatchAfterLink; }
            catch (OperationCanceledException ex) { batchAfterLinkCancellation = ex; }
        }
        finally {
            releaseParent.TrySetResult();
            suppliedCancellation.Cancel();
            await operation.Completion.WaitAsync(Timeout);
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(childBeforeLinkCancellation?.CancellationToken, Is.EqualTo(suppliedCancellation.Token));
            Assert.That(batchBeforeLinkCancellation?.CancellationToken, Is.EqualTo(suppliedCancellation.Token));
            Assert.That(parentToken.IsCancellationRequested, Is.True);
            Assert.That(childAfterLinkCancellation?.CancellationToken, Is.EqualTo(parentToken));
            Assert.That(batchAfterLinkCancellation?.CancellationToken, Is.EqualTo(parentToken));
            Assert.That(childRuns, Is.Zero);
            Assert.That(factoryCalls, Is.Zero);
        }
    }

    [Test]
    public async Task CompletedSuppliedCanceledParent_ChildAndBatchRetainCancellationToken() {
        IOperator @operator = new Operator();
        using var suppliedCancellation = new CancellationTokenSource();
        var parentStarted = new TaskCompletionSource<(IOperationProgress Progress, CancellationToken Token)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        var childRuns = 0;
        var operation = Start(@operator, async (progress, _) => {
            try {
                await progress.Child("supplied parent", (parentProgress, parentToken) => {
                    parentStarted.TrySetResult((parentProgress, parentToken));
                    return releaseParent.Task;
                }, OperationFailureBehavior.Record, suppliedCancellation.Token);
            }
            catch (OperationCanceledException) {
            }
        });
        var parent = await parentStarted.Task.WaitAsync(Timeout);
        suppliedCancellation.Cancel();
        releaseParent.TrySetResult();
        await operation.Completion.WaitAsync(Timeout);

        OperationCanceledException childCancellation = null;
        OperationCanceledException batchCancellation = null;
        try {
            await parent.Progress.Child("late child", (_, _) => {
                childRuns++;
                return Task.CompletedTask;
            }, OperationFailureBehavior.Record, default);
        }
        catch (OperationCanceledException ex) { childCancellation = ex; }
        try {
            await parent.Progress.Children(new[] { 1 }, _ => {
                factoryCalls++;
                return new OperationChild("late batch child", (_, _) => Task.CompletedTask);
            }, OperationFailureBehavior.Record, default);
        }
        catch (OperationCanceledException ex) { batchCancellation = ex; }

        using (Assert.EnterMultipleScope()) {
            Assert.That(parent.Token.IsCancellationRequested, Is.True);
            Assert.That(childCancellation?.CancellationToken, Is.EqualTo(parent.Token));
            Assert.That(batchCancellation?.CancellationToken, Is.EqualTo(parent.Token));
            Assert.That(childRuns, Is.Zero);
            Assert.That(factoryCalls, Is.Zero);
        }
    }

    [Test]
    public Task Child_AncestorSuppliedCancellationFreezesBeforeLinkedCallbacks() => Task.Run(async () => {
        IOperator @operator = new Operator();
        using var suppliedCancellation = new CancellationTokenSource();
        var grandchildReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGrandchild = new TaskCompletionSource(TaskCreationOptions.None);
        suppliedCancellation.Token.Register(() => releaseGrandchild.TrySetResult());
        Task<OperationResult> grandchildTask = null;
        var operation = Start(@operator, async (progress, _) => {
            try {
                await progress.Child("supplied parent", (parentProgress, _) => {
                    suppliedCancellation.Token.Register(() => releaseGrandchild.TrySetResult());
                    grandchildTask = parentProgress.Child("grandchild", (_, _) => {
                        grandchildReady.TrySetResult();
                        return releaseGrandchild.Task;
                    }, OperationFailureBehavior.Record, default);
                    return Task.CompletedTask;
                }, OperationFailureBehavior.Record, suppliedCancellation.Token);
            }
            catch (OperationCanceledException) {
            }
        });

        await grandchildReady.Task.WaitAsync(Timeout);
        suppliedCancellation.Cancel();
        await operation.Completion.WaitAsync(Timeout);

        OperationResult result = null;
        OperationCanceledException observed = null;
        try { result = await grandchildTask; }
        catch (OperationCanceledException ex) { observed = ex; }

        using (Assert.EnterMultipleScope()) {
            Assert.That(result, Is.Null);
            Assert.That(observed?.CancellationToken, Is.EqualTo(suppliedCancellation.Token));
        }
    });

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public Task ChildOrBatch_SuppliedCancellationLeasesInlineDescendantCallbacks(
        bool useBatch,
        bool cancelRoot) => Task.Run(async () => {
        IOperator @operator = new Operator();
        using var suppliedCancellation = new CancellationTokenSource();
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grandchildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childRelease = new TaskCompletionSource(TaskCreationOptions.None);
        var grandchildRelease = new TaskCompletionSource(TaskCreationOptions.None);
        var childCallbackError = (Exception)null;
        var grandchildCallbackError = (Exception)null;
        var childCompletedInsideCallback = false;
        var grandchildCompletedInsideCallback = false;
        Operation root = null;
        root = Start(@operator, async (progress, ignoredRootToken) => {
            OperationDelegate childTask = (childProgress, childToken) => {
                childToken.Register(() => {
                    childRelease.TrySetResult();
                    childCompletedInsideCallback = ((IEnumerable)root.ChildSource)
                        .Cast<OperationBase>().Single().Complete;
                    try { _ = childToken.WaitHandle; }
                    catch (Exception ex) { childCallbackError = ex; }
                });
                var grandchildTask = childProgress.Child("grandchild", (grandchildProgress, grandchildToken) => {
                    grandchildToken.Register(() => {
                        grandchildRelease.TrySetResult();
                        var child = ((IEnumerable)root.ChildSource).Cast<OperationBase>().Single();
                        grandchildCompletedInsideCallback = ((IEnumerable)child.ChildSource)
                            .Cast<OperationBase>().Single().Complete;
                        try { _ = grandchildToken.WaitHandle; }
                        catch (Exception ex) { grandchildCallbackError = ex; }
                    });
                    grandchildStarted.TrySetResult();
                    return grandchildRelease.Task;
                }, OperationFailureBehavior.Record, default);
                _ = grandchildTask.ContinueWith(
                    completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                childStarted.TrySetResult();
                return childRelease.Task;
            };

            try {
                if (useBatch) {
                    await progress.Children(new[] { 1 }, _ => new OperationChild("child", childTask),
                        OperationFailureBehavior.Record, suppliedCancellation.Token);
                }
                else {
                    await progress.Child(
                        "child",
                        childTask,
                        OperationFailureBehavior.Record,
                        suppliedCancellation.Token);
                }
            }
            catch (OperationCanceledException) {
            }
        });

        var childOperation = await WaitForChild((OperationBase)root);
        var grandchildOperation = await WaitForChild(childOperation);
        var childSource = GetPrivateField<CancellationTokenSource>(childOperation, "TokenSource");
        var grandchildSource = GetPrivateField<CancellationTokenSource>(grandchildOperation, "TokenSource");
        await childStarted.Task.WaitAsync(Timeout);
        await grandchildStarted.Task.WaitAsync(Timeout);

        try {
            if (cancelRoot) {
                root.Cancel();
            }
            else {
                suppliedCancellation.Cancel();
            }
            await root.Completion.WaitAsync(Timeout);
        }
        finally {
            childRelease.TrySetResult();
            grandchildRelease.TrySetResult();
            suppliedCancellation.Cancel();
            if (!root.Completion.IsCompleted) {
                await root.Completion.WaitAsync(Timeout);
            }
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(childCompletedInsideCallback, Is.True);
            Assert.That(grandchildCompletedInsideCallback, Is.True);
            Assert.That(childCallbackError, Is.Null);
            Assert.That(grandchildCallbackError, Is.Null);
            Assert.That(GetPrivateField<int>(childOperation, "TokenSourceCancellationCount"), Is.Zero);
            Assert.That(GetPrivateField<int>(grandchildOperation, "TokenSourceCancellationCount"), Is.Zero);
            Assert.That(GetPrivateField<bool>(childOperation, "TokenSourceDisposalPending"), Is.False);
            Assert.That(GetPrivateField<bool>(grandchildOperation, "TokenSourceDisposalPending"), Is.False);
            Assert.That(IsDisposed(childSource), Is.True);
            Assert.That(IsDisposed(grandchildSource), Is.True);
        }
    });

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task Cancel_NotificationFailureStillCancelsAndReleasesEveryTreeLease(
        bool callbackFails,
        bool reuseFailure) {
        IOperator @operator = new Operator();
        var rootStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var childStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var grandchildStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationFailure = new IOException("cancel notification");
        var callbackFailure = reuseFailure ? notificationFailure : new IOException("token callback");
        var callbackCounts = new int[3];
        async Task WaitForCancellationOrRelease(CancellationToken token) {
            var canceled = Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            if (await Task.WhenAny(canceled, release.Task) == canceled) {
                try { await canceled; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }
        var operation = Start(@operator, async (progress, rootToken) => {
            rootToken.Register(() => callbackCounts[0]++);
            rootStarted.TrySetResult(rootToken);
            await progress.Child("child", async (childProgress, childToken) => {
                childToken.Register(() => {
                    callbackCounts[1]++;
                    if (callbackFails) {
                        throw callbackFailure;
                    }
                });
                childStarted.TrySetResult(childToken);
                await childProgress.Child("grandchild", async (_, grandchildToken) => {
                    grandchildToken.Register(() => callbackCounts[2]++);
                    grandchildStarted.TrySetResult(grandchildToken);
                    await WaitForCancellationOrRelease(grandchildToken);
                }, OperationFailureBehavior.Record, default);
                await WaitForCancellationOrRelease(childToken);
            }, OperationFailureBehavior.Record, default);
            await WaitForCancellationOrRelease(rootToken);
        });
        var rootOperation = (OperationBase)operation;
        rootOperation.PropertyChanged += (_, args) => {
            if (args.PropertyName == nameof(OperationBase.Canceling)) {
                throw notificationFailure;
            }
        };

        var rootToken = await rootStarted.Task.WaitAsync(Timeout);
        var childToken = await childStarted.Task.WaitAsync(Timeout);
        var grandchildToken = await grandchildStarted.Task.WaitAsync(Timeout);
        var childOperation = await WaitForChild(rootOperation);
        var grandchildOperation = await WaitForChild(childOperation);
        var tokenSources = new[] { rootOperation, childOperation, grandchildOperation }
            .Select(item => GetPrivateField<CancellationTokenSource>(item, "TokenSource"))
            .ToArray();
        Exception observed = null;
        try { operation.Cancel(); }
        catch (Exception ex) { observed = ex; }
        finally { release.TrySetResult(); }
        await operation.Completion.WaitAsync(Timeout);

        var tokens = new[] { rootToken, childToken, grandchildToken };
        using (Assert.EnterMultipleScope()) {
            Assert.That(tokens.All(token => token.IsCancellationRequested), Is.True);
            Assert.That(callbackCounts, Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(observed, Is.Not.Null);
            if (callbackFails) {
                if (reuseFailure) {
                    Assert.That(observed, Is.SameAs(notificationFailure));
                }
                else {
                    Assert.That(observed, Is.InstanceOf<OperationInfrastructureAggregateException>());
                    var failures = ((OperationInfrastructureAggregateException)observed).InnerExceptions;
                    Assert.That(failures, Has.Count.EqualTo(2));
                    Assert.That(failures[0], Is.SameAs(notificationFailure));
                    Assert.That(failures[1], Is.SameAs(callbackFailure));
                }
            }
            else {
                Assert.That(observed, Is.SameAs(notificationFailure));
            }
            Assert.That(new[] { operation, childOperation, grandchildOperation }.All(item => item.Complete), Is.True);
            Assert.That(new[] { operation, childOperation, grandchildOperation }
                .All(item => GetPrivateField<int>(item, "TokenSourceCancellationCount") == 0), Is.True);
            Assert.That(new[] { operation, childOperation, grandchildOperation }
                .All(item => !GetPrivateField<bool>(item, "TokenSourceDisposalPending")), Is.True);
            Assert.That(tokenSources.All(IsDisposed), Is.True);
        }
    }

    [Test]
    public async Task Cancel_CallbackFailuresWithoutNotificationFailureKeepsFlattenedAggregateShape() {
        var directFailure = new IOException("direct callback");
        var aggregateFirstFailure = new IOException("aggregate first");
        var aggregateSecondFailure = new IOException("aggregate second");
        var callbackAggregate = new AggregateException("callback aggregate", aggregateFirstFailure,
                                                       aggregateSecondFailure);
        var callbackCount = 0;
        var callbacksRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        var operation = Start(new Operator(), (_, token) => {
            operationToken = token;
            token.Register(() => {
                callbackCount++;
                throw directFailure;
            });
            token.Register(() => {
                callbackCount++;
                throw callbackAggregate;
            });
            callbacksRegistered.TrySetResult();
            return release.Task;
        }, started => ((OperationBase)started).DisableRateSampling());
        await callbacksRegistered.Task.WaitAsync(Timeout);

        Exception observed = null;
        try { operation.Cancel(); }
        catch (Exception ex) { observed = ex; }
        finally {
            release.TrySetResult();
            await ((OperationBase)operation).Completion.WaitAsync(Timeout);
        }

        Assert.That(observed, Is.TypeOf<AggregateException>());
        var aggregate = (AggregateException)observed;
        using (Assert.EnterMultipleScope()) {
            Assert.That(operationToken.IsCancellationRequested, Is.True);
            Assert.That(callbackCount, Is.EqualTo(2));
            Assert.That(aggregate.InnerExceptions, Has.Count.EqualTo(3));
            Assert.That(aggregate.InnerExceptions, Does.Contain(aggregateFirstFailure));
            Assert.That(aggregate.InnerExceptions, Does.Contain(aggregateSecondFailure));
            Assert.That(aggregate.InnerExceptions, Does.Contain(directFailure));
            Assert.That(aggregate.InnerExceptions, Has.None.InstanceOf<AggregateException>());
        }
    }

    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    public async Task Cancel_EmptyAggregateCallbackFailureStillPropagatesAndReleasesEveryTreeLease(
        bool callbackOnDescendant,
        bool nestedAggregate,
        bool notificationFails) {
        var rootStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var childStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var grandchildStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFailure = nestedAggregate
            ? new AggregateException("outer callback", new AggregateException("inner callback"))
            : new AggregateException("callback");
        var notificationFailure = new IOException("cancel notification");
        var callbackCounts = new int[3];
        var operation = Start(new Operator(), async (progress, rootToken) => {
            rootToken.Register(() => {
                callbackCounts[0]++;
                if (!callbackOnDescendant) {
                    throw callbackFailure;
                }
            });
            rootStarted.TrySetResult(rootToken);
            await progress.Child("child", async (childProgress, childToken) => {
                childToken.Register(() => {
                    callbackCounts[1]++;
                    if (callbackOnDescendant) {
                        throw callbackFailure;
                    }
                });
                childStarted.TrySetResult(childToken);
                await childProgress.Child("grandchild", async (_, grandchildToken) => {
                    grandchildToken.Register(() => callbackCounts[2]++);
                    grandchildStarted.TrySetResult(grandchildToken);
                    await release.Task;
                }, OperationFailureBehavior.Record, default);
                await release.Task;
            }, OperationFailureBehavior.Record, default);
            await release.Task;
        }, started => ((OperationBase)started).DisableRateSampling());
        var rootOperation = (OperationBase)operation;
        if (notificationFails) {
            rootOperation.PropertyChanged += (_, args) => {
                if (args.PropertyName == nameof(OperationBase.Canceling)) {
                    throw notificationFailure;
                }
            };
        }

        var tokens = new[] {
            await rootStarted.Task.WaitAsync(Timeout),
            await childStarted.Task.WaitAsync(Timeout),
            await grandchildStarted.Task.WaitAsync(Timeout)
        };
        var childOperation = await WaitForChild(rootOperation);
        var grandchildOperation = await WaitForChild(childOperation);
        var operations = new[] { rootOperation, childOperation, grandchildOperation };
        var tokenSources = operations.Select(item => GetPrivateField<CancellationTokenSource>(item, "TokenSource"))
            .ToArray();
        Exception observed = null;
        try { operation.Cancel(); }
        catch (Exception ex) { observed = ex; }
        finally { release.TrySetResult(); }
        await operation.Completion.WaitAsync(Timeout);

        var aggregateFailure = observed as AggregateException;
        using (Assert.EnterMultipleScope()) {
            if (notificationFails) {
                Assert.That(observed, Is.SameAs(notificationFailure));
            }
            else {
                Assert.That(observed, Is.TypeOf<AggregateException>());
                Assert.That(aggregateFailure?.Message,
                    Is.EqualTo("Cancellation of 'root' encountered callback failures."));
                Assert.That(aggregateFailure?.InnerExceptions.Count, Is.Zero);
            }
            Assert.That(tokens.All(token => token.IsCancellationRequested), Is.True);
            Assert.That(callbackCounts, Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(operations.All(item => item.Complete), Is.True);
            Assert.That(
                operations.All(item => GetPrivateField<int>(item, "TokenSourceCancellationCount") == 0),
                Is.True);
            Assert.That(operations.All(item => !GetPrivateField<bool>(item, "TokenSourceDisposalPending")), Is.True);
            Assert.That(tokenSources.All(IsDisposed), Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Cancel_NotificationCancellationFailurePreservesInfrastructureProvenance(bool handleFailure) {
        IOperator @operator = await Task.Run(() => new Operator());
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationFailure = new OperationCanceledException("cancel notification");
        var handled = false;
        Operation operation = null;
        Exception completionFailure = null;
        Exception observed = null;
        try {
            await UiTestThread.Run(async () => {
                operation = Start(@operator, async (_, token) => {
                    started.TrySetResult(token);
                    await release.Task;
                    if (!handleFailure) {
                        operation.Cancel();
                        return;
                    }
                    try {
                        operation.Cancel();
                    }
                    catch (OperationCanceledException ex) when (ReferenceEquals(ex, notificationFailure)) {
                        handled = true;
                    }
                });
                _ = await started.Task.WaitAsync(Timeout);
                operation.PropertyChanged += (_, args) => {
                    if (args.PropertyName == nameof(OperationBase.Canceling)) {
                        throw notificationFailure;
                    }
                };
                release.TrySetResult();
                try {
                    await ((OperationBase)operation).Completion.WaitAsync(Timeout);
                }
                catch (Exception ex) {
                    completionFailure = ex;
                }
            });
        }
        catch (Exception ex) {
            observed = ex;
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(operation.CompleteWithError, Is.False);
            Assert.That(handled, Is.EqualTo(handleFailure));
            Assert.That(operation.Complete, Is.True);
            Assert.That(operation.Error, Is.Null);
            Assert.That(completionFailure, handleFailure ? Is.Null : Is.SameAs(notificationFailure));
            Assert.That(observed, handleFailure ? Is.Null : Is.SameAs(notificationFailure));
        }
    }

    [Test]
    public Task OperationCancellation_CompletedChildStillReceivesCancellationBeforePendingLeaseRelease() =>
        Task.Run(async () => {
            IOperator @operator = new Operator();
            var childStarted = new TaskCompletionSource<CancellationToken>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseChild = new TaskCompletionSource(TaskCreationOptions.None);
            var completedDuringNotification = false;
            var operation = Start(@operator, async (progress, _) => {
                await progress.Child("child", async (_, childToken) => {
                    childStarted.TrySetResult(childToken);
                    await releaseChild.Task;
                }, OperationFailureBehavior.Record, default);
            });
            var childToken = await childStarted.Task.WaitAsync(Timeout);
            var childOperation = await WaitForChild((OperationBase)operation);
            var childSource = GetPrivateField<CancellationTokenSource>(childOperation, "TokenSource");
            operation.PropertyChanged += (_, args) => {
                if (args.PropertyName == nameof(OperationBase.Canceling)) {
                    releaseChild.TrySetResult();
                    completedDuringNotification = childOperation.Complete;
                }
            };

            operation.Cancel();
            await operation.Completion.WaitAsync(Timeout);

            using (Assert.EnterMultipleScope()) {
                Assert.That(completedDuringNotification, Is.True);
                Assert.That(childOperation.Complete, Is.True);
                Assert.That(childToken.IsCancellationRequested, Is.True);
                Assert.That(GetPrivateField<int>(childOperation, "TokenSourceCancellationCount"), Is.Zero);
                Assert.That(GetPrivateField<bool>(childOperation, "TokenSourceDisposalPending"), Is.False);
                Assert.That(IsDisposed(childSource), Is.True);
            }
        });

    [Test]
    public Task SuppliedAndRootCancellation_KeepIndependentCallbackLeases() => Task.Run(async () => {
        IOperator @operator = new Operator();
        using var suppliedCancellation = new CancellationTokenSource();
        var grandchildReady = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGrandchild = new TaskCompletionSource(TaskCreationOptions.None);
        var rootCancellationReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackWaitTimedOut = false;
        Exception callbackTokenError = null;
        Exception suppliedCancellationError = null;
        Exception rootCancellationError = null;
        Task suppliedCancellationTask = null;
        var operation = Start(@operator, async (progress, _) => {
            try {
                await progress.Child("supplied child", async (childProgress, childToken) => {
                    try {
                        await childProgress.Child("grandchild", (grandchildProgress, grandchildToken) => {
                            grandchildToken.Register(() => {
                                releaseGrandchild.TrySetResult();
                                callbackEntered.TrySetResult();
                                callbackWaitTimedOut = !rootCancellationReturned.Task.Wait(
                                    (int)Timeout.TotalMilliseconds,
                                    CancellationToken.None);
                                try { GC.KeepAlive(grandchildToken.WaitHandle); }
                                catch (Exception ex) { callbackTokenError = ex; }
                            });
                            grandchildReady.TrySetResult(grandchildToken);
                            return releaseGrandchild.Task;
                        }, OperationFailureBehavior.Record, default);
                    }
                    catch (OperationCanceledException) {
                    }
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, childToken);
                }, OperationFailureBehavior.Record, suppliedCancellation.Token);
            }
            catch (OperationCanceledException) {
            }
        });
        var grandchildToken = await grandchildReady.Task.WaitAsync(Timeout);
        var childOperation = await WaitForChild((OperationBase)operation);
        var grandchildOperation = await WaitForChild(childOperation);
        var grandchildSource = GetPrivateField<CancellationTokenSource>(grandchildOperation, "TokenSource");
        operation.PropertyChanged += (_, args) => {
            if (args.PropertyName != nameof(OperationBase.Canceling)) {
                return;
            }
            suppliedCancellationTask = Task.Run(() => {
                try { suppliedCancellation.Cancel(); }
                catch (Exception ex) { suppliedCancellationError = ex; }
            });
            if (!callbackEntered.Task.Wait(Timeout)) {
                throw new TimeoutException("The supplied cancellation callback did not start.");
            }
        };

        try {
            var rootCancellationTask = Task.Run(() => {
                try { operation.Cancel(); }
                catch (Exception ex) { rootCancellationError = ex; }
                finally { rootCancellationReturned.TrySetResult(); }
            });
            await rootCancellationTask.WaitAsync(Timeout);
            await suppliedCancellationTask.WaitAsync(Timeout);
            await operation.Completion.WaitAsync(Timeout);
        }
        finally {
            rootCancellationReturned.TrySetResult();
            releaseGrandchild.TrySetResult();
            suppliedCancellation.Cancel();
            if (!operation.Completion.IsCompleted) {
                await operation.Completion.WaitAsync(Timeout);
            }
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(callbackWaitTimedOut, Is.False);
            Assert.That(callbackTokenError, Is.Null);
            Assert.That(suppliedCancellationError, Is.Null);
            Assert.That(rootCancellationError, Is.Null);
            Assert.That(grandchildToken.IsCancellationRequested, Is.True);
            Assert.That(GetPrivateField<int>(grandchildOperation, "TokenSourceCancellationCount"), Is.Zero);
            Assert.That(GetPrivateField<bool>(grandchildOperation, "TokenSourceDisposalPending"), Is.False);
            Assert.That(IsDisposed(grandchildSource), Is.True);
        }
    });

    [Test]
    public Task SuppliedAndRootCancellation_CancelsDescendantBeforeUnreachedRelayDisposal() =>
        Task.Run(async () => {
            IOperator @operator = new Operator();
            using var suppliedCancellation = new CancellationTokenSource();
            var childStarted = new TaskCompletionSource<CancellationToken>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var grandchildStarted = new TaskCompletionSource<CancellationToken>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var grandchildCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseChild = new TaskCompletionSource(TaskCreationOptions.None);
            var releaseGrandchild = new TaskCompletionSource(TaskCreationOptions.None);
            var rootCancellationReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var childCallbackIndex = 0;
            var callbackWaitTimedOut = false;
            var grandchildCompletedInline = false;
            var grandchildCanceledAfterRootCancellation = false;
            var grandchildCancellationCallbacks = 0;
            Exception suppliedCancellationError = null;
            Exception rootCancellationError = null;
            Task suppliedCancellationTask = null;
            var childToken = default(CancellationToken);
            var grandchildToken = default(CancellationToken);

            void OnChildCancellation() {
                releaseChild.TrySetResult();
                releaseGrandchild.TrySetResult();
                if (Interlocked.Exchange(ref childCallbackIndex, 1) == 0) {
                    grandchildCompletedInline = grandchildCompleted.Task.Wait(
                        (int)Timeout.TotalMilliseconds,
                        CancellationToken.None);
                    callbackEntered.TrySetResult();
                    callbackWaitTimedOut = !rootCancellationReturned.Task.Wait(
                        (int)Timeout.TotalMilliseconds,
                        CancellationToken.None);
                    grandchildCanceledAfterRootCancellation = grandchildToken.IsCancellationRequested;
                }
            }

            var operation = Start(@operator, async (progress, rootToken) => {
                try {
                    await progress.Child("supplied child", (childProgress, token) => {
                        childToken = token;
                        token.Register(OnChildCancellation);
                        var grandchildTask = childProgress.Child(
                            "grandchild",
                            (grandchildProgress, grandchildCancellationToken) => {
                                grandchildToken = grandchildCancellationToken;
                                grandchildCancellationToken.Register(() =>
                                    Interlocked.Increment(ref grandchildCancellationCallbacks));
                                grandchildStarted.TrySetResult(grandchildCancellationToken);
                                return releaseGrandchild.Task;
                            },
                            OperationFailureBehavior.Record,
                            default);
                        _ = grandchildTask.ContinueWith(
                            completed => _ = completed.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                            TaskScheduler.Default);
                        token.Register(OnChildCancellation);
                        childStarted.TrySetResult(token);
                        return releaseChild.Task;
                    }, OperationFailureBehavior.Record, suppliedCancellation.Token);
                }
                catch (OperationCanceledException) {
                }
            });
            var childOperation = await WaitForChild((OperationBase)operation);
            var grandchildOperation = await WaitForChild(childOperation);
            var grandchildSource = GetPrivateField<CancellationTokenSource>(grandchildOperation, "TokenSource");
            grandchildOperation.Completed += (_, _) => grandchildCompleted.TrySetResult();
            await childStarted.Task.WaitAsync(Timeout);
            await grandchildStarted.Task.WaitAsync(Timeout);

            operation.PropertyChanged += (_, args) => {
                if (args.PropertyName != nameof(OperationBase.Canceling)) {
                    return;
                }
                suppliedCancellationTask = Task.Run(() => {
                    try { suppliedCancellation.Cancel(); }
                    catch (Exception ex) { suppliedCancellationError = ex; }
                });
                if (!callbackEntered.Task.Wait(Timeout)) {
                    throw new TimeoutException("The child cancellation callback did not start before its relay.");
                }
            };

            try {
                var rootCancellationTask = Task.Run(() => {
                    try { operation.Cancel(); }
                    catch (Exception ex) { rootCancellationError = ex; }
                    finally { rootCancellationReturned.TrySetResult(); }
                });
                await rootCancellationTask.WaitAsync(Timeout);
                await suppliedCancellationTask.WaitAsync(Timeout);
                await operation.Completion.WaitAsync(Timeout);
            }
            finally {
                rootCancellationReturned.TrySetResult();
                releaseChild.TrySetResult();
                releaseGrandchild.TrySetResult();
                try { suppliedCancellation.Cancel(); }
                catch (AggregateException) { }
                if (!operation.Completion.IsCompleted) {
                    await operation.Completion.WaitAsync(Timeout);
                }
            }

            using (Assert.EnterMultipleScope()) {
                Assert.That(childToken.IsCancellationRequested, Is.True);
                Assert.That(callbackWaitTimedOut, Is.False);
                Assert.That(grandchildCompletedInline, Is.True);
                Assert.That(grandchildCanceledAfterRootCancellation, Is.True);
                Assert.That(grandchildCancellationCallbacks, Is.EqualTo(1));
                Assert.That(suppliedCancellationError, Is.Null);
                Assert.That(rootCancellationError, Is.Null);
                Assert.That(grandchildToken.IsCancellationRequested, Is.True);
                Assert.That(GetPrivateField<int>(grandchildOperation, "TokenSourceCancellationCount"), Is.Zero);
                Assert.That(GetPrivateField<bool>(grandchildOperation, "TokenSourceDisposalPending"), Is.False);
                Assert.That(IsDisposed(grandchildSource), Is.True);
            }
        });

    [Test]
    public async Task Child_ResultUsesFrozenCancellationStateBeforeCompletedNotification() {
        IOperator @operator = new Operator();
        using var suppliedCancellation = new CancellationTokenSource();
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationResult result = null;
        var operation = Start(@operator, async (progress, _) => {
            result = await progress.Child("frozen", async (_, _) => {
                childStarted.TrySetResult();
                await releaseChild.Task;
            }, OperationFailureBehavior.Record, suppliedCancellation.Token);
        });
        OperationBase child = null;
        try {
            child = await WaitForChild(operation);
            await childStarted.Task.WaitAsync(Timeout);
            child.Completed += (_, _) => suppliedCancellation.Cancel();
        }
        finally {
            releaseChild.TrySetResult();
            await operation.Completion.WaitAsync(Timeout);
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(suppliedCancellation.IsCancellationRequested, Is.True);
            Assert.That(result.Kind, Is.EqualTo(OperationResultKind.Succeeded));
            Assert.That(result.Error, Is.Null);
            Assert.That(child.CompleteWithError, Is.False);
            Assert.That(operation.CompleteWithError, Is.False);
        }
    }

    [Test]
    public async Task Child_ParentCancellationWinsOverFailureAfterJoiningNonCooperativeDescendant() {
        IOperator @operator = new Operator();
        var workFailure = new IOException("ordinary descendant failure");
        var descendantStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDescendant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootToken = default(CancellationToken);
        OperationCanceledException propagated = null;
        var operation = Start(@operator, async (progress, token) => {
            rootToken = token;
            try {
                await progress.Child("subtree", async (childProgress, childToken) => {
                    await childProgress.Child("failed", (_, _) => Task.FromException(workFailure),
                        OperationFailureBehavior.Record, default);
                    _ = childProgress.Child("non-cooperative", async (_, _) => {
                        descendantStarted.TrySetResult();
                        await releaseDescendant.Task;
                    }, OperationFailureBehavior.Record, default);
                    await Task.Delay(System.Threading.Timeout.Infinite, childToken);
                }, OperationFailureBehavior.Record, default);
            }
            catch (OperationCanceledException ex) {
                propagated = ex;
            }
        });

        try {
            await descendantStarted.Task.WaitAsync(Timeout);
            operation.Cancel();
            Assert.That(operation.Complete, Is.False);
        }
        finally {
            releaseDescendant.TrySetResult();
            await operation.Completion.WaitAsync(Timeout);
        }
        var subtree = await WaitForChild(operation);
        var descendants = ((IEnumerable)subtree.ChildSource).Cast<OperationBase>().ToArray();

        using (Assert.EnterMultipleScope()) {
            Assert.That(propagated, Is.Not.Null);
            Assert.That(propagated.CancellationToken, Is.EqualTo(rootToken));
            Assert.That(descendants[0].Error, Is.SameAs(workFailure));
            Assert.That(operation.Error, Is.Null);
            Assert.That(operation.CompleteWithError, Is.True);
            Assert.That(operation.Completion.Status, Is.EqualTo(TaskStatus.RanToCompletion));
        }
    }

    [Test]
    public async Task Child_RejectsUndefinedFailureBehaviorBeforeRegistration() {
        IOperator @operator = new Operator();
        var sourceEnumerated = false;
        var childStarted = false;
        Exception childValidation = null;
        Exception batchValidation = null;
        IEnumerable<int> Source() {
            sourceEnumerated = true;
            yield return 1;
        }
        var operation = Start(@operator, async (progress, _) => {
            try {
                await progress.Child("invalid", (_, _) => {
                    childStarted = true;
                    return Task.CompletedTask;
                }, (OperationFailureBehavior)123, default);
            }
            catch (Exception ex) {
                childValidation = ex;
            }
            try {
                await progress.Children(Source(), _ => new OperationChild("invalid", (_, _) => Task.CompletedTask),
                    (OperationFailureBehavior)123, default);
            }
            catch (Exception ex) {
                batchValidation = ex;
            }
        });

        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(childValidation, Is.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(batchValidation, Is.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(childStarted, Is.False);
            Assert.That(sourceEnumerated, Is.False);
            Assert.That(((IEnumerable)operation.ChildSource).Cast<OperationBase>(), Is.Empty);
        }
    }

    [Test]
    public async Task OperationCancellation_TokenlessCancellationExceptionUsesCanceledOperationToken() {
        IOperator @operator = new Operator();
        var delegateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var throwCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, async (_, _) => {
            delegateStarted.TrySetResult();
            await throwCancellation.Task;
            throw new OperationCanceledException("no exception token");
        });

        await delegateStarted.Task.WaitAsync(Timeout);
        operation.Cancel();
        throwCancellation.TrySetResult();
        await operation.Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(operation.Error, Is.Null);
            Assert.That(operation.CompleteWithError, Is.False);
        }
    }

    [Test]
    public async Task RefreshRates_UpdatesRunningRateAndAverageForNetProgressAndIdleTime() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, async (progress, _) => {
            progress.Change(setProgress: 10);
            await release.Task;
        }, operation => operation.DisableRateSampling());

        try {
            var item = (OperationBase)@operator.Operations.Snapshot().Single();
            Assert.That(item, Is.SameAs(operation));
            operation.RefreshRates(_ => TimeSpan.FromSeconds(1));
            AssertRates(item, 10, 10);
            operation.RefreshRates(_ => TimeSpan.FromSeconds(3));
            AssertRates(item, 0, 10d / 3);
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
        Assert.That(operation.ProgressRate, Is.Zero);
    }

    [Test]
    public async Task RefreshRates_UsesParentAggregateAndEachOperationsOwnElapsedTime() {
        IOperator @operator = new Operator();
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, async (progress, _) => {
            progress.Change(setProgress: 10);
            await progress.Child("child", async (childProgress, _) => {
                childProgress.Change(setProgress: 5);
                await releaseChild.Task;
            }, OperationFailureBehavior.Record, default);
        }, operation => operation.DisableRateSampling());
        var child = await WaitForChild(operation);

        try {
            operation.RefreshRates(current => current.Name == "root"
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromSeconds(1));

            AssertRates(operation, 7.5, 7.5);
            AssertRates(child, 5, 5);

            operation.RefreshRates(current => current.Name == "root"
                ? TimeSpan.FromSeconds(3)
                : TimeSpan.FromSeconds(2));
            AssertRates(child, 0, 2.5);
            AssertRates(operation, 0, 5);
        }
        finally {
            releaseChild.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task Complete_FinalizesRatesBeforeCompletionNotificationAndFreezesThem() {
        IOperator @operator = new Operator();
        var completedRates = new TaskCompletionSource<(double Rate, double Average)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, (progress, _) => {
            progress.Change(setProgress: 9);
            return Task.CompletedTask;
        }, operation => operation.Completed += (_, _) => completedRates.TrySetResult(
            (operation.ProgressRate, operation.ProgressRateAverage)));

        await operation.Completion.WaitAsync(Timeout);
        var rates = await completedRates.Task.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(rates.Rate, Is.Zero);
            Assert.That(double.IsNaN(rates.Average) || double.IsInfinity(rates.Average), Is.False);
            Assert.That(operation.Complete, Is.True);
        }
        var finalAverage = operation.ProgressRateAverage;
        operation.RefreshRates(_ => TimeSpan.FromDays(1));
        Assert.That(operation.ProgressRateAverage, Is.EqualTo(finalAverage));
    }

    [Test]
    public async Task RateNotification_ObservesCommittedValuesOutsideStateLock() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, async (progress, _) => {
            progress.Change(setProgress: 4);
            await release.Task;
        }, operation => operation.DisableRateSampling());
        operation.PropertyChanged += (_, args) => {
            if (args.PropertyName == nameof(OperationBase.ProgressRate)) {
                var rates = Task.Run(() => (operation.ProgressRate, operation.ProgressRateAverage))
                    .WaitAsync(Timeout).GetAwaiter().GetResult();
                if (rates == (4d, 4d)) {
                    notification.TrySetResult();
                }
            }
        };

        try {
            operation.RefreshRates(_ => TimeSpan.FromSeconds(1));
            await notification.Task.WaitAsync(Timeout);
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task Change_FormatsProgressRatesWithSeparateFormatsAndProvider() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IOperationProgress reporter = null;
        var progressRateStringChanged = false;
        var progressRateAverageStringChanged = false;
        var operation = Start(@operator, async (progress, _) => {
            reporter = progress;
            progress.Change(setProgress: 25);
            reported.TrySetResult();
            await release.Task;
        }, operation => operation.DisableRateSampling());
        operation.PropertyChanged += (_, args) => {
            progressRateStringChanged |= args.PropertyName == nameof(OperationBase.ProgressRateString);
            progressRateAverageStringChanged |=
                args.PropertyName == nameof(OperationBase.ProgressRateAverageString);
        };

        try {
            await reported.Task.WaitAsync(Timeout);
            reporter.Change(progressRateFormat: "0.00",
                            progressRateAverageFormat: "0.000",
                            formatProvider: CultureInfo.GetCultureInfo("en-US"));

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).ProgressRateString, Is.EqualTo("0.00"));
                Assert.That(((OperationBase)operation).ProgressRateAverageString, Is.EqualTo("0.000"));
                Assert.That(progressRateStringChanged, Is.True);
                Assert.That(progressRateAverageStringChanged, Is.True);
            }

            progressRateStringChanged = false;
            progressRateAverageStringChanged = false;
            operation.RefreshRates(_ => TimeSpan.FromSeconds(2));

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).ProgressRateString, Is.EqualTo("12.50"));
                Assert.That(((OperationBase)operation).ProgressRateAverageString, Is.EqualTo("12.500"));
                Assert.That(progressRateStringChanged, Is.True);
                Assert.That(progressRateAverageStringChanged, Is.True);
            }

            progressRateStringChanged = false;
            progressRateAverageStringChanged = false;
            reporter.Change(formatProvider: CultureInfo.GetCultureInfo("fr-FR"));

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).ProgressRateString, Is.EqualTo("12,50"));
                Assert.That(((OperationBase)operation).ProgressRateAverageString, Is.EqualTo("12,500"));
                Assert.That(progressRateStringChanged, Is.True);
                Assert.That(progressRateAverageStringChanged, Is.True);
            }
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task Change_FormatsFileSizeRatesWithAutomaticUnits() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IOperationProgress reporter = null;
        var progressRateStringChanged = false;
        var progressRateAverageStringChanged = false;
        var operation = Start(@operator, async (progress, _) => {
            reporter = progress;
            progress.Change(setProgress: 1_000_000, setTarget: 2_000_000);
            reported.TrySetResult();
            await release.Task;
        }, operation => operation.DisableRateSampling());
        operation.PropertyChanged += (_, args) => {
            progressRateStringChanged |= args.PropertyName == nameof(OperationBase.ProgressRateString);
            progressRateAverageStringChanged |=
                args.PropertyName == nameof(OperationBase.ProgressRateAverageString);
        };

        try {
            await reported.Task.WaitAsync(Timeout);
            reporter.Change(progressFormat: "N2",
                            progressRateFormat: "N2",
                            progressRateAverageFormat: "N2",
                            formatProvider: CultureInfo.InvariantCulture);
            progressRateStringChanged = false;
            progressRateAverageStringChanged = false;

            reporter.Change(kind: OperationProgressKind.FileSize);

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).ProgressString, Is.EqualTo("976.56 KiB"));
                Assert.That(((OperationBase)operation).ProgressRateString, Is.EqualTo("0 B"));
                Assert.That(((OperationBase)operation).ProgressRateAverageString, Is.EqualTo("0 B"));
                Assert.That(progressRateStringChanged, Is.True);
                Assert.That(progressRateAverageStringChanged, Is.True);
            }

            operation.RefreshRates(_ => TimeSpan.FromSeconds(1));

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).ProgressRateString, Is.EqualTo("976.56 KiB"));
                Assert.That(((OperationBase)operation).ProgressRateAverageString, Is.EqualTo("976.56 KiB"));
                Assert.That(((OperationBase)operation).ProgressRateString,
                            Is.EqualTo(((OperationBase)operation).ProgressString));
                Assert.That(((OperationBase)operation).ProgressRateAverageString,
                            Is.EqualTo(((OperationBase)operation).ProgressString));
            }
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task SamplerListenerFailure_FaultsCompletionAfterFinalizingOperation() {
        Operation operation = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new IOException("rate listener");
        Exception observed = null;
        try {
            await UiTestThread.Run(async () => {
                IOperator @operator = new Operator();
                var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var throwOnce = 0;
                operation = Start(@operator, async (progress, _) => {
                    progress.Change(setProgress: 1);
                    await release.Task;
                }, started => started.PropertyChanged += (_, args) => {
                    if (args.PropertyName == nameof(OperationBase.ProgressRate)) {
                        notification.TrySetResult();
                        if (Interlocked.Exchange(ref throwOnce, 1) == 0) {
                            throw error;
                        }
                    }
                });

                await notification.Task.WaitAsync(TimeSpan.FromSeconds(4));
                release.TrySetResult();
                try {
                    await operation.Completion.WaitAsync(Timeout);
                }
                catch (IOException ex) {
                    observed = ex;
                }
            });
        }
        catch (Exception ex) {
            observed = ex;
        }
        finally {
            release.TrySetResult();
        }

        if (operation is not null) {
            try {
                await operation.Completion.WaitAsync(Timeout);
            }
            catch (IOException ex) {
                observed ??= ex;
            }
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(observed, Is.SameAs(error));
            Assert.That(operation.Complete, Is.True);
            Assert.That(operation.ProgressRate, Is.Zero);
        }
    }

    [Test]
    public async Task SamplerListenerFailure_ReentrantCompletionWaitsForCallbackBeforeFaulting() {
        Operation operation = null;
        var release = new TaskCompletionSource(TaskCreationOptions.None);
        var error = new IOException("rate listener");
        Exception observed = null;
        try {
            await UiTestThread.Run(async () => {
                IOperator @operator = new Operator();
                var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = await Task.Run(() => Start(@operator, async (progress, _) => {
                    progress.Change(setProgress: 1);
                    await release.Task.ConfigureAwait(false);
                }, started => started.PropertyChanged += (_, args) => {
                    if (args.PropertyName == nameof(OperationBase.ProgressRate) && started.ProgressRate > 0) {
                        notification.TrySetResult();
                        release.TrySetResult();
                        throw error;
                    }
                })).WaitAsync(Timeout);

                await notification.Task.WaitAsync(TimeSpan.FromSeconds(4));
                try {
                    await operation.Completion.WaitAsync(Timeout);
                }
                catch (IOException ex) {
                    observed = ex;
                }
            });
        }
        catch (Exception ex) {
            observed = ex;
        }
        finally {
            release.TrySetResult();
        }

        if (operation is not null) {
            try {
                await operation.Completion.WaitAsync(Timeout);
            }
            catch (IOException ex) {
                observed ??= ex;
            }
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(observed, Is.SameAs(error));
            Assert.That(operation.Complete, Is.True);
            Assert.That(operation.ProgressRate, Is.Zero);
        }
    }

    [Test]
    public async Task Sampler_ReportsProgressAndThenAnIdleRateWithoutAnotherChange() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRate = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleRate = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, async (progress, _) => {
            progress.Change(setProgress: 1);
            await release.Task;
        }, operation => operation.PropertyChanged += (_, args) => {
            if (args.PropertyName == nameof(OperationBase.ProgressRate)) {
                var value = operation.ProgressRate;
                if (value == 0) {
                    idleRate.TrySetResult(value);
                }
                else {
                    firstRate.TrySetResult(value);
                }
            }
        });

        try {
            Assert.That(await firstRate.Task.WaitAsync(TimeSpan.FromSeconds(4)), Is.GreaterThan(0));
            Assert.That(await idleRate.Task.WaitAsync(TimeSpan.FromSeconds(4)), Is.Zero);
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task Sampler_ReportsProgressAfterProgressListenerFailure() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenerFailure = new InvalidOperationException("progress listener");
        var caughtFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<double> positiveRate = null;
        var operation = Start(@operator, async (progress, _) => {
            try {
                progress.Change(setProgress: 10);
            }
            catch (InvalidOperationException ex) {
                caughtFailure.TrySetResult(ex);
            }
            await release.Task;
        }, started => {
            positiveRate = WaitForPositiveRate(started);
            started.PropertyChanged += (_, args) => {
                if (args.PropertyName == nameof(OperationBase.Progress)) {
                    throw listenerFailure;
                }
            };
        });

        try {
            Assert.That(await caughtFailure.Task.WaitAsync(Timeout), Is.SameAs(listenerFailure));
            var rate = await positiveRate;

            using (Assert.EnterMultipleScope()) {
                Assert.That(operation.Progress, Is.EqualTo(10));
                Assert.That(rate, Is.GreaterThan(0));
                Assert.That(operation.ProgressRateAverage, Is.GreaterThan(0));
                Assert.That(operation.Complete, Is.False);
            }
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task Sampler_ReportsProgressDuringLongSynchronousDelegate() {
        IOperator @operator = new Operator();
        var operationReady = new TaskCompletionSource<Operation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<double> positiveRate = null;
        using var release = new ManualResetEventSlim();
        var start = Task.Factory.StartNew(
            () => Start(@operator, (progress, _) => {
                progress.Change(setProgress: 10);
                reported.TrySetResult();
                if (!release.Wait(Timeout, CancellationToken.None)) {
                    throw new TimeoutException("The synchronous operation was not released in time.");
                }
                return Task.CompletedTask;
            }, operation => {
                positiveRate = WaitForPositiveRate(operation);
                operationReady.TrySetResult(operation);
            }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Operation operation = null;

        try {
            operation = await operationReady.Task.WaitAsync(Timeout);
            await reported.Task.WaitAsync(Timeout);
            var rate = await positiveRate;

            using (Assert.EnterMultipleScope()) {
                Assert.That(rate, Is.GreaterThan(0));
                Assert.That(operation.ProgressRateAverage, Is.GreaterThan(0));
                Assert.That(start.IsCompleted, Is.False);
            }
        }
        finally {
            release.Set();
            await start.WaitAsync(Timeout);
            if (operation is not null) {
                await operation.Completion.WaitAsync(Timeout);
            }
        }
    }

    [Test]
    public async Task Sampler_ReportsDescendantProgressAfterSynchronousParentFailure() {
        IOperator @operator = new Operator();
        var releaseReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new IOException("parent sync failure");
        Task<double> positiveRate = null;
        var operation = Start(@operator, (progress, _) => {
            progress.Child("child", async (childProgress, _) => {
                await releaseReport.Task;
                childProgress.Change(setProgress: 10);
                childReported.TrySetResult();
                await releaseChild.Task;
            }, OperationFailureBehavior.Record, default);
            throw error;
        }, started => positiveRate = WaitForPositiveRate(started));

        try {
            var child = await WaitForChild(operation);
            var childPositiveRate = WaitForPositiveRate(child);
            releaseReport.TrySetResult();
            await childReported.Task.WaitAsync(Timeout);
            var rate = await positiveRate;
            var childRate = await childPositiveRate;

            using (Assert.EnterMultipleScope()) {
                Assert.That(rate, Is.GreaterThan(0));
                Assert.That(childRate, Is.GreaterThan(0));
                Assert.That(operation.ProgressRateAverage, Is.GreaterThan(0));
                Assert.That(operation.Complete, Is.False);
                Assert.That(operation.Error, Is.SameAs(error));
            }
        }
        finally {
            releaseReport.TrySetResult();
            releaseChild.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public void Sampler_DoesNotDelayShortSynchronousOperationCompletion() {
        IOperator @operator = new Operator();
        var operation = Start(@operator, (progress, _) => {
            progress.Change(setProgress: 10);
            return Task.CompletedTask;
        });

        using (Assert.EnterMultipleScope()) {
            Assert.That(operation.Completion.IsCompleted, Is.True);
            Assert.That(operation.Complete, Is.True);
        }
    }

#if NET10_0
    [Test]
    public void Sampler_ShutdownDoesNotBlockABoundedThreadPool() {
        var targetDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var outputDirectory = targetDirectory.Parent?.Parent ?? throw new DirectoryNotFoundException();
        var probe = Path.Combine(outputDirectory.FullName,
                                 "Brows.Operations.Tests.PoolProbe",
                                 targetDirectory.Name,
                                 "Brows.Operations.Tests.PoolProbe.dll");
        var startInfo = new ProcessStartInfo {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(probe);
        using var process = Process.Start(startInfo);
        Assert.That(process, Is.Not.Null);
        if (!process.WaitForExit(Timeout)) {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The bounded-thread-pool probe did not exit before the watchdog timeout.");
        }
        Assert.That(process.ExitCode, Is.Zero,
                    $"Probe output: {process.StandardOutput.ReadToEnd()}{process.StandardError.ReadToEnd()}");
    }
#endif

    private static void AssertRates(OperationBase operation, double progressRate, double average) {
        Assert.That(operation.ProgressRate, Is.EqualTo(progressRate).Within(0.000001));
        Assert.That(operation.ProgressRateAverage, Is.EqualTo(average).Within(0.000001));
    }
}

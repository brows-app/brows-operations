using System;
using System.Collections;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>Tests error publication and propagation of completion-listener failures.</summary>
[TestFixture]
internal sealed class NotificationFailureTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static Operation Start(IOperator @operator, OperationDelegate task, Action<Operation> subscribe = null) {
        Operation root = null;
        new OperationManager((OperationCollection)@operator.Operations).Operate("root", task, operation => {
            root = operation;
            subscribe?.Invoke(root);
        });
        return root;
    }

    private static OperationBase[] Children(OperationBase operation) =>
        ((IEnumerable)operation.ChildSource).Cast<OperationBase>().ToArray();

    /// <summary>Verifies that a completion-listener failure surfaces after the operation result is finalized.</summary>
    /// <param name="fails">Whether the delegate fails rather than completing successfully.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task CompletionListenerFailure_PropagatesAfterFinalizingTheResult(bool fails) {
        IOperator @operator = null;
        Operation root = null;
        var laterCalls = 0;
        var delegateError = new IOException("delegate");
        var listenerError = new IOException("completion observer");
        Exception observed = null;
        try {
            await UiTestThread.Run(() => {
                @operator = new Operator();
                root = Start(@operator, (_, _) => fails ? Task.FromException(delegateError) : Task.CompletedTask, operation => {
                    operation.Completed += (_, _) => throw listenerError;
                    operation.Completed += (_, _) => laterCalls++;
                });
                return Task.CompletedTask;
            });
        }
        catch (Exception ex) { observed = ex; }
        using (Assert.EnterMultipleScope()) {
            Assert.That(observed, Is.SameAs(listenerError));
            Assert.That(root.Completion.IsFaulted, Is.True);
            Assert.That(laterCalls, Is.Zero);
            Assert.That(root.Complete, Is.True);
            Assert.That(root.Progressing, Is.False);
            Assert.That(root.CompleteWithError, Is.EqualTo(fails));
            Assert.That(root.Error, Is.EqualTo(fails ? delegateError : null));
            Assert.That(@operator.Operations.Count, Is.EqualTo(fails ? 1 : 0));
        }
    }

    /// <summary>
    /// Verifies a worker-started root surfaces unexpected completion failures on its captured context.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task CompletionListenerFailure_FromWorkerStart_UsesCapturedContext() {
        IOperator @operator = null;
        Operation root = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenerError = new IOException("completion observer");
        Exception observed = null;
        try {
            await UiTestThread.Run(async () => {
                @operator = new Operator();
                await Task.Run(() => @operator.Operate("root", async (_, _) => await release.Task)).WaitAsync(Timeout);
                root = (Operation)@operator.Operations.Snapshot().Single();
                root.Completed += (_, _) => throw listenerError;
                release.SetResult();
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
            Assert.That(observed, Is.SameAs(listenerError));
            Assert.That(root.Completion.IsFaulted, Is.True);
            Assert.That(root.Complete, Is.True);
        }
    }

    /// <summary>
    /// Verifies that child registration closes before the delegate's error notification is published.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task DelegateFailure_ClosesRegistrationBeforePublishingItsError() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        IOperationProgress retained = null;
        Task registration = null;
        var ran = false;
        var root = Start(@operator, (progress, _) => {
            retained = progress;
            return Task.FromException(new IOException("delegate"));
        }, root => root.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(OperationBase.Error)) {
                registration = retained.Child("late", (_, _) => {
                    ran = true;
                    return Task.CompletedTask;
                }, OperationFailureBehavior.Record, default);
            }
        });
        Exception failure = null;
        try { await registration.WaitAsync(Timeout); }
        catch (Exception ex) { failure = ex; }
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(ran, Is.False);
            Assert.That(failure, Is.InstanceOf<InvalidOperationException>());
            Assert.That(Children(root).Length, Is.EqualTo(0));
        }
    });

    /// <summary>
    /// Verifies an unhandled validation error faults root completion without becoming a work Error.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task UnhandledChildValidationError_FaultsCompletionWithoutWorkError() {
        var @operator = await Task.Run(() => new Operator());
        Exception observed = null;
        Operation root = null;
        try {
            await UiTestThread.Run(async () => {
                root = Start(@operator, async (progress, _) =>
                    await progress.Child("invalid", null, OperationFailureBehavior.Record, default));
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
            Assert.That(observed, Is.InstanceOf<ArgumentNullException>());
            Assert.That(root.Completion.IsFaulted, Is.True);
            Assert.That(root.Complete, Is.True);
            Assert.That(root.Error, Is.Null);
            Assert.That(root.CompleteWithError, Is.False);
        }
    }

    /// <summary>
    /// Verifies that a progress-listener failure remains infrastructure under the Record policy.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(OperationFailureBehavior.Record)]
    [TestCase(OperationFailureBehavior.Throw)]
    public async Task ProgressListenerFailure_PropagatesUnderBothPolicies(OperationFailureBehavior failureBehavior) {
        var @operator = await Task.Run(() => new Operator());
        var listenerError = new IOException("progress listener");
        Operation root = null;
        Exception publicBoundaryObserved = null;
        Exception observed = null;
        try {
            await UiTestThread.Run(async () => {
                root = Start(@operator, async (progress, _) => {
                    try {
                        await progress.Child("child", (childProgress, _) => {
                            childProgress.Change(setProgress: 1);
                            return Task.CompletedTask;
                        }, failureBehavior, default);
                    }
                    catch (Exception ex) {
                        publicBoundaryObserved = ex;
                        throw;
                    }
                }, operation => {
                    ((INotifyCollectionChanged)operation.ChildSource).CollectionChanged += (_, args) => {
                        if (args.NewItems?[0] is OperationBase child) {
                            child.ProgressChanged += (_, _) => throw listenerError;
                        }
                    };
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
            Assert.That(publicBoundaryObserved, Is.SameAs(listenerError));
            Assert.That(observed, Is.SameAs(listenerError));
            Assert.That(root.Completion.IsFaulted, Is.True);
            Assert.That(root.Complete, Is.True);
            Assert.That(root.Error, Is.Null);
        }
    }

    /// <summary>
    /// Verifies completion-listener failures propagate through Child under either work-failure policy.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(OperationFailureBehavior.Record)]
    [TestCase(OperationFailureBehavior.Throw)]
    public async Task ChildCompletionListenerFailure_PropagatesUnderBothPolicies(
        OperationFailureBehavior failureBehavior) {
        var @operator = await Task.Run(() => new Operator());
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenerError = new IOException("child completion listener");
        Exception publicBoundaryObserved = null;
        Exception observed = null;
        Operation root = null;
        try {
            await UiTestThread.Run(async () => {
                root = Start(@operator, async (progress, _) => {
                    try {
                        await progress.Child("child", async (_, _) => {
                            childStarted.TrySetResult();
                            await releaseChild.Task;
                        }, failureBehavior, default);
                    }
                    catch (Exception ex) {
                        publicBoundaryObserved = ex;
                    }
                });
                await childStarted.Task.WaitAsync(Timeout);
                Children(root).Single().Completed += (_, _) => throw listenerError;
                releaseChild.TrySetResult();
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
            Assert.That(publicBoundaryObserved, Is.SameAs(listenerError));
            Assert.That(observed, Is.SameAs(listenerError));
            Assert.That(root.Completion.IsFaulted, Is.True);
            Assert.That(root.Complete, Is.True);
            Assert.That(root.Error, Is.Null);
            Assert.That(root.CompleteWithError, Is.False);
        }
    }

    /// <summary>
    /// Verifies that a handled progress-listener failure does not fault internal completion.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task HandledProgressListenerFailure_DoesNotPoisonSuccessfulWork() =>
        UiTestThread.Run(async () => {
            var @operator = await Task.Run(() => new Operator());
            var listenerError = new IOException("handled progress listener");
            var handled = false;
            Operation root = null;
            root = Start(@operator, (progress, _) => {
                try {
                    progress.Change(setProgress: 1);
                }
                catch (IOException ex) when (ReferenceEquals(ex, listenerError)) {
                    handled = true;
                }
                return Task.CompletedTask;
            }, operation => operation.ProgressChanged += (_, _) => throw listenerError);

            await root.Completion.WaitAsync(Timeout);
            using (Assert.EnterMultipleScope()) {
                Assert.That(handled, Is.True);
                Assert.That(root.Completion.Status, Is.EqualTo(TaskStatus.RanToCompletion));
                Assert.That(root.Error, Is.Null);
            }
        });

    /// <summary>
    /// Verifies a committed child starts and is joined when collection publication throws.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task RegistrationPublicationFailure_JoinsCommittedChildBeforePropagating() {
        var @operator = await Task.Run(() => new Operator());
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationError = new IOException("collection publication");
        Exception observed = null;
        Task<OperationResult> publicChild = null;
        var root = Start(@operator, async (progress, _) => {
            try {
                publicChild = progress.Child("committed", async (_, _) => {
                    childStarted.TrySetResult();
                    await releaseChild.Task;
                }, OperationFailureBehavior.Record, default);
                await publicChild;
            }
            catch (Exception ex) {
                observed = ex;
            }
        }, operation => ((INotifyCollectionChanged)operation.ChildSource).CollectionChanged += (_, args) => {
            if (args.Action == NotifyCollectionChangedAction.Add) {
                throw publicationError;
            }
        });

        try {
            await childStarted.Task.WaitAsync(Timeout);
            var child = Children(root).Single();
            Assert.That(publicChild.IsCompleted, Is.False);
            Assert.That(observed, Is.Null);
            Assert.That(child.Complete, Is.False);
            Assert.That(root.Complete, Is.False);
        }
        finally {
            releaseChild.TrySetResult();
            try {
                await ((OperationBase)root).Completion.WaitAsync(Timeout);
            }
            catch (Exception) {
            }
        }
        await ((OperationBase)root).Completion.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(observed, Is.SameAs(publicationError));
            Assert.That(Children(root), Has.Length.EqualTo(1));
            Assert.That(Children(root)[0].Complete, Is.True);
            Assert.That(root.Completion.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(root.CompleteWithError, Is.False);
            Assert.That(root.Error, Is.Null);
        }
    }

    /// <summary>
    /// Verifies batch registration stops after publication failure and joins the committed child.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task BatchRegistrationPublicationFailure_JoinsCommittedChildBeforePropagating() {
        var @operator = await Task.Run(() => new Operator());
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationError = new IOException("batch collection publication");
        var factoryCalls = 0;
        Exception observed = null;
        Task<OperationBatchResult> publicBatch = null;
        var root = Start(@operator, async (progress, _) => {
            try {
                publicBatch = progress.Children(new[] { 1, 2 }, item => {
                    factoryCalls++;
                    return new OperationChild(item.ToString(), async (_, _) => {
                        childStarted.TrySetResult();
                        await releaseChild.Task;
                    });
                }, OperationFailureBehavior.Record, default);
                await publicBatch;
            }
            catch (Exception ex) {
                observed = ex;
            }
        }, operation => ((INotifyCollectionChanged)operation.ChildSource).CollectionChanged += (_, args) => {
            if (args.Action == NotifyCollectionChangedAction.Add) {
                throw publicationError;
            }
        });

        try {
            await childStarted.Task.WaitAsync(Timeout);
            Assert.That(publicBatch.IsCompleted, Is.False);
            Assert.That(observed, Is.Null);
            Assert.That(factoryCalls, Is.EqualTo(1));
            Assert.That(Children(root), Has.Length.EqualTo(1));
        }
        finally {
            releaseChild.TrySetResult();
            await ((OperationBase)root).Completion.WaitAsync(Timeout);
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(observed, Is.SameAs(publicationError));
            Assert.That(Children(root), Has.Length.EqualTo(1));
            Assert.That(Children(root)[0].Complete, Is.True);
            Assert.That(root.Completion.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(root.Error, Is.Null);
            Assert.That(root.CompleteWithError, Is.False);
        }
    }

    /// <summary>
    /// Verifies a committed publication error remains infrastructure when cancellation is requested during publication.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task RegistrationPublicationFailure_RacingCancellationKeepsInfrastructurePrecedence() {
        var @operator = await Task.Run(() => new Operator());
        var parentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationError = new IOException("publication during cancellation");
        var childRan = false;
        Exception observed = null;
        var root = Start(@operator, async (progress, _) => {
            parentStarted.TrySetResult();
            await releaseParent.Task;
            try {
                await progress.Child("committed", (_, _) => {
                    childRan = true;
                    return Task.CompletedTask;
                }, OperationFailureBehavior.Record, default);
            }
            catch (Exception ex) {
                observed = ex;
            }
        }, operation => ((INotifyCollectionChanged)operation.ChildSource).CollectionChanged += (_, args) => {
            if (args.Action == NotifyCollectionChangedAction.Add) {
                operation.Cancel();
                throw publicationError;
            }
        });

        try {
            await parentStarted.Task.WaitAsync(Timeout);
            releaseParent.TrySetResult();
            await ((OperationBase)root).Completion.WaitAsync(Timeout);
        }
        finally {
            releaseParent.TrySetResult();
            try {
                await ((OperationBase)root).Completion.WaitAsync(Timeout);
            }
            catch (Exception) {
            }
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(observed, Is.SameAs(publicationError));
            Assert.That(childRan, Is.False);
            Assert.That(Children(root), Has.Length.EqualTo(1));
            Assert.That(Children(root)[0].Complete, Is.True);
            Assert.That(root.Error, Is.Null);
            Assert.That(root.CompleteWithError, Is.False);
            Assert.That(root.Completion.Status, Is.EqualTo(TaskStatus.RanToCompletion));
        }
    }

    /// <summary>
    /// Verifies that a token-backed progress-listener exception stays infrastructure during batch cancellation.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task BatchFactory_ProgressListenerCancellationRemainsInfrastructure() {
        var @operator = await Task.Run(() => new Operator());
        using var batchSource = new CancellationTokenSource();
        using var exceptionSource = new CancellationTokenSource();
        exceptionSource.Cancel();
        var listenerError = new OperationCanceledException("progress listener", exceptionSource.Token);
        Operation root = null;
        Exception publicBoundaryObserved = null;
        Exception observed = null;
        var factoryCalls = 0;
        try {
            await UiTestThread.Run(async () => {
                root = Start(@operator, async (progress, _) => {
                    try {
                        await progress.Children(new[] { 1, 2 }, _ => {
                            factoryCalls++;
                            progress.Change(setProgress: 1);
                            return new OperationChild("child", (_, _) => Task.CompletedTask);
                        }, OperationFailureBehavior.Record, batchSource.Token);
                    }
                    catch (Exception ex) {
                        publicBoundaryObserved = ex;
                        throw;
                    }
                }, operation => operation.ProgressChanged += (_, _) => {
                    batchSource.Cancel();
                    throw listenerError;
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
            Assert.That(publicBoundaryObserved, Is.SameAs(listenerError));
            Assert.That(observed, Is.SameAs(listenerError));
            Assert.That(root.Completion.IsFaulted, Is.True);
            Assert.That(factoryCalls, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// Verifies that infrastructure provenance wins when an exception instance was previously work failure.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task ReusedWorkException_ProgressListenerOriginRemainsInfrastructure() {
        var @operator = await Task.Run(() => new Operator());
        var sharedError = new IOException("shared failure");
        Operation root = null;
        Exception observed = null;
        try {
            await UiTestThread.Run(async () => {
                root = Start(@operator, async (progress, _) => {
                    var first = await progress.Child("work", (_, _) => Task.FromException(sharedError),
                        OperationFailureBehavior.Record, default);
                    Assert.That(first.Kind, Is.EqualTo(OperationResultKind.Failed));
                    await progress.Child("listener", (childProgress, _) => {
                        childProgress.Change(setProgress: 1);
                        return Task.CompletedTask;
                    }, OperationFailureBehavior.Record, default);
                }, operation => {
                    ((INotifyCollectionChanged)operation.ChildSource).CollectionChanged += (_, args) => {
                        if (args.NewItems?[0] is OperationBase child && child.Name == "listener") {
                            child.ProgressChanged += (_, _) => throw sharedError;
                        }
                    };
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
            Assert.That(observed, Is.SameAs(sharedError));
            Assert.That(root.Completion.IsFaulted, Is.True);
            Assert.That(Children(root).Select(child => child.Name), Is.EqualTo(new[] { "work", "listener" }));
        }
    }

    /// <summary>
    /// Verifies nested library infrastructure aggregates contribute each original failure once.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task NestedCompletionFailures_AggregateDistinctInfrastructureLeaves() {
        var @operator = await Task.Run(() => new Operator());
        var leafError = new IOException("leaf completion observer");
        var parentError = new IOException("parent completion observer");
        AggregateException childAggregate = null;
        Exception observed = null;
        try {
            await UiTestThread.Run(async () => {
                var root = Start(@operator, async (progress, _) => {
                    try {
                        await progress.Child("parent", async (parentProgress, _) => {
                            await parentProgress.Child("leaf", (_, _) => Task.CompletedTask,
                                OperationFailureBehavior.Record, default);
                        }, OperationFailureBehavior.Record, default);
                    }
                    catch (AggregateException ex) {
                        childAggregate = ex;
                    }
                }, operation => {
                    operation.Completed += (_, _) => throw childAggregate;
                    void Subscribe(OperationBase parent) {
                        ((INotifyCollectionChanged)parent.ChildSource).CollectionChanged += (_, args) => {
                            if (args.NewItems is null) {
                                return;
                            }
                            foreach (OperationBase child in args.NewItems) {
                                child.Completed += (_, _) => {
                                    throw child.Name == "leaf" ? leafError : parentError;
                                };
                                Subscribe(child);
                            }
                        };
                    }
                    Subscribe(operation);
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

        Assert.That(observed, Is.InstanceOf<AggregateException>());
        var aggregate = (AggregateException)observed;
        using (Assert.EnterMultipleScope()) {
            Assert.That(childAggregate, Is.Not.Null);
            Assert.That(aggregate.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(aggregate.InnerExceptions, Does.Contain(leafError));
            Assert.That(aggregate.InnerExceptions, Does.Contain(parentError));
            Assert.That(aggregate.InnerExceptions, Has.None.InstanceOf<AggregateException>());
        }
    }

    /// <summary>
    /// Verifies that a canceled parent's effective token remains available after source disposal.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task CompletedCanceledParent_BatchDoesNotEnumerateAfterTokenSourceDisposal() {
        var @operator = await Task.Run(() => new Operator());
        var progressReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IOperationProgress savedProgress = null;
        CancellationToken operationToken = default;
        Operation root = null;

        await UiTestThread.Run(async () => {
            root = Start(@operator, async (progress, token) => {
                savedProgress = progress;
                operationToken = token;
                progressReady.TrySetResult();
                await release.Task;
            });
            await progressReady.Task.WaitAsync(Timeout);
            root.Cancel();
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        });

        var enumerated = false;
        var childRan = false;
        OperationCanceledException childObserved = null;
        try {
            await savedProgress.Child("late child", (_, _) => {
                childRan = true;
                return Task.CompletedTask;
            }, OperationFailureBehavior.Record, default);
        }
        catch (OperationCanceledException ex) {
            childObserved = ex;
        }

        IEnumerable<int> Source() {
            enumerated = true;
            yield return 1;
        }

        OperationCanceledException observed = null;
        try {
            await savedProgress.Children(Source(), _ => new OperationChild("child", (_, _) => Task.CompletedTask),
                OperationFailureBehavior.Record, default);
        }
        catch (OperationCanceledException ex) {
            observed = ex;
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(childObserved?.CancellationToken, Is.EqualTo(operationToken));
            Assert.That(childRan, Is.False);
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.CancellationToken, Is.EqualTo(operationToken));
            Assert.That(enumerated, Is.False);
            Assert.That(root.Complete, Is.True);
        }
    }

    /// <summary>
    /// Verifies that an unawaited batch retains parent cancellation while its continuation is delayed past completion.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public void UnawaitedBatch_DelayedContinuationRetainsCanceledParentToken() {
        var @operator = Task.Run(() => new Operator()).GetAwaiter().GetResult();
        var context = new ControlledSynchronizationContext();
        var childStarted = new ManualResetEventSlim();
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var suppliedCancellation = new CancellationTokenSource();
        Task<OperationBatchResult> batch = null;
        OperationBase parentOperation = null;
        Operation root = null;
        var operationToken = default(CancellationToken);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try {
            root = Start(@operator, async (progress, _) => {
                await progress.Child("parent", (parentProgress, token) => {
                    operationToken = token;
                    batch = parentProgress.Children(new[] { 1 }, _ => new OperationChild("child", async (_, _) => {
                        childStarted.Set();
                        await releaseChild.Task;
                    }), OperationFailureBehavior.Record, default);
                    return Task.CompletedTask;
                }, OperationFailureBehavior.Record, suppliedCancellation.Token);
            }, operation => ((INotifyCollectionChanged)operation.ChildSource).CollectionChanged += (_, args) => {
                if (args.NewItems?[0] is OperationBase child && child.Name == "parent") {
                    parentOperation = child;
                }
            });
            Assert.That(childStarted.Wait(Timeout), Is.True);
            Assert.That(parentOperation, Is.Not.Null);
            suppliedCancellation.Cancel();
            releaseChild.TrySetResult();

            Assert.That(context.WaitForCount(1, Timeout), Is.True);
            Assert.That(context.RunOldest(), Is.True);
            Assert.That(context.WaitForCount(2, Timeout), Is.True);
            Assert.That(context.RunUntil(() => parentOperation.Complete, newest: true, Timeout), Is.True);
            Assert.That(context.RunUntil(() => root.Completion.IsCompleted, newest: true, Timeout), Is.True);

            Assert.That(root.Complete, Is.True);
            Assert.That(batch.IsCompleted, Is.False);
            Assert.That(context.RunUntil(() => batch.IsCompleted, newest: false, Timeout), Is.True);

            OperationCanceledException observed = null;
            try {
                batch.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex) {
                observed = ex;
            }
            Assert.That(observed?.CancellationToken, Is.EqualTo(operationToken));
        }
        finally {
            suppliedCancellation.Cancel();
            releaseChild.TrySetResult();
            context.RunUntil(() => root is null || root.Completion.IsCompleted, newest: true, Timeout);
            context.RunUntil(() => batch is null || batch.IsCompleted, newest: false, Timeout);
            childStarted.Dispose();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}

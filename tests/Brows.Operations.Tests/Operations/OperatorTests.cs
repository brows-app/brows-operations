using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>Tests operation execution, collection behavior, progress reporting, and cancellation through IOperator.</summary>
[TestFixture]
public sealed class OperatorTests {
    private static IOperator CreateOperator() =>
        new Operator();

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T GetPrivateField<T>(OperationBase operation, string fieldName) =>
        (T)typeof(OperationBase)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(operation);

    private static bool IsDisposed(CancellationTokenSource tokenSource) {
        try {
            _ = tokenSource.Token;
            return false;
        }
        catch (ObjectDisposedException) {
            return true;
        }
    }

    private static async Task<OperationBase[]> WaitForChildren(OperationBase operation, int count = 1) {
        var children = (IList)operation.ChildSource;
        if (children.Count < count) {
            var changed = NewSignal();
            NotifyCollectionChangedEventHandler handler = (_, _) => {
                if (children.Count >= count) {
                    changed.TrySetResult();
                }
            };
            var source = (INotifyCollectionChanged)children;
            source.CollectionChanged += handler;
            try {
                if (children.Count < count) {
                    await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            finally {
                source.CollectionChanged -= handler;
            }
        }
        return children.Cast<OperationBase>().ToArray();
    }

    private static Operation Start(IOperator @operator,
                                   string name,
                                   OperationDelegate task,
                                   Action<Operation> initialize = null) {
        Operation operation = null;
        new OperationManager((OperationCollection)@operator.Operations).Operate(name, task, root => {
            operation = root;
            initialize?.Invoke(root);
        });
        return operation;
    }

    /// <summary>Verifies that starting an operation rejects a null work delegate.</summary>
    [Test]
    public void Operate_RejectsNullTask() {
        var @operator = CreateOperator();

        Assert.That(() => @operator.Operate("invalid", null), Throws.ArgumentNullException);
    }

    /// <summary>Verifies that a successful operation is removed from the public collection.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task SuccessfulOperation_IsRemovedFromThePublicCollection() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            @operator.Operate("success", (_, _) => Task.CompletedTask);

            Assert.That(@operator.Operations.Count, Is.Zero);
            return Task.CompletedTask;
        });

    /// <summary>Verifies that a failed operation remains in the collection with completion and error flags.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task FaultedOperation_IsRetainedAndExposesCompletionFlags() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            @operator.Operate("failure", (_, _) => Task.FromException(new IOException("operation failed")));
            var item = @operator.Operations.Snapshot().Single();

            using (Assert.EnterMultipleScope()) {
                Assert.That(@operator.Operations.Count, Is.EqualTo(1));
                Assert.That(item.Complete, Is.True);
                Assert.That(item.CompleteWithError, Is.True);
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that a running operation remains in the collection until its work finishes.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task RunningOperation_RemainsVisibleUntilItCompletes() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var release = NewSignal();
            var removed = NewSignal();
            var source = (INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Remove) {
                    removed.TrySetResult();
                }
            };
            source.CollectionChanged += handler;
            try {
                @operator.Operate("running", async (_, _) => await release.Task);
                var visibleWhileRunning = @operator.Operations.Count;
                release.SetResult();
                await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope()) {
                    Assert.That(visibleWhileRunning, Is.EqualTo(1));
                    Assert.That(@operator.Operations.Count, Is.EqualTo(0));
                }
            }
            finally {
                release.TrySetResult();
                source.CollectionChanged -= handler;
            }
        });

    /// <summary>Verifies that collection removal rejects an operation that is still running.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CollectionRemove_RejectsRunningOperation() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var release = NewSignal();
            @operator.Operate("running", async (_, _) => await release.Task);
            var item = @operator.Operations.Snapshot().Single();
            try {
                Assert.That(@operator.Operations.Remove(item), Is.False);
            }
            finally {
                release.TrySetResult();
            }
            await ((OperationBase)item).Completion.WaitAsync(TimeSpan.FromSeconds(5));
        });

    /// <summary>Verifies that collection removal accepts a completed operation with an error.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CollectionRemove_RemovesACompletedFaultedOperation() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            @operator.Operate("failure", (_, _) => Task.FromException(new IOException("failure")));
            var item = @operator.Operations.Snapshot().Single();
            var removed = @operator.Operations.Remove(item);

            using (Assert.EnterMultipleScope()) {
                Assert.That(removed, Is.True);
                Assert.That(@operator.Operations.Count, Is.EqualTo(0));
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that collection removal rejects an operation owned by a different collection.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CollectionRemove_RejectsAnOperationFromAnotherCollection() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            var removed = @operator.Operations.Remove(new StubOperation());

            using (Assert.EnterMultipleScope()) {
                Assert.That(removed, Is.False);
                Assert.That(@operator.Operations.Count, Is.EqualTo(0));
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that collection removal rejects a null operation.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CollectionRemove_RejectsNull() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();

            using (Assert.EnterMultipleScope()) {
                Assert.That(@operator.Operations.Remove(null), Is.False);
                Assert.That(@operator.Operations.Count, Is.EqualTo(0));
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that the default collection cleanup removes completed roots and keeps running roots.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CollectionRemoveComplete_DefaultRemovesCompletedAndKeepsRunning() =>
        UiTestThread.Run(async () => {
            IOperator @operator = CreateOperator();
            var release = NewSignal();
            @operator.Operate("first failure", (_, _) => Task.FromException(new IOException("first")));
            @operator.Operate("second failure", (_, _) => Task.FromException(new IOException("second")));
            @operator.Operate("running", async (_, _) => await release.Task);
            var running = @operator.Operations.Snapshot().Last();
            var runningRemoved = NewSignal();
            var source = (INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems?.Contains(running) == true) {
                    runningRemoved.TrySetResult();
                }
            };
            source.CollectionChanged += handler;
            try {
                var removed = @operator.Operations.RemoveComplete();
                var remaining = @operator.Operations.Snapshot().ToArray();
                using (Assert.EnterMultipleScope()) {
                    Assert.That(removed, Is.EqualTo(2));
                    Assert.That(remaining, Is.EqualTo(new[] { running }));
                }
            }
            finally {
                release.TrySetResult();
                try {
                    await runningRemoved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally {
                    source.CollectionChanged -= handler;
                }
            }
        });

    /// <summary>Verifies that the error filter removes only completed operations matching its value.</summary>
    /// <param name="withError">Whether to select completed operations with or without errors.</param>
    /// <param name="expectedRemoved">The expected number of failed operations removed.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(true, 1)]
    [TestCase(false, 0)]
    public Task CollectionRemoveComplete_WithErrorFilterSelectsMatchingOperations(bool withError,
                                                                                  int expectedRemoved) =>
        UiTestThread.Run(() => {
            IOperator @operator = CreateOperator();
            @operator.Operate("failure", (_, _) => Task.FromException(new IOException("failure")));

            var removed = @operator.Operations.RemoveComplete(withError);

            using (Assert.EnterMultipleScope()) {
                Assert.That(removed, Is.EqualTo(expectedRemoved));
                Assert.That(@operator.Operations.Count, Is.EqualTo(1 - expectedRemoved));
            }
            return Task.CompletedTask;
        });

    /// <summary>
    /// Verifies that null, true, and false filters remove all, failed, and successful completed roots respectively.
    /// </summary>
    /// <param name="withError">The completion error filter to apply.</param>
    /// <param name="expectedRemoved">The number of roots expected to be removed.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(null, 2)]
    [TestCase(true, 1)]
    [TestCase(false, 1)]
    public Task CollectionRemoveComplete_FiltersSuccessAndFailureRoots(bool? withError, int expectedRemoved) =>
        UiTestThread.Run(async () => {
            var collection = new OperationCollection();
            IOperationCollection operations = collection;
            var release = NewSignal();
            var failed = new Operation("failed", (_, _) => Task.FromException(new IOException("failure")));
            var successful = new Operation("successful", (_, _) => Task.CompletedTask);
            var running = new Operation("running", async (_, _) => await release.Task);
            collection.Add(failed);
            collection.Add(successful);
            collection.Add(running);
            failed.Start();
            successful.Start();
            running.Start();

            try {
                await Task.WhenAll(failed.Completion, successful.Completion).WaitAsync(TimeSpan.FromSeconds(5));
                var removed = operations.RemoveComplete(withError);
                var remaining = operations.Snapshot().ToArray();
                var expected = withError switch {
                    true => new IOperation[] { successful, running },
                    false => new IOperation[] { failed, running },
                    _ => new IOperation[] { running }
                };
                using (Assert.EnterMultipleScope()) {
                    Assert.That(removed, Is.EqualTo(expectedRemoved));
                    Assert.That(remaining, Is.EqualTo(expected));
                }
            }
            finally {
                release.TrySetResult();
            }
            await running.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        });

    /// <summary>Verifies that a snapshot retains its membership after collection changes.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CollectionSnapshot_RetainsItemsAfterRemoval() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            @operator.Operate("failure", (_, _) => Task.FromException(new IOException("failure")));
            var snapshot = @operator.Operations.Snapshot();
            var item = snapshot.Single();
            Assert.That(@operator.Operations.Remove(item), Is.True);
            using (Assert.EnterMultipleScope()) {
                Assert.That(snapshot, Is.EqualTo(new[] { item }));
                Assert.That(@operator.Operations.Snapshot(), Is.Empty);
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that snapshots can be enumerated while another thread changes the roots.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public async Task CollectionSnapshot_ConcurrentMutations_DoNotInvalidateEnumeration() {
        var collection = new OperationCollection();
        IOperationCollection operations = collection;
        var started = NewSignal();
        var enumerate = NewSignal();
        var producer = Task.Run(() => {
            started.SetResult();
            enumerate.Task.GetAwaiter().GetResult();
            for (var index = 0; index < 500; index++) {
                var item = new Operation($"root {index}", (_, _) => Task.CompletedTask);
                collection.Add(item);
                item.Start();
                Assert.That(collection.Remove(item), Is.True);
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        enumerate.SetResult();
        do {
            var snapshot = operations.Snapshot();
            Assert.That(snapshot.All(item => item is not null), Is.True);
        } while (!producer.IsCompleted);
        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(operations.Snapshot(), Is.Empty);
    }

    /// <summary>Verifies that the collection source reflects additions and removals.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CollectionSource_TracksAddsAndRemovals() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var changes = new List<NotifyCollectionChangedAction>();
            var collectionChanges = NewSignal();
            var source = (INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source;
            source.CollectionChanged += (_, args) => {
                changes.Add(args.Action);
                if (changes.Count == 2) {
                    collectionChanges.TrySetResult();
                }
            };
            @operator.Operate("failure", (_, _) => Task.FromException(new IOException("failure")));
            var item = @operator.Operations.Snapshot().Single();
            @operator.Operations.Remove(item);
            await collectionChanges.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(changes, Is.EqualTo(new[] { NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Remove }));
            return Task.CompletedTask;
        });

    /// <summary>Verifies that the batch result reports whether any child operations were created.</summary>
    /// <param name="childCount">The number of child operations to create.</param>
    /// <param name="expected">The expected batch result.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(3, true)]
    public Task Children_ReturnsWhetherItCreatedAnyOperations(int childCount, bool expected) =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var factoryCalls = 0;
            var result = false;
            @operator.Operate("parent", async (progress, _) => {
                result = await progress.Children(Enumerable.Range(0, childCount), index => {
                    factoryCalls++;
                    return new OperationChild(index.ToString(), (_, _) => Task.CompletedTask);
                });
            });

            using (Assert.EnterMultipleScope()) {
                Assert.That(result, Is.EqualTo(expected));
                Assert.That(factoryCalls, Is.EqualTo(childCount));
            }
        });

    /// <summary>Verifies that a batch skips null child descriptions and reports whether other children were created.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Children_SkipsNullChildrenAndReportsWhetherAnyWereCreated() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var result = false;
            var factoryCalls = 0;
            @operator.Operate("parent", async (progress, _) => {
                result = await progress.Children(new[] { 0, 1, 2 }, index => {
                    factoryCalls++;
                    return index == 1
                        ? null
                        : new OperationChild(index.ToString(), (_, _) => Task.CompletedTask);
                });
            });

            using (Assert.EnterMultipleScope()) {
                Assert.That(result, Is.True);
                Assert.That(factoryCalls, Is.EqualTo(3));
            }
        });

    /// <summary>Verifies that a batch returns false when every child description is null.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Children_WithOnlyNullChildren_ReturnsFalse() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var result = true;
            @operator.Operate("parent", async (progress, _) => {
                result = await progress.Children(new[] { 1, 2 }, _ => null);
            });

            Assert.That(result, Is.False);
        });

    /// <summary>Verifies that batch creation evaluates the child factory for each source item.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Children_FactoryIsEvaluatedForEveryItem() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var factoryCalls = 0;
            @operator.Operate("parent", async (progress, _) => {
                await progress.Children(Enumerable.Range(0, 5), index => {
                    factoryCalls++;
                    return new OperationChild(index.ToString(), (_, _) => Task.CompletedTask);
                });
            });

            Assert.That(factoryCalls, Is.EqualTo(5));
        });

    /// <summary>Verifies that a batch starts all children and keeps their parent running until they finish.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Children_StartsAllChildrenAndKeepsTheParentRunningUntilTheyFinish() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var childrenStarted = NewSignal<int>();
            var release = NewSignal();
            var startedCount = 0;
            var root = Start(@operator, "parent", async (progress, _) => {
                var children = progress.Children(Enumerable.Range(0, 3), index => new OperationChild(
                    index.ToString(), async (_, _) => {
                        startedCount++;
                        await release.Task;
                    }));
                childrenStarted.TrySetResult(startedCount);
                await children;
            });
            try {
                var startedBeforeRelease = await childrenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var parentWasStillRunning = !root.Complete;
                release.SetResult();
                await ((OperationBase)root).Completion.WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope()) {
                    Assert.That(startedBeforeRelease, Is.EqualTo(3));
                    Assert.That(parentWasStillRunning, Is.True);
                    Assert.That(root.Complete, Is.True);
                }
            }
            finally {
                release.TrySetResult();
            }
        });

    /// <summary>Verifies that a failed batch child contributes to the parent's error state.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Children_ChildFailureMarksTheParentAsFaulted() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            @operator.Operate("parent", async (progress, _) => {
                await progress.Children(new[] { 1 }, _ => new OperationChild(
                    "child", (_, _) => Task.FromException(new IOException("child failed"))));
            });
            var parent = (OperationBase)@operator.Operations.Snapshot().Single();
            var child = (await WaitForChildren(parent)).Single();
            var wasRelevant = ((OperationCollection)@operator.Operations).Relevant;
            var removed = @operator.Operations.Remove(parent);

            using (Assert.EnterMultipleScope()) {
                Assert.That(parent.Complete, Is.True);
                Assert.That(parent.CompleteWithError, Is.True);
                Assert.That(child.CompleteWithError, Is.True);
                Assert.That(child.Error, Is.InstanceOf<IOException>());
                Assert.That(parent.Relevant, Is.True);
                Assert.That(child.Relevant, Is.True);
                Assert.That(wasRelevant, Is.True);
                Assert.That(removed, Is.True);
                Assert.That(((OperationCollection)@operator.Operations).Relevant, Is.False);
            }
        });

    /// <summary>Verifies that a failed parent still waits for its registered child.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ParentFailure_DoesNotFinishUntilItsStartedChildFinishes() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var childStarted = NewSignal();
            var release = NewSignal();
            var root = Start(@operator, "parent", (progress, token) => {
                _ = progress.Child("child", async (_, _) => {
                    childStarted.TrySetResult();
                    await release.Task;
                });
                throw new IOException("parent failed");
            });
            try {
                var child = (await WaitForChildren(root)).Single();
                await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var parentWaitedForChild = !root.Complete;
                release.SetResult();
                await ((OperationBase)root).Completion.WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope()) {
                    Assert.That(parentWaitedForChild, Is.True);
                    Assert.That(root.Complete, Is.True);
                    Assert.That(root.CompleteWithError, Is.True);
                    Assert.That(((OperationBase)root).Error, Is.InstanceOf<IOException>());
                    Assert.That(child.Complete, Is.True);
                }
            }
            finally {
                release.TrySetResult();
            }
        });

    /// <summary>Verifies that numeric reports aggregate through a hierarchy of child operations.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Change_ProgressAndTargetAggregateThroughNestedChildren() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var root = Start(@operator, "root", async (progress, _) => {
                progress.Change(setProgress: 2, setTarget: 5);
                await progress.Child("child", async (childProgress, _) => {
                    childProgress.Change(addProgress: 1, addTarget: 3);
                    await childProgress.Child("grandchild", (grandchildProgress, _) => {
                        grandchildProgress.Change(addProgress: 2, addTarget: 4);
                        return Task.CompletedTask;
                    });
                });
            });
            var child = (await WaitForChildren(root)).Single();
            var grandchild = (await WaitForChildren(child)).Single();

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)root).Progress, Is.EqualTo(5L));
                Assert.That(((OperationBase)root).Target, Is.EqualTo(12L));
                Assert.That(child.Progress, Is.EqualTo(3L));
                Assert.That(child.Target, Is.EqualTo(7L));
                Assert.That(grandchild.Progress, Is.EqualTo(2L));
                Assert.That(grandchild.Target, Is.EqualTo(4L));
                Assert.That(((OperationBase)root).Depth, Is.EqualTo(0));
                Assert.That(((OperationBase)root).DepthString, Is.EqualTo(""));
                Assert.That(child.Depth, Is.EqualTo(1));
                Assert.That(child.DepthString, Is.EqualTo(">"));
                Assert.That(grandchild.Depth, Is.EqualTo(2));
                Assert.That(grandchild.DepthString, Is.EqualTo(">>"));
            }
        });

    /// <summary>Verifies that setting a child's numeric values adjusts ancestors by the difference.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Change_SettingChildProgressAndTargetPropagatesOnlyTheDifference() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var root = Start(@operator, "root", async (progress, _) => {
                progress.Change(setProgress: 2, setTarget: 5);
                await progress.Child("child", (childProgress, _) => {
                    childProgress.Change(setProgress: 3, setTarget: 4);
                    childProgress.Change(setProgress: 1, setTarget: 2);
                    return Task.CompletedTask;
                });
            });
            var child = (await WaitForChildren(root)).Single();

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)root).Progress, Is.EqualTo(3L));
                Assert.That(((OperationBase)root).Target, Is.EqualTo(7L));
                Assert.That(child.Progress, Is.EqualTo(1L));
                Assert.That(child.Target, Is.EqualTo(2L));
            }
        });

    /// <summary>Verifies the percentage calculated from reported progress and target values.</summary>
    /// <param name="target">The reported target value.</param>
    /// <param name="progress">The reported progress value.</param>
    /// <param name="expected">The expected progress percentage.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(10L, 2L, 20d)]
    [TestCase(8L, 12L, 150d)]
    [TestCase(3L, 0L, 0d)]
    [TestCase(0L, 5L, 100d)]
    public Task Change_CalculatesProgressPercent(long target, long progress, double expected) =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            var operation = CaptureWithChange(@operator, item => item.Change(setTarget: target, setProgress: progress));

            Assert.That(operation.ProgressPercent, Is.EqualTo(expected));
            return Task.CompletedTask;
        });

    /// <summary>Verifies percentage behavior for an operation with a zero target before and after completion.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Change_ZeroTargetHasZeroPercentUntilTheOperationCompletes() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var changed = NewSignal();
            var release = NewSignal();
            var operation = Start(@operator, "zero target", async (progress, _) => {
                progress.Change(setProgress: 5);
                changed.TrySetResult();
                await release.Task;
            });
            try {
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var percentWhileRunning = operation.ProgressPercent;
                release.SetResult();
                await ((OperationBase)operation).Completion.WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope()) {
                    Assert.That(percentWhileRunning, Is.EqualTo(0d));
                    Assert.That(operation.ProgressPercent, Is.EqualTo(100d));
                }
            }
            finally {
                release.TrySetResult();
            }
        });

    /// <summary>Verifies numeric display defaults, explicit strings, and restoring the defaults.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Change_UpdatesNumericDefaultsAndAllowsExplicitDisplayStrings() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            Operation operation = null;
            var displayValues = new List<(string Progress, string Target)>();
            Start(@operator, "strings", (progress, _) => {
                progress.Change(setProgress: 1, setTarget: 2, progressString: "one", targetString: "two");
                displayValues.Add((((OperationBase)operation).ProgressString, ((OperationBase)operation).TargetString));
                progress.Change(addProgress: 1);
                displayValues.Add((((OperationBase)operation).ProgressString, ((OperationBase)operation).TargetString));
                return Task.CompletedTask;
            }, started => operation = started);

            using (Assert.EnterMultipleScope()) {
                Assert.That(displayValues.Select(value => value.Progress), Is.EqualTo(new[] { "one", "2" }));
                Assert.That(displayValues.Select(value => value.Target), Is.EqualTo(new[] { "two", "2" }));
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that metadata reports update the operation's name and detail text.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Change_UpdatesNameAndDataWhenProvided() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            Operation operation = null;
            Start(@operator, "initial", (progress, _) => {
                progress.Change(name: "renamed", data: "copying");
                return Task.CompletedTask;
            }, started => operation = started);

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).Name, Is.EqualTo("renamed"));
                Assert.That(((OperationBase)operation).Data, Is.EqualTo("copying"));
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that progress notifications support reentrant numeric reports.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Change_NotificationHandlerCanReportProgressReentrantly() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            Operation operation = null;
            var changedAgain = false;
            Start(@operator, "reentrant", (progress, _) => {
                ((INotifyPropertyChanged)operation).PropertyChanged += (_, args) => {
                    if (!changedAgain && args.PropertyName == nameof(OperationBase.Progress)) {
                        changedAgain = true;
                        progress.Change(addProgress: 1);
                    }
                };
                progress.Change(setProgress: 1, setTarget: 2);
                return Task.CompletedTask;
            }, started => operation = started);

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).Progress, Is.EqualTo(2L));
                Assert.That(((OperationBase)operation).Target, Is.EqualTo(2L));
                Assert.That(((OperationBase)operation).ProgressPercent, Is.EqualTo(100d));
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that numeric reports notify dependent display properties.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Change_NotifiesProgressAndTargetDependents() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            var propertyNames = new HashSet<string>();
            Start(@operator, "notifications", (progress, _) => {
                progress.Change(setProgress: 2, setTarget: 4, progressString: "2 items", targetString: "4 items");
                return Task.CompletedTask;
            }, operation => ((INotifyPropertyChanged)operation).PropertyChanged += (_, change) =>
                propertyNames.Add(change.PropertyName));

            Assert.That(propertyNames, Is.SupersetOf(new[] {
                nameof(OperationBase.Progress), nameof(OperationBase.Target),
                nameof(OperationBase.ProgressPercent), nameof(OperationBase.ProgressString),
                nameof(OperationBase.TargetString)
            }));
            return Task.CompletedTask;
        });

    /// <summary>Verifies that child registration is rejected after the parent delegate returns.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Child_CannotBeAddedAfterTheParentDelegateCompletes() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            IOperationProgress savedProgress = null;
            @operator.Operate("parent", (progress, _) => {
                savedProgress = progress;
                return Task.CompletedTask;
            });

            Assert.That(async () => await savedProgress.Child("late", (_, _) => Task.CompletedTask),
                        Throws.InvalidOperationException);
        });

    /// <summary>Verifies that child registration rejects a null work delegate.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Child_RejectsANullTask() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            @operator.Operate("parent", async (progress, _) => await progress.Child("invalid", null));
            var operation = (OperationBase)@operator.Operations.Snapshot().Single();

            using (Assert.EnterMultipleScope()) {
                Assert.That(operation.Error, Is.InstanceOf<ArgumentNullException>());
                Assert.That(operation.CompleteWithError, Is.True);
            }
        });

    /// <summary>Verifies that batch creation rejects a null source sequence.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Children_RejectsANullSource() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            @operator.Operate("parent", async (progress, _) =>
                await progress.Children<int>(null, _ => null));
            var operation = (OperationBase)@operator.Operations.Snapshot().Single();

            using (Assert.EnterMultipleScope()) {
                Assert.That(operation.Error, Is.InstanceOf<ArgumentNullException>());
                Assert.That(operation.CompleteWithError, Is.True);
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that batch creation rejects a null child factory.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Children_RejectsANullFactory() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            @operator.Operate("parent", async (progress, _) =>
                await progress.Children(Array.Empty<int>(), null));
            var operation = (OperationBase)@operator.Operations.Snapshot().Single();

            using (Assert.EnterMultipleScope()) {
                Assert.That(operation.Error, Is.InstanceOf<ArgumentNullException>());
                Assert.That(operation.CompleteWithError, Is.True);
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that requested cancellation cancels the delegate's token and completes without an error.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task OperationCancellation_CancelsTheDelegateTokenAndCompletesWithoutError() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var started = NewSignal<CancellationToken>();
            var operation = Start(@operator, "cancel", async (_, token) => {
                started.TrySetResult(token);
                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) {
                }
            });
            var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            operation.Cancel();
            await ((OperationBase)operation).Completion.WaitAsync(TimeSpan.FromSeconds(5));

            using (Assert.EnterMultipleScope()) {
                Assert.That(token.IsCancellationRequested, Is.True);
                Assert.That(operation.Complete, Is.True);
                Assert.That(operation.CompleteWithError, Is.False);
                Assert.That(@operator.Operations.Count, Is.EqualTo(0));
            }
        });

    /// <summary>Verifies that repeated cancellation requests invoke token callbacks only once.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task OperationCancellation_IsIdempotent() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var started = NewSignal();
            var operation = Start(@operator, "cancel", async (_, token) => {
                started.TrySetResult();
                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) {
                }
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            operation.Cancel();
            operation.Cancel();
            await ((OperationBase)operation).Completion.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(operation.Complete, Is.True);
        });

    /// <summary>Verifies that cancellation and completion update command eligibility.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task OperationCancellation_UpdatesCancelAndRemoveCapabilities() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var started = NewSignal();
            var operation = Start(@operator, "cancel", async (_, token) => {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var whileRunning = (operation.CanCancel, operation.CanRemove);
            operation.Cancel();
            var whileCanceling = (operation.CanCancel, operation.CanRemove, operation.Complete);
            await ((OperationBase)operation).Completion.WaitAsync(TimeSpan.FromSeconds(5));
            var afterCompletion = (operation.CanCancel, operation.CanRemove);

            using (Assert.EnterMultipleScope()) {
                Assert.That(whileRunning.CanCancel, Is.True);
                Assert.That(whileRunning.CanRemove, Is.False);
                Assert.That(whileCanceling.CanCancel, Is.False);
                /*
                 * On .NET Framework, canceling the token source runs the delegate's awaiting continuation inline,
                 * so the operation completes (and becomes removable) before Cancel() returns. On .NET (Core),
                 * that continuation is queued, so the operation is still completing when Cancel() returns.
                 */
#if NETFRAMEWORK
                Assert.That(whileCanceling.Complete, Is.True);
                Assert.That(whileCanceling.CanRemove, Is.True);
#else
                Assert.That(whileCanceling.Complete, Is.False);
                Assert.That(whileCanceling.CanRemove, Is.False);
#endif
                Assert.That(afterCompletion.CanCancel, Is.False);
                Assert.That(afterCompletion.CanRemove, Is.True);
            }
        });

    /// <summary>Verifies that cancellation rejects new children without producing an operation error.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task OperationCancellation_RejectsNewChildrenWithoutMarkingCancellationAsAnError() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var started = NewSignal();
            var savedProgress = default(IOperationProgress);
            var operation = Start(@operator, "cancel", async (progress, token) => {
                savedProgress = progress;
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            operation.Cancel();
            Exception childError = null;
            try {
                await savedProgress.Child("too late", (_, _) => Task.CompletedTask);
            }
            catch (Exception exception) {
                childError = exception;
            }
            await ((OperationBase)operation).Completion.WaitAsync(TimeSpan.FromSeconds(5));

            using (Assert.EnterMultipleScope()) {
                Assert.That(childError, Is.InstanceOf<OperationCanceledException>());
                Assert.That(operation.Complete, Is.True);
                Assert.That(operation.CompleteWithError, Is.False);
            }
        });

    /// <summary>Verifies that an unsolicited cancellation exception is recorded as an operation error.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task OperationCanceledExceptionWithoutRequestedCancellation_IsRecordedAsAnError() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            @operator.Operate("unexpected cancellation", (_, _) =>
                Task.FromException(new OperationCanceledException("not requested")));
            var operation = (OperationBase)@operator.Operations.Snapshot().Single();

            using (Assert.EnterMultipleScope()) {
                Assert.That(operation.Error, Is.InstanceOf<OperationCanceledException>());
                Assert.That(operation.Complete, Is.True);
                Assert.That(operation.CompleteWithError, Is.True);
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that cancellation aggregates callback failures while still canceling the operation.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task OperationCancellation_AggregatesCallbackFailuresAndStillCancels() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var started = NewSignal<CancellationToken>();
            var operation = Start(@operator, "cancel", async (_, token) => {
                token.Register(() => throw new InvalidOperationException("callback failure"));
                started.TrySetResult(token);
                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) {
                }
            });
            var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AggregateException cancellationError = null;
            try {
                operation.Cancel();
            }
            catch (AggregateException exception) {
                cancellationError = exception;
            }
            await ((OperationBase)operation).Completion.WaitAsync(TimeSpan.FromSeconds(5));

            using (Assert.EnterMultipleScope()) {
                Assert.That(cancellationError?.InnerExceptions.SingleOrDefault(), Is.InstanceOf<InvalidOperationException>());
                Assert.That(token.IsCancellationRequested, Is.True);
                Assert.That(operation.CompleteWithError, Is.False);
            }
        });

    /// <summary>Verifies that cancellation reaches existing child operations.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task OperationCancellation_PropagatesToChildren() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var parentStarted = NewSignal<CancellationToken>();
            var childStarted = NewSignal<CancellationToken>();
            var operation = Start(@operator, "parent", async (progress, token) => {
                parentStarted.TrySetResult(token);
                await progress.Child("child", async (_, childToken) => {
                    childStarted.TrySetResult(childToken);
                    try {
                        await Task.Delay(Timeout.InfiniteTimeSpan, childToken);
                    }
                    catch (OperationCanceledException) when (childToken.IsCancellationRequested) {
                    }
                });
            });
            var parentToken = await parentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var childToken = await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            operation.Cancel();
            await ((OperationBase)operation).Completion.WaitAsync(TimeSpan.FromSeconds(5));

            using (Assert.EnterMultipleScope()) {
                Assert.That(parentToken.IsCancellationRequested, Is.True);
                Assert.That(childToken.IsCancellationRequested, Is.True);
                Assert.That(operation.Complete, Is.True);
                Assert.That(operation.CompleteWithError, Is.False);
            }
        });

    /// <summary>
    /// Verifies that subtree cancellation releases each active operation's token source lease.
    /// </summary>
    /// <returns>
    /// A task representing execution of the test.
    /// </returns>
    [TestCase(false)]
    [TestCase(true)]
    public Task OperationCancellation_ReleasesEveryActiveTreeLeaseToItsOwner(bool throwCallback) =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var rootStarted = NewSignal<CancellationToken>();
            var childStarted = NewSignal<CancellationToken>();
            var grandchildStarted = NewSignal<CancellationToken>();
            var operation = Start(@operator, "root", async (progress, token) => {
                rootStarted.TrySetResult(token);
                await progress.Child("child", async (childProgress, childToken) => {
                    childStarted.TrySetResult(childToken);
                    await childProgress.Child("grandchild", async (_, grandchildToken) => {
                        if (throwCallback) {
                            grandchildToken.Register(() => throw new InvalidOperationException("callback failure"));
                        }
                        grandchildStarted.TrySetResult(grandchildToken);
                        try {
                            await Task.Delay(Timeout.InfiniteTimeSpan, grandchildToken);
                        }
                        catch (OperationCanceledException) when (grandchildToken.IsCancellationRequested) {
                        }
                    });
                    try {
                        await Task.Delay(Timeout.InfiniteTimeSpan, childToken);
                    }
                    catch (OperationCanceledException) when (childToken.IsCancellationRequested) {
                    }
                });
                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) {
                }
            });

            await rootStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await grandchildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var child = (await WaitForChildren(operation)).Single();
            var grandchild = (await WaitForChildren(child)).Single();
            var operations = new[] { operation, child, grandchild };
            var tokenSources = operations
                .Select(item => GetPrivateField<CancellationTokenSource>(item, "TokenSource"))
                .ToArray();

            AggregateException cancellationError = null;
            try {
                operation.Cancel();
            }
            catch (AggregateException exception) {
                cancellationError = exception;
            }
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            using (Assert.EnterMultipleScope()) {
                if (throwCallback) {
                    Assert.That(cancellationError?.InnerExceptions.SingleOrDefault(),
                                Is.InstanceOf<InvalidOperationException>());
                }
                else {
                    Assert.That(cancellationError, Is.Null);
                }
                foreach (var item in operations) {
                    Assert.That(GetPrivateField<int>(item, "TokenSourceCancellationCount"), Is.Zero, item.Name);
                    Assert.That(GetPrivateField<bool>(item, "TokenSourceDisposalPending"), Is.False, item.Name);
                }
                Assert.That(tokenSources.All(IsDisposed), Is.True);
            }
        });

    /// <summary>
    /// Verifies that completed descendants release cancellation leases after a held callback returns.
    /// </summary>
    /// <returns>
    /// A task representing execution of the test.
    /// </returns>
    [Test]
    public Task OperationCancellation_ReleasesCompletedDescendantLeasesAfterCallbacksFinish() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var rootStarted = NewSignal<CancellationToken>();
            var childStarted = NewSignal<CancellationToken>();
            var grandchildStarted = NewSignal<CancellationToken>();
            var callbackEntered = NewSignal();
            var releaseCallback = NewSignal();
            var releaseGrandchild = NewSignal();
            var operation = Start(@operator, "root", async (progress, token) => {
                rootStarted.TrySetResult(token);
                await progress.Child("child", async (childProgress, childToken) => {
                    childStarted.TrySetResult(childToken);
                    await childProgress.Child("grandchild", async (_, grandchildToken) => {
                        grandchildToken.Register(() => {
                            callbackEntered.TrySetResult();
                            releaseCallback.Task.GetAwaiter().GetResult();
                        });
                        grandchildStarted.TrySetResult(grandchildToken);
                        await releaseGrandchild.Task;
                    });
                });
            });
            Task cancellation = null;
            try {
                await rootStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await grandchildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var child = (await WaitForChildren(operation)).Single();
                var grandchild = (await WaitForChildren(child)).Single();
                var operations = new[] { operation, child, grandchild };
                var tokenSources = operations
                    .Select(item => GetPrivateField<CancellationTokenSource>(item, "TokenSource"))
                    .ToArray();

                cancellation = Task.Run(operation.Cancel);
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                releaseGrandchild.TrySetResult();
                await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope()) {
                    foreach (var item in operations) {
                        Assert.That(item.Complete, Is.True, item.Name);
                        Assert.That(GetPrivateField<int>(item, "TokenSourceCancellationCount"),
                                    Is.EqualTo(1), item.Name);
                        Assert.That(GetPrivateField<bool>(item, "TokenSourceDisposalPending"), Is.True, item.Name);
                    }
                    Assert.That(tokenSources.All(tokenSource => !IsDisposed(tokenSource)), Is.True);
                }

                releaseCallback.TrySetResult();
                await cancellation.WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope()) {
                    foreach (var item in operations) {
                        Assert.That(GetPrivateField<int>(item, "TokenSourceCancellationCount"), Is.Zero, item.Name);
                        Assert.That(GetPrivateField<bool>(item, "TokenSourceDisposalPending"), Is.False, item.Name);
                    }
                    Assert.That(tokenSources.All(IsDisposed), Is.True);
                }
            }
            finally {
                releaseGrandchild.TrySetResult();
                releaseCallback.TrySetResult();
                if (cancellation is not null) {
                    await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
                }
                await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });

    /// <summary>Verifies that numeric reporting makes still-running work relevant after the display delay.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Relevant_BecomesTrueAfterAProgressChangeRemainsActiveForTheDelay() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var release = NewSignal();
            var becameRelevant = NewSignal();
            var operation = Start(@operator, "long operation", async (progress, _) => {
                progress.Change(addProgress: 1, addTarget: 1);
                await release.Task;
            }, started => ((OperationBase)started).RelevantChanged += (_, _) => becameRelevant.TrySetResult());
            try {
                await becameRelevant.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var visible = operation.Relevant && ((OperationCollection)@operator.Operations).Relevant;
                release.SetResult();
                await ((OperationBase)operation).Completion.WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope()) {
                    Assert.That(visible, Is.True);
                    Assert.That(operation.Relevant, Is.True);
                    Assert.That(((OperationCollection)@operator.Operations).Relevant, Is.False);
                    Assert.That(@operator.Operations.Count, Is.EqualTo(0));
                }
            }
            finally {
                release.TrySetResult();
            }
        });

    private static Operation CaptureWithChange(IOperator @operator, Action<IOperationProgress> change) {
        return Start(@operator, "change", (progress, _) => {
            change(progress);
            return Task.CompletedTask;
        });
    }

    private sealed class StubOperation : IOperation {
        public bool Complete => true;
        public bool CompleteWithError => false;
    }
}

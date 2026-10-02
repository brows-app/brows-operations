using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
            var item = @operator.Operations.AsEnumerable().Single();

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
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
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
            var item = @operator.Operations.AsEnumerable().Single();
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
            var item = @operator.Operations.AsEnumerable().Single();
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
            var running = @operator.Operations.AsEnumerable().Last();
            var runningRemoved = NewSignal();
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems?.Contains(running) == true) {
                    runningRemoved.TrySetResult();
                }
            };
            source.CollectionChanged += handler;
            try {
                var removed = @operator.Operations.RemoveComplete();
                var remaining = @operator.Operations.AsEnumerable().ToArray();
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
                var remaining = operations.AsEnumerable().ToArray();
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

    /// <summary>Verifies that each public enumeration path exposes the same root operations.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CollectionEnumerators_ExposeTheSameItemsAsAsEnumerableAndSource() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            @operator.Operate("failure", (_, _) => Task.FromException(new IOException("failure")));
            var expected = @operator.Operations.AsEnumerable().ToArray();
            var enumerated = new List<IOperation>();
            using (var iterator = @operator.Operations.GetEnumerator()) {
                while (iterator.MoveNext()) {
                    enumerated.Add(iterator.Current);
                }
            }
            var sourceItems = @operator.Operations.Source.Cast<IOperation>();

            Assert.That(enumerated.Concat(sourceItems), Is.EqualTo(expected.Concat(expected)));
            return Task.CompletedTask;
        });

    /// <summary>Verifies that the collection source reflects additions and removals.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CollectionSource_TracksAddsAndRemovals() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            var changes = new List<NotifyCollectionChangedAction>();
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            source.CollectionChanged += (_, args) => changes.Add(args.Action);
            @operator.Operate("failure", (_, _) => Task.FromException(new IOException("failure")));
            var item = @operator.Operations.AsEnumerable().Single();
            @operator.Operations.Remove(item);

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
            Operation root = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    root = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("parent", async (progress, _) => {
                var children = progress.Children(Enumerable.Range(0, 3), index => new OperationChild(
                    index.ToString(), async (_, _) => {
                        startedCount++;
                        await release.Task;
                    }));
                childrenStarted.TrySetResult(startedCount);
                await children;
            });
            source.CollectionChanged -= handler;
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
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            @operator.Operate("parent", async (progress, _) => {
                await progress.Children(new[] { 1 }, _ => new OperationChild(
                    "child", (_, _) => Task.FromException(new IOException("child failed"))));
            });
            var parent = (OperationBase)@operator.Operations.AsEnumerable().Single();
            var child = ((IEnumerable)parent.ChildSource).Cast<OperationBase>().Single();
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
            return Task.CompletedTask;
        });

    /// <summary>Verifies that a failed parent still waits for its registered child.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ParentFailure_DoesNotFinishUntilItsStartedChildFinishes() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var childStarted = NewSignal();
            var release = NewSignal();
            Operation root = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    root = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("parent", (progress, token) => {
                _ = progress.Child("child", async (_, _) => {
                    childStarted.TrySetResult();
                    await release.Task;
                });
                throw new IOException("parent failed");
            });
            source.CollectionChanged -= handler;
            var child = ((IEnumerable)((OperationBase)root).ChildSource).Cast<OperationBase>().Single();
            try {
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
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            Operation root = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    root = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("root", async (progress, _) => {
                progress.Change(setProgress: 2, setTarget: 5);
                await progress.Child("child", async (childProgress, _) => {
                    childProgress.Change(addProgress: 1, addTarget: 3);
                    await childProgress.Child("grandchild", (grandchildProgress, _) => {
                        grandchildProgress.Change(addProgress: 2, addTarget: 4);
                        return Task.CompletedTask;
                    });
                });
            });
            source.CollectionChanged -= handler;
            var child = ((IEnumerable)((OperationBase)root).ChildSource).Cast<OperationBase>().Single();
            var grandchild = ((IEnumerable)child.ChildSource).Cast<OperationBase>().Single();

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
            return Task.CompletedTask;
        });

    /// <summary>Verifies that setting a child's numeric values adjusts ancestors by the difference.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Change_SettingChildProgressAndTargetPropagatesOnlyTheDifference() =>
        UiTestThread.Run(() => {
            var @operator = CreateOperator();
            Operation root = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    root = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("root", async (progress, _) => {
                progress.Change(setProgress: 2, setTarget: 5);
                await progress.Child("child", (childProgress, _) => {
                    childProgress.Change(setProgress: 3, setTarget: 4);
                    childProgress.Change(setProgress: 1, setTarget: 2);
                    return Task.CompletedTask;
                });
            });
            source.CollectionChanged -= handler;
            var child = ((IEnumerable)((OperationBase)root).ChildSource).Cast<OperationBase>().Single();

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)root).Progress, Is.EqualTo(3L));
                Assert.That(((OperationBase)root).Target, Is.EqualTo(7L));
                Assert.That(child.Progress, Is.EqualTo(1L));
                Assert.That(child.Target, Is.EqualTo(2L));
            }
            return Task.CompletedTask;
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
            Operation operation = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("zero target", async (progress, _) => {
                progress.Change(setProgress: 5);
                changed.TrySetResult();
                await release.Task;
            });
            source.CollectionChanged -= handler;
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
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("strings", (progress, _) => {
                progress.Change(setProgress: 1, setTarget: 2, progressString: "one", targetString: "two");
                displayValues.Add((((OperationBase)operation).ProgressString, ((OperationBase)operation).TargetString));
                progress.Change(addProgress: 1);
                displayValues.Add((((OperationBase)operation).ProgressString, ((OperationBase)operation).TargetString));
                return Task.CompletedTask;
            });
            source.CollectionChanged -= handler;

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
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("initial", (progress, _) => {
                progress.Change(name: "renamed", data: "copying");
                return Task.CompletedTask;
            });
            source.CollectionChanged -= handler;

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
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("reentrant", (progress, _) => {
                ((INotifyPropertyChanged)operation).PropertyChanged += (_, args) => {
                    if (!changedAgain && args.PropertyName == nameof(OperationBase.Progress)) {
                        changedAgain = true;
                        progress.Change(addProgress: 1);
                    }
                };
                progress.Change(setProgress: 1, setTarget: 2);
                return Task.CompletedTask;
            });
            source.CollectionChanged -= handler;

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
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    ((INotifyPropertyChanged)args.NewItems[0]).PropertyChanged += (_, change) => propertyNames.Add(change.PropertyName);
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("notifications", (progress, _) => {
                progress.Change(setProgress: 2, setTarget: 4, progressString: "2 items", targetString: "4 items");
                return Task.CompletedTask;
            });
            source.CollectionChanged -= handler;

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
            var operation = (OperationBase)@operator.Operations.AsEnumerable().Single();

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
            var operation = (OperationBase)@operator.Operations.AsEnumerable().Single();

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
            var operation = (OperationBase)@operator.Operations.AsEnumerable().Single();

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
            Operation operation = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("cancel", async (_, token) => {
                started.TrySetResult(token);
                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) {
                }
            });
            source.CollectionChanged -= handler;
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
            Operation operation = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("cancel", async (_, token) => {
                started.TrySetResult();
                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) {
                }
            });
            source.CollectionChanged -= handler;
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
            Operation operation = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("cancel", async (_, token) => {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            source.CollectionChanged -= handler;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var whileRunning = (operation.CanCancel, operation.CanRemove);
            operation.Cancel();
            var whileCanceling = (operation.CanCancel, operation.CanRemove);
            await ((OperationBase)operation).Completion.WaitAsync(TimeSpan.FromSeconds(5));
            var afterCompletion = (operation.CanCancel, operation.CanRemove);

            using (Assert.EnterMultipleScope()) {
                Assert.That(whileRunning.CanCancel, Is.True);
                Assert.That(whileRunning.CanRemove, Is.False);
                Assert.That(whileCanceling.CanCancel, Is.False);
                Assert.That(whileCanceling.CanRemove, Is.False);
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
            Operation operation = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("cancel", async (progress, token) => {
                savedProgress = progress;
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            source.CollectionChanged -= handler;
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
            var operation = (OperationBase)@operator.Operations.AsEnumerable().Single();

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
            Operation operation = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("cancel", async (_, token) => {
                token.Register(() => throw new InvalidOperationException("callback failure"));
                started.TrySetResult(token);
                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) {
                }
            });
            source.CollectionChanged -= handler;
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
            Operation operation = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("parent", async (progress, token) => {
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
            source.CollectionChanged -= handler;
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

    /// <summary>Verifies that numeric reporting makes still-running work relevant after the display delay.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Relevant_BecomesTrueAfterAProgressChangeRemainsActiveForTheDelay() =>
        UiTestThread.Run(async () => {
            var @operator = CreateOperator();
            var release = NewSignal();
            var becameRelevant = NewSignal();
            Operation operation = null;
            var source = (INotifyCollectionChanged)@operator.Operations.Source;
            NotifyCollectionChangedEventHandler handler = (_, args) => {
                if (args.Action == NotifyCollectionChangedAction.Add) {
                    operation = (Operation)args.NewItems[0];
                    ((OperationBase)operation).RelevantChanged += (_, _) => becameRelevant.TrySetResult();
                }
            };
            source.CollectionChanged += handler;
            @operator.Operate("long operation", async (progress, _) => {
                progress.Change(addProgress: 1, addTarget: 1);
                await release.Task;
            });
            source.CollectionChanged -= handler;
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
        Operation operation = null;
        var source = (INotifyCollectionChanged)@operator.Operations.Source;
        NotifyCollectionChangedEventHandler handler = (_, args) => {
            if (args.Action == NotifyCollectionChangedAction.Add) {
                operation = (Operation)args.NewItems[0];
            }
        };
        source.CollectionChanged += handler;
        @operator.Operate("change", (progress, _) => {
            change(progress);
            return Task.CompletedTask;
        });
        source.CollectionChanged -= handler;
        return operation;
    }

    private sealed class StubOperation : IOperation {
        public bool Complete => true;
        public bool CompleteWithError => false;
    }
}

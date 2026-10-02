using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>Tests regressions in hierarchical lifetime tracking, notifications, cancellation, and removal.</summary>
[TestFixture]
public sealed class RegressionTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Start work through IOperator. Inspect internals only for state the public interfaces do not expose.
    private static Operation Start(IOperator @operator, OperationDelegate task) {
        Operation operation = null;
        var source = (INotifyCollectionChanged)@operator.Operations.Source;
        NotifyCollectionChangedEventHandler added = (_, e) => {
            if (e.Action == NotifyCollectionChangedAction.Add) operation = (Operation)e.NewItems[0];
        };
        source.CollectionChanged += added;
        try { @operator.Operate("root", task); }
        finally { source.CollectionChanged -= added; }
        return operation;
    }

    private static OperationBase[] Children(OperationBase operation) =>
        ((IEnumerable)operation.ChildSource).Cast<OperationBase>().ToArray();

    /// <summary>Verifies that synchronous child success allows the completed root to be removed.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task SynchronousChildSuccess_RemovesTheCompletedRoot() => UiTestThread.Run(() => {
        IOperator @operator = new Operator();
        Start(@operator, async (progress, _) => await progress.Child("child", (_, _) => Task.CompletedTask));
        Assert.That(@operator.Operations.Count, Is.Zero);
        return Task.CompletedTask;
    });

    /// <summary>Verifies count notifications for additions and successful removals.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CountNotifications_ReportAddAndSuccessfulRemovalOnly() => UiTestThread.Run(() => {
        IOperator @operator = new Operator();
        var counts = new List<int>();
        ((INotifyPropertyChanged)@operator.Operations).PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(IOperationCollection.Count)) counts.Add(@operator.Operations.Count);
        };
        var root = Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
        @operator.Operations.Remove(null);
        @operator.Operations.Remove(root);
        @operator.Operations.Remove(root);
        Assert.That(counts, Is.EqualTo(new[] { 1, 0 }));
        return Task.CompletedTask;
    });

    /// <summary>Verifies that a rejected removal of running work does not publish a count change.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CountNotifications_RejectingRunningRemovalDoesNotNotify() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = Start(@operator, async (_, _) => await release.Task);
        var notifications = 0;
        ((INotifyPropertyChanged)@operator.Operations).PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(IOperationCollection.Count)) notifications++;
        };
        var removed = @operator.Operations.Remove(root);
        var countBeforeCompletion = notifications;
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(removed, Is.False);
            Assert.That(countBeforeCompletion, Is.EqualTo(0));
            Assert.That(notifications, Is.EqualTo(1));
        }
    });

    /// <summary>
    /// Verifies that registration closes when the parent delegate returns, while existing children are still running.
    /// </summary>
    /// <param name="parentFails">Whether the parent delegate fails before joining its child.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public Task
    ChildRegistration_ClosesWhileTheParentIsStillJoiningChildren(bool parentFails) => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        IOperationProgress retained = null;
        var release = Signal();
        var root = Start(@operator, (progress, token) => {
            retained = progress;
            _ = progress.Child("existing child", async (_, _) => await release.Task);
            return parentFails ? Task.FromException(new IOException("parent")) : Task.CompletedTask;
        });
        var childStarted = false;
        Exception registrationError = null;
        try {
            try { await retained.Child("late child", (_, _) => { childStarted = true; return Task.CompletedTask; }); }
            catch (Exception ex) { registrationError = ex; }
        }
        finally { release.TrySetResult(); }
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(registrationError, Is.InstanceOf<InvalidOperationException>());
            Assert.That(childStarted, Is.False);
            Assert.That(Children(root).Length, Is.EqualTo(1));
        }
    });

    /// <summary>
    /// Verifies that a canceled parent waits for a child that ignores cancellation and includes its later error.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CanceledParent_JoinsNonCooperativeChildAndIncludesItsLateError() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var delegateSettled = Signal();
        var root = Start(@operator, async (progress, token) => {
            _ = progress.Child("child", async (_, _) => {
                await release.Task;
                throw new IOException("late child failure");
            });
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            finally { delegateSettled.TrySetResult(); }
        });
        root.Cancel();
        await delegateSettled.Task.WaitAsync(Timeout);
        // Let the parent's delegate continuation reach its child join.
        await Task.Yield();
        var stateWhileJoining = (root.Complete, root.Progressing, @operator.Operations.Count);
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(stateWhileJoining.Complete, Is.False);
            Assert.That(stateWhileJoining.Progressing, Is.True);
            Assert.That(stateWhileJoining.Count, Is.EqualTo(1));
            Assert.That(root.Complete, Is.True);
            Assert.That(root.CompleteWithError, Is.True);
            Assert.That(root.Error, Is.Null);
            Assert.That(@operator.Operations.Count, Is.EqualTo(1));
        }
    });

    /// <summary>
    /// Verifies that batch construction failure preserves the parent's join of children already started.
    /// </summary>
    /// <param name="enumerationFails">
    /// Whether failure occurs during source enumeration rather than in the child factory.
    /// </param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public Task
    BatchConstructionFailure_JoinsChildrenAlreadyStarted(bool enumerationFails) => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = Start(@operator, async (progress, _) => {
            await progress.Children(enumerationFails ? FailingSource() : new[] { 1, 2 }, item => {
                if (item == 2) throw new IOException("factory failed");
                return new OperationChild("started child", async (_, _) => await release.Task);
            });
        });
        var stateWhileJoining = (root.Complete, root.Progressing, HasError: root.Error is IOException);
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(stateWhileJoining.Complete, Is.False);
            Assert.That(stateWhileJoining.Progressing, Is.True);
            Assert.That(stateWhileJoining.HasError, Is.True);
            Assert.That(root.CompleteWithError, Is.True);
            Assert.That(Children(root).Single().Complete, Is.True);
        }
    });

    private static IEnumerable<int> FailingSource() {
        yield return 1;
        throw new IOException("enumeration failed");
    }

    /// <summary>
    /// Verifies that awaiting failed child work reports completion without propagating its delegate error.
    /// </summary>
    /// <param name="batch">Whether to await a batch rather than a single child.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public Task AwaitingFailedChild_ReportsCompletionWithoutThrowing(bool batch) => UiTestThread.Run(() => {
        IOperator @operator = new Operator();
        var continued = false;
        var batchResult = false;
        var root = Start(@operator, async (progress, _) => {
            OperationDelegate failure = (_, _) => Task.FromException(new IOException("child"));
            if (batch) batchResult = await progress.Children(new[] { 1 }, _ => new OperationChild("child", failure));
            else await progress.Child("child", failure);
            continued = true;
        });
        using (Assert.EnterMultipleScope()) {
            Assert.That(continued, Is.True);
            Assert.That(batchResult, Is.EqualTo(batch));
            Assert.That(root.CompleteWithError, Is.True);
        }
        return Task.CompletedTask;
    });

    /// <summary>
    /// Verifies that a reentrant leaf update from an ancestor notification preserves committed hierarchy totals.
    /// </summary>
    /// <param name="target">Whether to report target values rather than progress.</param>
    /// <param name="add">Whether the reentrant report adds to the value rather than setting it.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public Task AncestorNotification_ReentrantLeafUpdatePreservesCommittedTotals(bool target, bool add) =>
        UiTestThread.Run(async () => {
            IOperator @operator = new Operator();
            var release = Signal();
            IOperationProgress leafProgress = null;
            var root = Start(@operator, async (progress, _) => await progress.Child("middle", async (middle, _) =>
                await middle.Child("leaf", async (leaf, _) => { leafProgress = leaf; await release.Task; })));
            var middleOperation = Children(root).Single();
            var leafOperation = Children(middleOperation).Single();
            var first = true;
            (long Root, long Middle, long Leaf) notificationValues = default;
            root.PropertyChanged += (_, e) => {
                if (!first || e.PropertyName != (target ? nameof(OperationBase.Target) :
                                                          nameof(OperationBase.Progress))) return;
                first = false;
                notificationValues = target
                    ? (root.Target, middleOperation.Target, leafOperation.Target)
                    : (root.Progress, middleOperation.Progress, leafOperation.Progress);
                Report(leafProgress, target, add, 2);
            };
            Report(leafProgress, target, false, 1);
            (long Root, long Middle, long Leaf) totals = target
                ? (root.Target, middleOperation.Target, leafOperation.Target)
                : (root.Progress, middleOperation.Progress, leafOperation.Progress);
            release.SetResult();
            await root.Completion.WaitAsync(Timeout);
            var expected = add ? 3L : 2L;
            using (Assert.EnterMultipleScope()) {
                Assert.That(notificationValues.Root, Is.EqualTo(1L));
                Assert.That(notificationValues.Middle, Is.EqualTo(1L));
                Assert.That(notificationValues.Leaf, Is.EqualTo(1L));
                Assert.That(totals.Root, Is.EqualTo(expected));
                Assert.That(totals.Middle, Is.EqualTo(expected));
                Assert.That(totals.Leaf, Is.EqualTo(expected));
            }
        });

    private static void Report(IOperationProgress progress, bool target, bool add, long value) {
        if (target) {
            if (add) progress.Change(addTarget: value);
            else progress.Change(setTarget: value);
        }
        else {
            if (add) progress.Change(addProgress: value);
            else progress.Change(setProgress: value);
        }
    }

    /// <summary>Verifies that descendant reports notify ancestors with current numeric display strings.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task DescendantUpdates_NotifyAncestorsWithCurrentNumericDisplayStrings() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        IOperationProgress childProgress = null;
        var root = Start(@operator, async (progress, _) => await progress.Child("child", async (child, _) => {
            childProgress = child;
            await release.Task;
        }));
        var values = new List<(string Property, string Value)>();
        root.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(OperationBase.ProgressString))
                values.Add((e.PropertyName, root.ProgressString));
            if (e.PropertyName == nameof(OperationBase.TargetString))
                values.Add((e.PropertyName, root.TargetString));
        };
        childProgress.Change(setProgress: 2, setTarget: 7);
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(values.Select(value => value.Property),
                        Is.EquivalentTo(new[] { nameof(OperationBase.TargetString), nameof(OperationBase.ProgressString) }));
            Assert.That(values.Where(value => value.Property == nameof(OperationBase.TargetString)).Select(value => value.Value),
                        Is.EqualTo(new[] { "7" }));
            Assert.That(values.Where(value => value.Property == nameof(OperationBase.ProgressString)).Select(value => value.Value),
                        Is.EqualTo(new[] { "2" }));
        }
    });

    /// <summary>
    /// Verifies that throwing child token callbacks do not prevent exactly one cancellation of siblings and the parent.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Cancel_ThrowingChildCallbacksStillCancelSiblingsAndParentExactlyOnce() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var calls = new int[3];
        var root = Start(@operator, async (progress, token) => {
            using var registration = token.Register(() => calls[0]++);
            _ = progress.Child("throwing child", async (_, childToken) => {
                using var childRegistration = childToken.Register(() => {
                    calls[1]++; throw new IOException("callback");
                });
                await release.Task;
            });
            _ = progress.Child("sibling", async (_, childToken) => {
                using var childRegistration = childToken.Register(() => calls[2]++);
                await release.Task;
            });
            await release.Task;
        });
        AggregateException failure = null;
        try { root.Cancel(); }
        catch (AggregateException ex) { failure = ex; }
        root.Cancel();
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(calls[0], Is.EqualTo(1));
            Assert.That(calls[1], Is.EqualTo(1));
            Assert.That(calls[2], Is.EqualTo(1));
            Assert.That(failure?.InnerExceptions.Single(), Is.InstanceOf<IOException>());
            Assert.That(root.CompleteWithError, Is.False);
        }
    });

    /// <summary>Verifies that removing one failed root preserves collection relevance while another remains.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Relevance_RemovingOneOfTwoFailuresKeepsTheCollectionRelevant() => UiTestThread.Run(() => {
        IOperator @operator = new Operator();
        var first = Start(@operator, (_, _) => Task.FromException(new IOException("first")));
        var second = Start(@operator, (_, _) => Task.FromException(new IOException("second")));
        @operator.Operations.Remove(first);
        var relevantWithOneRemaining = ((OperationCollection)@operator.Operations).Relevant;
        @operator.Operations.Remove(second);
        using (Assert.EnterMultipleScope()) {
            Assert.That(relevantWithOneRemaining, Is.True);
            Assert.That(((OperationCollection)@operator.Operations).Relevant, Is.False);
        }
        return Task.CompletedTask;
    });

    /// <summary>Verifies that cancellation between child registration and startup skips the child's delegate.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CancellationBetweenChildRegistrationAndStart_SkipsItsDelegate() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        IOperationProgress retained = null;
        var root = Start(@operator, async (progress, _) => { retained = progress; await release.Task; });
        var source = (INotifyCollectionChanged)root.ChildSource;
        source.CollectionChanged += (_, _) => root.Cancel();
        var childRan = false;
        await retained.Child("child", (_, _) => { childRan = true; return Task.CompletedTask; });
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(childRan, Is.False);
            Assert.That(Children(root).Single().Complete, Is.True);
            Assert.That(root.CompleteWithError, Is.False);
        }
    });

    /// <summary>Verifies that timed relevance of a numeric descendant exposes its entire ancestor path.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task SlowNumericDescendant_MakesTheEntireAncestorPathRelevant() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var relevant = Signal();
        var root = Start(@operator, async (progress, _) => await progress.Child("middle", async (middle, _) =>
            await middle.Child("leaf", async (leaf, _) => { leaf.Change(setTarget: 1); await release.Task; })));
        root.RelevantChanged += (_, _) => { if (root.Relevant) relevant.TrySetResult(); };
        (bool Root, bool Middle, bool Leaf, bool Collection) state;
        try {
            await relevant.Task.WaitAsync(Timeout);
            var middle = Children(root).Single();
            state = (root.Relevant, middle.Relevant, Children(middle).Single().Relevant,
                ((OperationCollection)@operator.Operations).Relevant);
        }
        finally { release.TrySetResult(); }
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(state.Root, Is.True);
            Assert.That(state.Middle, Is.True);
            Assert.That(state.Leaf, Is.True);
            Assert.That(state.Collection, Is.True);
        }
    });

    /// <summary>Verifies that numeric reports apply absolute values before additions.</summary>
    /// <param name="progress">The absolute progress value.</param>
    /// <param name="addProgress">The amount to add to progress.</param>
    /// <param name="target">The absolute target value.</param>
    /// <param name="addTarget">The amount to add to the target.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(0L, 0L, 0L, 0L)]
    [TestCase(2L, 3L, 4L, 5L)]
    [TestCase(10L, -2L, 20L, -4L)]
    public Task
    Change_SetsValuesBeforeApplyingAdditions(long progress, long addProgress, long target, long addTarget) =>
        UiTestThread.Run(() => {
            IOperator @operator = new Operator();
            var root = Start(@operator, (report, _) => {
                report.Change(setProgress: progress, addProgress: addProgress,
                              setTarget: target, addTarget: addTarget);
                return Task.CompletedTask;
            });
            using (Assert.EnterMultipleScope()) {
                Assert.That(root.Progress, Is.EqualTo(progress + addProgress));
                Assert.That(root.Target, Is.EqualTo(target + addTarget));
            }
            return Task.CompletedTask;
        });

    /// <summary>Verifies that the public collection source prevents external mutation.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CollectionSource_DoesNotPermitMutationOutsideTheOperationCollection() => UiTestThread.Run(() => {
        IOperator @operator = new Operator();
        Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
        Assert.That(@operator.Operations.Source is not IList list || list.IsReadOnly, Is.True);
        return Task.CompletedTask;
    });

    /// <summary>Verifies that a reentrant cancellation notification cannot register new child work.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Cancel_ReentrantNotificationCannotStartNewChildWork() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        IOperationProgress retained = null;
        var root = Start(@operator, async (progress, _) => { retained = progress; await release.Task; });
        var childRan = false;
        Task registration = null;
        root.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(OperationBase.Canceling)) {
                registration = retained.Child("late child", (_, _) => { childRan = true; return Task.CompletedTask; });
            }
        };
        root.Cancel();
        Exception error = null;
        try { await registration; }
        catch (Exception ex) { error = ex; }
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(childRan, Is.False);
            Assert.That(error, Is.InstanceOf<OperationCanceledException>());
        }
    });

    /// <summary>Verifies that reentrant cancellation and token callback failures still cancel every operation exactly once.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Cancel_ReentrantNotificationAndTokenFailureStillCancelEveryOperationOnce() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var calls = new int[3];
        var root = Start(@operator, async (progress, token) => {
            using var registration = token.Register(() => calls[0]++);
            _ = progress.Child("throwing child", async (_, childToken) => {
                using var childRegistration = childToken.Register(() => {
                    calls[1]++;
                    throw new IOException("child token");
                });
                await release.Task;
            });
            _ = progress.Child("sibling", async (_, childToken) => {
                using var childRegistration = childToken.Register(() => calls[2]++);
                await release.Task;
            });
            await release.Task;
        });
        PropertyChangedEventHandler parentHandler = (_, e) => {
            if (e.PropertyName == nameof(OperationBase.Canceling)) {
                root.Cancel();
            }
        };
        root.PropertyChanged += parentHandler;
        AggregateException failure = null;
        try {
            try { root.Cancel(); }
            catch (AggregateException ex) { failure = ex; }
            root.Cancel();
        }
        finally {
            root.PropertyChanged -= parentHandler;
            release.TrySetResult();
        }
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(calls, Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(failure?.InnerExceptions.Select(ex => ex.Message),
                        Is.EquivalentTo(new[] { "child token" }));
        }
    });

    /// <summary>Verifies that removal from the completion capability notification does not break subsequent removal requests.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Remove_DuringCompletionNotificationDoesNotPreventLaterRemoval() => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = Start(@operator, async (_, _) => { await release.Task; throw new IOException("failure"); });
        root.CanRemoveChanged += (_, _) => { if (root.CanRemove) root.Remove(); };
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        root.Remove();
        Assert.That(@operator.Operations.Count, Is.Zero);
    });

    /// <summary>Verifies that a rejected early removal request preserves later command removal.</summary>
    /// <param name="duringFinalization">Whether to make the early removal request from a finalization notification.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public Task Remove_RejectedRequestKeepsTheHandlerForRemovalAfterCompletion(bool duringFinalization) => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = Start(@operator, async (_, _) => { await release.Task; throw new IOException("failure"); });
        var eligibleBefore = true;
        var countAfterRejectedRequest = -1;
        void RequestEarlyRemoval() {
            eligibleBefore = root.CanRemove;
            root.Remove();
            countAfterRejectedRequest = @operator.Operations.Count;
        }
        if (duringFinalization) {
            root.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(OperationBase.Progressing) && !root.Progressing) RequestEarlyRemoval();
            };
        }
        else RequestEarlyRemoval();
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        var eligibleAfter = root.CanRemove;
        root.Remove();
        using (Assert.EnterMultipleScope()) {
            Assert.That(eligibleBefore, Is.False);
            Assert.That(countAfterRejectedRequest, Is.EqualTo(1));
            Assert.That(eligibleAfter, Is.True);
            Assert.That(@operator.Operations.Count, Is.EqualTo(0));
        }
    });

    /// <summary>Verifies that removal eligibility notifications are published only once the operation is complete.</summary>
    /// <param name="fails">Whether the delegate fails rather than completing successfully.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public Task RemovalCapability_NotifiesOnlyWhenComplete(bool fails) => UiTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = Start(@operator, async (_, _) => {
            await release.Task;
            if (fails) throw new IOException("failure");
        });
        var notifications = new List<(string Kind, bool Complete, bool CanRemove)>();
        root.CanRemoveChanged += (_, _) => notifications.Add(("Event", root.Complete, root.CanRemove));
        root.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(Operation.CanRemove)) notifications.Add(("Property", root.Complete, root.CanRemove));
        };
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(notifications.Select(notification => notification.Kind), Is.EquivalentTo(new[] { "Event", "Property" }));
            Assert.That(notifications.Select(notification => notification.Complete), Is.All.True);
            Assert.That(notifications.Select(notification => notification.CanRemove), Is.All.True);
        }
    });
}

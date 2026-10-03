using System;
using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Brows.Operations;

/// <summary>Tests error publication and propagation of completion-listener failures.</summary>
[TestFixture]
public sealed class NotificationFailureTests {
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
                registration = retained.Child("late", (_, _) => { ran = true; return Task.CompletedTask; });
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
}

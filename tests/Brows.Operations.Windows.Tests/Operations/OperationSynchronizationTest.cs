using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Brows.Operations;

/// <summary>
/// Tests operation notification synchronization with a WPF dispatcher.
/// </summary>
[TestFixture]
public sealed class OperationSynchronizationTest {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Verifies worker notifications permit synchronous dispatcher reads of operation and collection state.
    /// </summary>
    /// <param name="notification">
    /// The property or event notification to observe.
    /// </param>
    /// <returns>
    /// A task representing execution of the test.
    /// </returns>
    [TestCase("Name")]
    [TestCase("Data")]
    [TestCase("Progress")]
    [TestCase("Target")]
    [TestCase("ProgressString")]
    [TestCase("TargetString")]
    [TestCase("Canceling")]
    [TestCase("Progressing")]
    [TestCase("Complete")]
    [TestCase("CompleteWithError")]
    [TestCase("Error")]
    [TestCase("Relevant")]
    [TestCase("CanCancel")]
    [TestCase("CanRemove")]
    [TestCase("ProgressChanged")]
    [TestCase("TargetChanged")]
    [TestCase("RelevantChanged")]
    [TestCase("CanCancelChanged")]
    [TestCase("CanRemoveChanged")]
    [TestCase("CollectionCount")]
    [TestCase("CollectionRelevant")]
    public Task WorkerNotification_AllowsSynchronousDispatcherStateRead(string notification) =>
        WpfTestThread.Run(async () => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            IOperator @operator = new Operator();
            var collection = (OperationCollection)@operator.Operations;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Operation root = null;
            IOperationProgress progress = null;
            var fails = notification is "CompleteWithError" or "Error" or "Relevant" or
                "RelevantChanged" or "CollectionRelevant";
            await Task.Run(() => new OperationManager(collection).Operate("root", async (reporter, _) => {
                progress = reporter;
                await release.Task.ConfigureAwait(false);
                if (fails) {
                    throw new IOException("failure");
                }
            }, operation => root = operation)).WaitAsync(Timeout);

            var calls = 0;
            var onWorker = false;
            var readEntered = false;
            var readFinished = false;
            void ReadOnDispatcher() {
                if (Interlocked.Increment(ref calls) != 1) {
                    return;
                }
                onWorker = !dispatcher.CheckAccess();
                var read = dispatcher.InvokeAsync(() => {
                    readEntered = true;
                    _ = ((IOperation)root).Complete;
                    _ = @operator.Operations.Count;
                    _ = collection.Relevant;
                });
                /*
                 * Bound the wait so a regression releases the gate and cannot strand the dispatcher.
                 */
                readFinished = read.Task.Wait(TimeSpan.FromSeconds(1));
            }

            switch (notification) {
                case "ProgressChanged": root.ProgressChanged += (_, _) => ReadOnDispatcher(); break;
                case "TargetChanged": root.TargetChanged += (_, _) => ReadOnDispatcher(); break;
                case "RelevantChanged": root.RelevantChanged += (_, _) => ReadOnDispatcher(); break;
                case "CanCancelChanged": root.CanCancelChanged += (_, _) => ReadOnDispatcher(); break;
                case "CanRemoveChanged": root.CanRemoveChanged += (_, _) => ReadOnDispatcher(); break;
                case "CollectionCount":
                case "CollectionRelevant":
                    ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => {
                        if (e.PropertyName == notification.Substring("Collection".Length)) {
                            ReadOnDispatcher();
                        }
                    };
                    break;
                default:
                    root.PropertyChanged += (_, e) => {
                        if (e.PropertyName == notification) {
                            ReadOnDispatcher();
                        }
                    };
                    break;
            }

            try {
                if (notification is "Canceling" or "CanCancel" or "CanCancelChanged") {
                    await Task.Run(root.Cancel).WaitAsync(Timeout);
                }
                else if (notification is "Name" or "Data" or "Progress" or "Target" or "ProgressString" or
                         "TargetString" or "ProgressChanged" or "TargetChanged") {
                    await Task.Run(() => progress.Change(setProgress: 1, setTarget: 2, name: "updated", data: "details",
                        progressString: "one", targetString: "two")).WaitAsync(Timeout);
                }
            }
            finally {
                release.TrySetResult();
                await root.Completion.WaitAsync(Timeout);
            }

            using (Assert.EnterMultipleScope()) {
                Assert.That(calls, Is.GreaterThanOrEqualTo(1));
                Assert.That(onWorker, Is.True);
                Assert.That(readEntered, Is.True);
                Assert.That(readFinished, Is.True,
                    "The dispatcher getter waited for the notifying worker's state lock.");
            }
        });
}

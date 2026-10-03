using Brows.Composition;
using Brows.Operations;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Brows;

/// <summary>Demonstrates hierarchical work, progress reporting, cancellation, and failures through the operation APIs.</summary>
sealed partial class OperationsSampleWindow : IExport {
    private IOperator _operator;

    [ImportRequired]
    internal IOperatorFactory OperatorFactory { get; set; }

    /// <summary>Initializes the demonstration window and its controls.</summary>
    public OperationsSampleWindow() {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) {
        if (_operator is not null) {
            return;
        }
        _operator = OperatorFactory?.Create()
            ?? throw new InvalidOperationException("The operation factory import was not supplied.");
        OperationView.Operator = _operator;
        ScenarioPanel.IsEnabled = true;
        ShowcaseButton.IsEnabled = true;
        StatusText.Text = "Choose a scenario to start. You can run several at once.";
    }

    private void RunShowcase_Click(object sender, RoutedEventArgs e) {
        StartOperation("Nested workflow", NestedWorkflow);
        StartOperation("Cancellable task", CancellableWork);
        StartOperation("Child error", ChildFailure);
        StatusText.Text = "Showcase started. Try cancelling a task or removing the failed operation.";
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e) {
        if (_operator is null) {
            return;
        }
        var removed = _operator.Operations.RemoveComplete();
        StatusText.Text = removed == 0
            ? "No finished operations to clear yet."
            : $"Cleared {removed} finished operation{(removed == 1 ? "" : "s")}.";
    }

    private void RunNested_Click(object sender, RoutedEventArgs e) {
        StartOperation("Nested workflow", NestedWorkflow);
    }

    private void RunBatch_Click(object sender, RoutedEventArgs e) {
        StartOperation("Parallel batch", ParallelBatch);
    }

    private void RunProgressUpdates_Click(object sender, RoutedEventArgs e) {
        StartOperation("Set/add progress and custom labels", ProgressUpdates);
    }

    private void RunCancellable_Click(object sender, RoutedEventArgs e) {
        StartOperation("Cancellable task", CancellableWork);
    }

    private void RunChildFailure_Click(object sender, RoutedEventArgs e) {
        StartOperation("Child error", ChildFailure);
    }

    private void RunParentFailure_Click(object sender, RoutedEventArgs e) {
        StartOperation("Parent error while child runs", ParentFailure);
    }

    private void RunSuccess_Click(object sender, RoutedEventArgs e) {
        StartOperation("Successful task", SuccessfulWork);
    }

    private void StartOperation(string name, OperationDelegate task) {
        if (_operator is null) {
            StatusText.Text = "The operation factory has not been initialized.";
            return;
        }
        try {
            _operator.Operate(name, task);
            StatusText.Text = $"Started: {name}";
        }
        catch (Exception ex) {
            StatusText.Text = $"Could not start {name}: {ex.Message}";
        }
    }

    private static async Task NestedWorkflow(IOperationProgress progress, CancellationToken token) {
        progress.Change(data: "Two folders, with two files in each folder");
        for (var folder = 1; folder <= 2; folder++) {
            var folderNumber = folder;
            await progress.Child($"Folder {folderNumber}", async (folderProgress, folderToken) => {
                folderProgress.Change(data: "Processing files sequentially");
                for (var file = 1; file <= 2; file++) {
                    var fileNumber = file;
                    await folderProgress.Child($"File {fileNumber}", (fileProgress, fileToken) =>
                        RunSteps(fileProgress,
                                 fileToken,
                                 name: $"Copy file {fileNumber}",
                                 steps: 8,
                                 delay: TimeSpan.FromMilliseconds(450),
                                 action: "Copied"));
                }
            });
        }
    }

    private static async Task ParallelBatch(IOperationProgress progress, CancellationToken token) {
        progress.Change(data: "Four child operations report from thread-pool workers");
        await progress.Children(
            Enumerable.Range(1, 4),
            item => new OperationChild(
                $"Batch item {item}",
                (childProgress, childToken) => Task.Run(
                    () => RunSteps(childProgress,
                                   childToken,
                                   name: $"Process item {item}",
                                   steps: 10,
                                   delay: TimeSpan.FromMilliseconds(400 + item * 35),
                                   action: "Processed"),
                    childToken)));
    }

    private static async Task ProgressUpdates(IOperationProgress progress, CancellationToken token) {
        progress.Change(setProgress: 1,
                        setTarget: 4,
                        progressString: "1 unit",
                        targetString: "4 units",
                        data: "Set values and explicit display strings");
        await Task.Delay(1100, token);

        progress.Change(addProgress: 1,
                        addTarget: 2,
                        progressString: "2 units",
                        targetString: "6 units",
                        data: "Added 1 progress and 2 target");
        await Task.Delay(1100, token);

        progress.Change(setProgress: 5,
                        setTarget: 8,
                        progressString: "5 units",
                        targetString: "8 units",
                        data: "Set progress and target again");
        await Task.Delay(1100, token);

        progress.Change(addProgress: 2,
                        addTarget: 2,
                        progressString: "7 units",
                        targetString: "10 units",
                        data: "Added progress and target");
        await Task.Delay(1100, token);
    }

    private static async Task CancellableWork(IOperationProgress progress, CancellationToken token) {
        const int steps = 28;
        progress.Change(setTarget: steps, data: "Use Cancel in this operation's row");
        for (var step = 1; step <= steps; step++) {
            await Task.Delay(450, token);
            progress.Change(addProgress: 1, data: $"Waiting step {step} of {steps}");
        }
    }

    private static async Task ChildFailure(IOperationProgress progress, CancellationToken token) {
        progress.Change(data: "The child error should remain visible in the hierarchy");
        await progress.Child("Failing child", async (childProgress, childToken) => {
            await RunSteps(childProgress,
                           childToken,
                           name: "Work before failure",
                           steps: 4,
                           delay: TimeSpan.FromMilliseconds(400),
                           action: "Completed");
            throw new InvalidOperationException("This child failed on purpose.");
        });
    }

    private static async Task ParentFailure(IOperationProgress progress, CancellationToken token) {
        progress.Change(data: "The parent fails while this child is still working");
        _ = progress.Child("Finishing child", (childProgress, childToken) =>
            RunSteps(childProgress,
                     childToken,
                     name: "Cleanup work",
                     steps: 9,
                     delay: TimeSpan.FromMilliseconds(450),
                     action: "Finished"));
        await Task.Delay(500, token);
        throw new InvalidOperationException("The parent failed while its child was still running.");
    }

    private static async Task SuccessfulWork(IOperationProgress progress, CancellationToken token) {
        await RunSteps(progress,
                       token,
                       name: "Successful work",
                       steps: 8,
                       delay: TimeSpan.FromMilliseconds(450),
                       action: "Completed");
    }

    private static async Task RunSteps(IOperationProgress progress,
                                       CancellationToken token,
                                       string name,
                                       int steps,
                                       TimeSpan delay,
                                       string action) {
        progress.Change(setTarget: steps, name: name, data: "Starting");
        for (var step = 1; step <= steps; step++) {
            await Task.Delay(delay, token);
            progress.Change(addProgress: 1, data: $"{action} {step} of {steps}");
        }
    }
}

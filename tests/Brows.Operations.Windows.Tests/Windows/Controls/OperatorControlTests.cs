using Brows.Operations;
using Brows.Windows.Data;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Brows.Windows.Controls;

/// <summary>Tests WPF bindings, operation row presentation, and cancellation and removal commands on a dispatcher.</summary>
[TestFixture]
public sealed class OperatorControlTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Operation Start(IOperator @operator, OperationDelegate task) {
        Operation operation = null;
        new OperationManager((OperationCollection)@operator.Operations)
            .Operate("root", task, started => operation = started);
        return operation;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task Layout(OperatorControl control) {
        control.ApplyTemplate();
        control.Measure(new Size(1200, 600));
        control.Arrange(new Rect(0, 0, 1200, 600));
        control.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        control.UpdateLayout();
    }

    private static IValueConverter Converter() => new OperationCommandConverter();
    private static ICommand Command(Operation operation, string name) =>
        (ICommand)Converter().Convert(operation, typeof(ICommand), name, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Verifies bound child additions and root removals use the WPF dispatcher from worker tasks.</summary>
    /// <param name="fails">Whether to retain a child failure and remove its root explicitly.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public Task WorkerCollections_UpdateDispatcherBindings(bool fails) => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var release = Signal();
        var mutationsOnDispatcher = new List<bool>();
        ((INotifyCollectionChanged)((OperationCollection)@operator.Operations).Source).CollectionChanged += (_, _) =>
            mutationsOnDispatcher.Add(dispatcher.CheckAccess());
        var root = Start(@operator, async (progress, _) => {
            await release.Task.ConfigureAwait(false);
            await progress.Child("worker child", (childProgress, _) => {
                childProgress.Change(setTarget: 1, setProgress: 1, data: "worker details");
                return fails ? Task.FromException(new IOException("worker failure")) : Task.CompletedTask;
            }).ConfigureAwait(false);
        });
        ((INotifyCollectionChanged)root.ChildSource).CollectionChanged += (_, _) =>
            mutationsOnDispatcher.Add(dispatcher.CheckAccess());
        var rootItems = new ItemsControl { ItemsSource = ((OperationCollection)@operator.Operations).Source };
        var childItems = new ItemsControl { ItemsSource = (IEnumerable)root.ChildSource };
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        Assert.That(rootItems.Items.Count, Is.EqualTo(1));
        Assert.That(childItems.Items.Count, Is.Zero);
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        await Layout(control);
        if (fails) {
            var child = ((IEnumerable)root.ChildSource).Cast<OperationBase>().Single();
            using (Assert.EnterMultipleScope()) {
                Assert.That(child.Error.Message, Is.EqualTo("worker failure"));
                Assert.That(Descendants<ItemsControl>(control).Any(items => items.Items.Contains(child)), Is.True);
                Assert.That(Descendants<TextBlock>(control).Any(text => text.Text == "worker failure"), Is.True);
            }
            var removed = await Task.Run(() => @operator.Operations.Remove(root)).WaitAsync(Timeout);
            Assert.That(removed, Is.True);
            await Layout(control);
        }
        using (Assert.EnterMultipleScope()) {
            Assert.That(mutationsOnDispatcher, Is.EqualTo(new[] { true, true, true }));
            Assert.That(root.CompleteWithError, Is.EqualTo(fails));
            Assert.That(@operator.Operations.Count, Is.Zero);
            Assert.That(rootItems.Items.Count, Is.Zero);
            Assert.That(childItems.Items.Count, Is.EqualTo(1));
        }
    });

    /// <summary>Verifies that the command converter returns an unset value for a missing or unsupported operation.</summary>
    /// <param name="value">The missing or unsupported operation value to convert.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(null)]
    [TestCase("unsupported")]
    public Task Converter_MissingOperationReturnsUnsetValue(object value) => WpfTestThread.Run(() => {
        Assert.That(Converter().Convert(value, typeof(ICommand), "Cancel", System.Globalization.CultureInfo.InvariantCulture),
                    Is.SameAs(DependencyProperty.UnsetValue));
        return Task.CompletedTask;
    });

    /// <summary>Verifies that the command converter rejects unsupported command names.</summary>
    /// <param name="parameter">The unsupported command parameter.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(null)]
    [TestCase("invalid")]
    public Task Converter_RejectsInvalidCommandParameters(string parameter) => WpfTestThread.Run(() => {
        IOperator @operator = new Operator();
        var root = Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
        Assert.That(() => Command(root, parameter), Throws.ArgumentException);
        return Task.CompletedTask;
    });

    /// <summary>Verifies that the command converter does not support reverse conversion.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Converter_ConvertBackIsUnsupported() => WpfTestThread.Run(() => {
        Assert.That(() => Converter().ConvertBack(null, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture),
                    Throws.TypeOf<NotSupportedException>());
        return Task.CompletedTask;
    });

    /// <summary>Verifies that the root items binding is active and displays a failed operation.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task RootItemsBinding_IsActiveAndShowsFailedOperation() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var root = Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        var items = Descendants<ItemsControl>(control).First();
        var binding = BindingOperations.GetBindingExpression(items, ItemsControl.ItemsSourceProperty);
        using (Assert.EnterMultipleScope()) {
            Assert.That(binding.Status, Is.EqualTo(BindingStatus.Active));
            Assert.That(items.Items.Count, Is.EqualTo(1));
            Assert.That(items.Items[0], Is.SameAs(root));
            Assert.That(Descendants<Grid>(control).First().Visibility, Is.EqualTo(Visibility.Visible));
        }
    });

    /// <summary>Verifies that an operator with no operations collapses the display panel.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task EmptyOperator_CollapsesThePanel() => WpfTestThread.Run(async () => {
        var control = new OperatorControl { Operator = new Operator() };
        await Layout(control);
        Assert.That(Descendants<Grid>(control).First().Visibility, Is.EqualTo(Visibility.Collapsed));
    });

    /// <summary>Verifies that replacing the operator updates items and clearing it collapses the panel.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task OperatorReplacement_UpdatesItemsAndClearingCollapsesPanel() => WpfTestThread.Run(async () => {
        IOperator first = new Operator();
        IOperator second = new Operator();
        Start(first, (_, _) => Task.FromException(new IOException("first")));
        var secondRoot = Start(second, (_, _) => Task.FromException(new IOException("second")));
        var control = new OperatorControl { Operator = first };
        await Layout(control);
        control.Operator = second;
        await Layout(control);
        var item = Descendants<ItemsControl>(control).First().Items[0];
        control.Operator = null;
        await Layout(control);
        using (Assert.EnterMultipleScope()) {
            Assert.That(item, Is.SameAs(secondRoot));
            Assert.That(Descendants<ItemsControl>(control).First().Items.Count, Is.EqualTo(0));
            Assert.That(Descendants<Grid>(control).First().Visibility, Is.EqualTo(Visibility.Collapsed));
        }
    });

    /// <summary>Verifies that a row with its own error displays the message and hides progress.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task Failure_ShowsErrorMessageAndHidesProgress() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var root = Start(@operator, (_, _) => Task.FromException(new IOException("expected message")));
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        var row = Descendants<Grid>(control).Single(grid => grid.ColumnDefinitions.Count == 7 && ReferenceEquals(grid.DataContext, root));
        var error = row.Children.OfType<Control>().Single(item => Grid.GetColumn(item) == 5 &&
            item.Template?.LoadContent() is ContentPresenter);
        var progress = row.Children.OfType<Control>().Single(item => Grid.GetColumn(item) == 5 &&
            item.Template?.LoadContent() is ProgressBar);
        var errorText = Descendants<TextBlock>(error).Single().Text;
        using (Assert.EnterMultipleScope()) {
            Assert.That(error.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(progress.Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(errorText, Is.EqualTo("expected message"));
        }
    });

    /// <summary>Verifies that a relevant descendant makes each row in its ancestor path visible.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task RelevantDescendant_ShowsEveryAncestorRow() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        Start(@operator, async (root, _) => await root.Child("middle", async (middle, _) =>
            await middle.Child("leaf", (_, _) => Task.FromException(new IOException("leaf failed")))));
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        var rows = Descendants<Grid>(control).Where(grid => grid.ColumnDefinitions.Count == 7 && grid.DataContext is OperationBase);
        using (Assert.EnterMultipleScope()) {
            Assert.That(rows.Select(row => ((OperationBase)row.DataContext).Name), Is.EqualTo(new[] { "root", "middle", "leaf" }));
            Assert.That(rows.Select(row => row.Visibility), Is.All.EqualTo(Visibility.Visible));
        }
    });

    /// <summary>Verifies that descendant numeric reports refresh ancestor progress and target bindings.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task NumericDescendantUpdates_RefreshAncestorTextBindings() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        IOperationProgress child = null;
        var root = Start(@operator, async (progress, token) => {
            _ = progress.Child("child", async (progress, _) => { child = progress; await release.Task; });
            throw new IOException("keep parent visible");
        });
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        var childOperation = ((IEnumerable)root.ChildSource).Cast<OperationBase>().Single();
        var relevant = Signal();
        childOperation.RelevantChanged += (_, _) => { if (childOperation.Relevant) relevant.TrySetResult(); };
        child.Change(setProgress: 3, setTarget: 8);
        await relevant.Task.WaitAsync(Timeout);
        await Layout(control);
        var row = Descendants<Grid>(control).Single(grid => grid.ColumnDefinitions.Count == 7 && ReferenceEquals(grid.DataContext, root));
        var texts = row.Children.OfType<ContentControl>()
            .Where(item => Grid.GetColumn(item) is 3 or 4)
            .Select(item => item.Content?.ToString())
            .ToArray();
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        Assert.That(texts, Is.EqualTo(new[] { "3", "8" }));
    });

    /// <summary>Verifies that cancellation updates command eligibility and raises its change event.</summary>
    /// <param name="parameter">The command name, including its tested casing.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase("cancel")]
    [TestCase("CANCEL")]
    public Task CancelCommand_UpdatesEligibilityAndRaisesCanExecuteChanged(string parameter) => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = Start(@operator, async (_, _) => await release.Task);
        var command = Command(root, parameter);
        var before = command.CanExecute(null);
        var changed = 0;
        command.CanExecuteChanged += (_, _) => changed++;
        command.Execute(null);
        var afterCancel = command.CanExecute(null);
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(before, Is.True);
            Assert.That(afterCancel, Is.False);
            Assert.That(changed > 0, Is.True);
        }
    });

    /// <summary>Verifies worker state changes update bound buttons on the command's creating dispatcher.</summary>
    /// <param name="name">The command whose eligibility changes on a worker.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase("Cancel")]
    [TestCase("Remove")]
    public Task WorkerCommandChanges_UpdateButtonsOnCreatingDispatcher(string name) => WpfTestThread.Run(async () => {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var context = SynchronizationContext.Current;
        var release = Signal();
        var delivered = Signal();
        var cancel = name == "Cancel";
        var root = cancel
            ? Start(new Operator(), async (_, _) => await release.Task)
            : new Operation("worker completion", (_, _) => Task.CompletedTask);
        var command = Command(root, name);
        var button = new Button();
        BindingOperations.SetBinding(button, Button.CommandProperty, new Binding { Source = command });
        var notificationsOnDispatcher = new List<bool>();
        var propertyChangesOnDispatcher = new List<bool>();
        command.CanExecuteChanged += (_, _) => {
            notificationsOnDispatcher.Add(dispatcher.CheckAccess());
            delivered.TrySetResult();
        };
        root.PropertyChanged += (_, e) => {
            if (e.PropertyName == (cancel ? nameof(Operation.Canceling) : nameof(Operation.Complete))) {
                propertyChangesOnDispatcher.Add(dispatcher.CheckAccess());
            }
        };
        try {
            Assert.That(button.IsEnabled, Is.EqualTo(cancel));
            await Task.Run(() => {
                if (cancel) {
                    root.Cancel();
                }
                else {
                    // Synchronous completion stays on this worker. Route any unexpected
                    // async-void observer failure to the test dispatcher instead of the process.
                    var previous = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(context);
                    try { root.Start(); }
                    finally { SynchronizationContext.SetSynchronizationContext(previous); }
                }
            }).WaitAsync(Timeout);
            if (!cancel) {
                await root.Completion.WaitAsync(Timeout);
            }
            await delivered.Task.WaitAsync(Timeout);
            using (Assert.EnterMultipleScope()) {
                Assert.That(notificationsOnDispatcher, Is.EqualTo(new[] { true }));
                Assert.That(propertyChangesOnDispatcher, Is.EqualTo(new[] { false }));
                Assert.That(command.CanExecute(null), Is.EqualTo(!cancel));
                Assert.That(button.IsEnabled, Is.EqualTo(!cancel));
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>Verifies that the Cancel command contains token callback failures while still canceling work.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CancelCommand_ContainsTokenCallbackFailuresAndStillCancelsWork() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var root = Start(@operator, async (_, token) => {
            using var registration = token.Register(() => throw new IOException("callback"));
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        });
        var command = Command(root, "Cancel");
        Exception escaped = null;
        try { command.Execute(null); }
        catch (Exception ex) { escaped = ex; }
        await root.Completion.WaitAsync(Timeout);
        using (Assert.EnterMultipleScope()) {
            Assert.That(escaped, Is.Null);
            Assert.That(root.Canceling, Is.True);
            Assert.That(root.Complete, Is.True);
            Assert.That(root.CompleteWithError, Is.False);
        }
    });

    /// <summary>Verifies that the Remove command removes a completed failed operation.</summary>
    /// <param name="parameter">The command name, including its tested casing.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase("remove")]
    [TestCase("REMOVE")]
    public Task RemoveCommand_RemovesCompletedFailure(string parameter) => WpfTestThread.Run(() => {
        IOperator @operator = new Operator();
        var root = Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
        var command = Command(root, parameter);
        var enabled = command.CanExecute(null);
        command.Execute(null);
        using (Assert.EnterMultipleScope()) {
            Assert.That(enabled, Is.True);
            Assert.That(@operator.Operations.Count, Is.EqualTo(0));
        }
        return Task.CompletedTask;
    });

    /// <summary>Verifies that removal becomes eligible at completion despite a rejected early command execution.</summary>
    /// <param name="executeFromNotification">Whether to execute removal reentrantly from the command eligibility event.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public Task RemoveCommand_EnablesAtCompletionAndSurvivesRejectedExecution(bool executeFromNotification) => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = Start(@operator, async (_, _) => { await release.Task; throw new IOException("failure"); });
        var command = Command(root, "Remove");
        var states = new List<(bool Complete, bool Enabled, int Count)> {
            (root.Complete, command.CanExecute(null), @operator.Operations.Count)
        };
        command.Execute(null);
        command.CanExecuteChanged += (_, _) => {
            states.Add((root.Complete, command.CanExecute(null), @operator.Operations.Count));
            if (executeFromNotification && command.CanExecute(null)) command.Execute(null);
        };
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        command.Execute(null);
        states.Add((root.Complete, command.CanExecute(null), @operator.Operations.Count));
        using (Assert.EnterMultipleScope()) {
            Assert.That(states.Select(state => state.Complete), Is.EqualTo([false, true, true]));
            Assert.That(states.Select(state => state.Enabled), Is.EqualTo([false, true, true]));
            Assert.That(states.Select(state => state.Count), Is.EqualTo([1, 1, 0]));
        }
    });

    /// <summary>Verifies that each failed root has a Remove button bound to its own operation.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task MultipleFailedRoots_EachHaveTheirOwnRemoveButton() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        Start(@operator, (_, _) => Task.FromException(new IOException("first")));
        Start(@operator, (_, _) => Task.FromException(new IOException("second")));
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        var buttons = Descendants<Button>(control).Where(button => Equals(button.Content, "Remove")).ToArray();
        Assert.That(buttons.Select(button => button.DataContext), Is.EquivalentTo(@operator.Operations.Snapshot()));
    });

    /// <summary>Verifies that a parent with only a descendant error retains progress presentation and hides its own error message.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ParentWithOnlyChildError_ShowsItsProgressAndHidesItsOwnErrorMessage() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var root = Start(@operator, async (progress, _) => await progress.Child("child", (_, _) => Task.FromException(new IOException("child"))));
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        var row = Descendants<Grid>(control).Single(grid => grid.ColumnDefinitions.Count == 7 && ReferenceEquals(grid.DataContext, root));
        var error = row.Children.OfType<Control>().Single(item => Grid.GetColumn(item) == 5 &&
            item.Template?.LoadContent() is ContentPresenter);
        var progress = row.Children.OfType<Control>().Single(item => Grid.GetColumn(item) == 5 &&
            item.Template?.LoadContent() is ProgressBar);
        using (Assert.EnterMultipleScope()) {
            Assert.That(error.Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(progress.Visibility, Is.EqualTo(Visibility.Visible));
        }
    });

    /// <summary>Verifies that executing a Remove button updates bound root items and panel visibility.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task RemoveButton_UpdatesTheBoundRootItemsAndPanelVisibility() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        var button = Descendants<Button>(control).Single(item => Equals(item.Content, "Remove"));
        button.Command.Execute(button.CommandParameter);
        await Layout(control);
        using (Assert.EnterMultipleScope()) {
            Assert.That(Descendants<ItemsControl>(control).First().Items.Count, Is.EqualTo(0));
            Assert.That(Descendants<Grid>(control).First().Visibility, Is.EqualTo(Visibility.Collapsed));
        }
    });

    /// <summary>Verifies that a count binding reflects root addition and removal.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task CountBinding_TracksRootAddAndRemove() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var text = new TextBlock();
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(IOperationCollection.Count)) { Source = @operator.Operations });
        var root = Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var afterAdd = text.Text;
        @operator.Operations.Remove(root);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        using (Assert.EnterMultipleScope()) {
            Assert.That(afterAdd, Is.EqualTo("1"));
            Assert.That(text.Text, Is.EqualTo("0"));
        }
    });

    /// <summary>Verifies that a running root's Cancel button disables after cancellation and becomes an enabled Remove button at completion.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task RunningRootCancelButton_BecomesDisabledAfterCancellation() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = Start(@operator, async (progress, _) => {
            await progress.Child("failed child", (_, _) => Task.FromException(new IOException("child")));
            await release.Task;
        });
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        var button = Descendants<Button>(control).Single(item => Equals(item.Content, "Cancel"));
        var before = button.IsEnabled;
        button.Command.Execute(button.CommandParameter);
        await Layout(control);
        var after = button.IsEnabled;
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        await Layout(control);
        var finalButton = Descendants<Button>(control).Single(item => Equals(item.Content, "Remove"));
        using (Assert.EnterMultipleScope()) {
            Assert.That(before, Is.True);
            Assert.That(after, Is.False);
            Assert.That(finalButton.IsEnabled, Is.True);
        }
    });
}

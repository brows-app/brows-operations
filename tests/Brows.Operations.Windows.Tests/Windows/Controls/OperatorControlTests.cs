using Brows.Operations;
using Brows.Windows.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Brows.Windows.Controls;

/// <summary>Tests WPF bindings, operation row presentation, and cancellation and removal commands on a dispatcher.</summary>
[TestFixture]
public sealed class OperatorControlTests {
    private const int OperationRowColumnCount = 9;
    private const int OperationStatusIndicatorColumn = 7;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Operation Start(IOperator @operator,
                                   OperationDelegate task,
                                   Action<Operation> beforeStart = null) {
        Operation operation = null;
        new OperationManager((OperationCollection)@operator.Operations)
            .Operate("root", task, started => {
                operation = started;
                beforeStart?.Invoke(started);
            });
        return operation;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task Layout(OperatorControl control, double width = 1200, double height = 600) {
        if (!control.IsInitialized) {
            control.BeginInit();
            control.EndInit();
        }
        control.ApplyTemplate();
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        control.UpdateLayout();
    }

    private static async Task<Operation> StartReporting(IOperator @operator,
                                                        TaskCompletionSource release,
                                                        string data = "details") {
        var reported = Signal();
        var relevant = Signal();
        var root = Start(@operator, async (progress, _) => {
            progress.Change(setProgress: 1,
                            setTarget: 2,
                            data: data,
                            progressRateFormat: "N1",
                            progressRateAverageFormat: "N3",
                            formatProvider: System.Globalization.CultureInfo.InvariantCulture);
            reported.TrySetResult();
            await release.Task;
        }, operation => {
            operation.DisableRateSampling();
            operation.RelevantChanged += (_, _) => {
                if (operation.Relevant) relevant.TrySetResult();
            };
        });
        await reported.Task.WaitAsync(Timeout);
        await relevant.Task.WaitAsync(Timeout);
        root.RefreshRates(_ => TimeSpan.FromSeconds(1));
        return root;
    }

    private static Grid Row(OperatorControl control, OperationBase operation) =>
        Descendants<Grid>(control).Single(grid => grid.ColumnDefinitions.Count == OperationRowColumnCount &&
            ReferenceEquals(grid.DataContext, operation));

    private static Grid HeaderGrid(OperatorControl control) =>
        (Grid)control.Template.FindName("PART_OperationHeaderGrid", control);

    private static ScrollViewer ListScrollViewer(OperatorControl control) =>
        (ScrollViewer)control.Template.FindName("PART_OperationListScrollViewer", control);

    private static Visibility CellVisibility(Grid row, int column) =>
        row.Children.OfType<ContentControl>().Single(item => Grid.GetColumn(item) == column).Visibility;

    private static Visibility HeaderVisibility(OperatorControl control, string header) =>
        HeaderGrid(control).Children.OfType<ContentControl>().Single(item => Equals(item.Content, header)).Visibility;

    private static Control ProgressIndicator(Grid row) =>
        row.Children.OfType<Control>().Single(item =>
            Grid.GetColumn(item) == OperationStatusIndicatorColumn && item.Template?.LoadContent() is ProgressBar);

    private static Grid[] InlineFieldGrids(OperatorControl control, OperationBase operation) =>
        Descendants<Grid>(control)
            .Where(grid => grid.ColumnDefinitions.Count == 2 &&
                           grid.ColumnDefinitions[0].SharedSizeGroup == "InlineHeader" &&
                           ReferenceEquals(grid.DataContext, operation))
            .ToArray();

    private static string[] VisibleInlineFields(OperatorControl control, OperationBase operation) =>
        InlineFieldGrids(control, operation)
            .Where(grid => grid.Visibility == Visibility.Visible)
            .Select(grid => {
                var cells = grid.Children.OfType<ContentControl>().ToArray();
                return $"{cells[0].Content}={cells[1].Content}";
            })
            .ToArray();

    private static OperatorControl NarrowResponsiveControl(IOperator @operator) {
        var scrollStyle = new Style(typeof(ScrollViewer));
        scrollStyle.Setters.Add(new Setter(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        return new OperatorControl {
            Operator = @operator,
            OperationDisplay = OperationDisplay.Responsive,
            OperationScrollViewerStyle = scrollStyle
        };
    }

    private static void AssertCommandsReachable(OperatorControl control) {
        var scrollViewer = ListScrollViewer(control);
        var viewportLeft = scrollViewer.TranslatePoint(new Point(), control).X;
        var viewportRight = viewportLeft + scrollViewer.ViewportWidth;
        var buttons = new[] { "Cancel", "Remove" }
            .Select(content => Descendants<Button>(control).Single(button => Equals(button.Content, content)))
            .ToArray();
        using (Assert.EnterMultipleScope()) {
            foreach (var button in buttons) {
                var left = button.TranslatePoint(new Point(), control).X;
                var center = button.TranslatePoint(
                    new Point(button.ActualWidth / 2, button.ActualHeight / 2), control);
                var hit = VisualTreeHelper.HitTest(control, center)?.VisualHit;
                Assert.That(button.ActualWidth, Is.GreaterThan(0), $"{button.Content} width");
                Assert.That(left, Is.GreaterThanOrEqualTo(viewportLeft - 1), $"{button.Content} left");
                Assert.That(left + button.ActualWidth, Is.LessThanOrEqualTo(viewportRight + 1),
                            $"{button.Content} right");
                Assert.That(hit is not null && (ReferenceEquals(hit, button) || button.IsAncestorOf(hit)), Is.True,
                            $"{button.Content} hit test");
            }
        }
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
            }, OperationFailureBehavior.Record, default).ConfigureAwait(false);
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
        var row = Descendants<Grid>(control).Single(grid =>
            grid.ColumnDefinitions.Count == OperationRowColumnCount &&
            ReferenceEquals(grid.DataContext, root));
        var error = row.Children.OfType<Control>().Single(item =>
            Grid.GetColumn(item) == OperationStatusIndicatorColumn && item.Template?.LoadContent() is ContentPresenter);
        var progress = row.Children.OfType<Control>().Single(item =>
            Grid.GetColumn(item) == OperationStatusIndicatorColumn && item.Template?.LoadContent() is ProgressBar);
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
        Start(@operator, async (root, _) => await root.Child(
            "middle",
            async (middle, _) => await middle.Child(
                "leaf",
                (_, _) => Task.FromException(new IOException("leaf failed")),
                OperationFailureBehavior.Record,
                default),
            OperationFailureBehavior.Record,
            default));
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        var rows = Descendants<Grid>(control).Where(grid => grid.ColumnDefinitions.Count == OperationRowColumnCount &&
            grid.DataContext is OperationBase);
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
            _ = progress.Child("child", async (progress, _) => {
                child = progress;
                await release.Task;
            }, OperationFailureBehavior.Record, default);
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
        var row = Descendants<Grid>(control).Single(grid => grid.ColumnDefinitions.Count == OperationRowColumnCount &&
            ReferenceEquals(grid.DataContext, root));
        var texts = row.Children.OfType<ContentControl>()
            .Where(item => Grid.GetColumn(item) is 3 or 6)
            .Select(item => item.Content?.ToString())
            .ToArray();
        release.SetResult();
        await root.Completion.WaitAsync(Timeout);
        Assert.That(texts, Is.EqualTo(new[] { "3", "8" }));
    });

    /// <summary>Verifies that progress-rate columns bind their formatted values and use their templates.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ProgressRateColumns_BindFormattedValuesAndTemplates() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var reported = Signal();
        var relevant = Signal();
        var root = Start(@operator, async (progress, _) => {
            progress.Change(setProgress: 1,
                            setTarget: 2,
                            progressRateFormat: "N1",
                            progressRateAverageFormat: "N3",
                            formatProvider: System.Globalization.CultureInfo.InvariantCulture);
            reported.TrySetResult();
            await release.Task;
        }, operation => {
            operation.DisableRateSampling();
            operation.RelevantChanged += (_, _) => {
                if (operation.Relevant) relevant.TrySetResult();
            };
        });
        var rateHeaderTemplate = new DataTemplate();
        var averageHeaderTemplate = new DataTemplate();
        var rateContentTemplate = new DataTemplate();
        var averageContentTemplate = new DataTemplate();
        var control = new OperatorControl {
            Operator = @operator,
            OperationProgressRateHeaderTemplate = rateHeaderTemplate,
            OperationProgressRateAverageHeaderTemplate = averageHeaderTemplate,
            OperationProgressRateContentTemplate = rateContentTemplate,
            OperationProgressRateAverageContentTemplate = averageContentTemplate
        };
        try {
            await reported.Task.WaitAsync(Timeout);
            await relevant.Task.WaitAsync(Timeout);
            root.RefreshRates(_ => TimeSpan.FromSeconds(1));
            await Layout(control);
            var row = Descendants<Grid>(control).Single(grid =>
                grid.ColumnDefinitions.Count == OperationRowColumnCount && ReferenceEquals(grid.DataContext, root));
            var rate = row.Children.OfType<ContentControl>().Single(item => Grid.GetColumn(item) == 4);
            var average = row.Children.OfType<ContentControl>().Single(item => Grid.GetColumn(item) == 5);
            var rateHeader = Descendants<ContentControl>(control).Single(item => Equals(item.Content, "Rate"));
            var averageHeader = Descendants<ContentControl>(control).Single(item => Equals(item.Content, "Average"));
            using (Assert.EnterMultipleScope()) {
                Assert.That(root.ProgressRateString, Is.EqualTo("1.0"));
                Assert.That(root.ProgressRateAverageString, Is.EqualTo("1.000"));
                Assert.That(rate.Content, Is.EqualTo("1.0"));
                Assert.That(average.Content, Is.EqualTo("1.000"));
                Assert.That(rate.ContentTemplate, Is.SameAs(rateContentTemplate));
                Assert.That(average.ContentTemplate, Is.SameAs(averageContentTemplate));
                Assert.That(rateHeader.ContentTemplate, Is.SameAs(rateHeaderTemplate));
                Assert.That(averageHeader.ContentTemplate, Is.SameAs(averageHeaderTemplate));
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>Verifies horizontal scrolling keeps operation headers aligned with their values.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task HorizontalScroll_HeadersRemainAlignedWithOperationValues() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var root = Start(@operator, (_, _) => Task.FromException(new IOException("expected error")));
        for (var i = 0; i < 20; i++) {
            Start(@operator, (_, _) => Task.FromException(new IOException("extra error")));
        }
        var scrollStyle = new Style(typeof(ScrollViewer));
        scrollStyle.Setters.Add(new Setter(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Auto));
        scrollStyle.Setters.Add(new Setter(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Auto));
        var control = new OperatorControl {
            Operator = @operator,
            OperationScrollViewerStyle = scrollStyle
        };
        await Layout(control, 1094, 200);

        var row = Descendants<Grid>(control).Single(grid => grid.ColumnDefinitions.Count == OperationRowColumnCount &&
            ReferenceEquals(grid.DataContext, root));
        var rate = row.Children.OfType<ContentControl>().Single(item => Grid.GetColumn(item) == 4);
        var headerGrid = Descendants<Grid>(control).Single(grid =>
            grid.ColumnDefinitions.Count == OperationRowColumnCount &&
            grid.Children.OfType<ContentControl>().Any(item => Equals(item.Content, "Rate")));
        var rateHeader = headerGrid.Children.OfType<ContentControl>()
            .Single(item => Equals(item.Content, "Rate"));
        var scrollViewer = (ScrollViewer)control.Template.FindName("PART_OperationListScrollViewer", control);
        scrollViewer.ScrollToHorizontalOffset(100);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        control.UpdateLayout();
        var verticalScrollBar = Descendants<ScrollBar>(scrollViewer)
            .Single(bar => bar.Orientation == Orientation.Vertical);
        var scrollViewerRight = scrollViewer.TranslatePoint(new Point(), control).X + scrollViewer.ActualWidth;
        var verticalScrollBarRight =
            verticalScrollBar.TranslatePoint(new Point(), control).X + verticalScrollBar.ActualWidth;

        using (Assert.EnterMultipleScope()) {
            Assert.That(scrollViewer.HorizontalOffset, Is.GreaterThan(0));
            Assert.That(scrollViewer.Style, Is.SameAs(scrollStyle));
            Assert.That(((TranslateTransform)headerGrid.RenderTransform).X,
                        Is.EqualTo(-scrollViewer.HorizontalOffset).Within(1));
            Assert.That(scrollViewer.ScrollableHeight, Is.GreaterThan(0));
            Assert.That(verticalScrollBarRight, Is.EqualTo(scrollViewerRight).Within(2));
            Assert.That(rate.TranslatePoint(new Point(), control).X,
                        Is.EqualTo(rateHeader.TranslatePoint(new Point(), control).X).Within(1));
        }
    });

    /// <summary>Verifies the default scroll viewer can expose commands beyond the viewport.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task DefaultHorizontalScroll_OperationCommandsRemainReachable() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        Start(@operator, (_, _) => Task.FromException(new IOException("expected error")));
        var control = new OperatorControl { Operator = @operator };
        await Layout(control, 950, 200);

        var scrollViewer = (ScrollViewer)control.Template.FindName("PART_OperationListScrollViewer", control);
        var removeButton = Descendants<Button>(control).Single(button => Equals(button.Content, "Remove"));
        scrollViewer.ScrollToHorizontalOffset(scrollViewer.ScrollableWidth);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        control.UpdateLayout();

        var scrollViewerLeft = scrollViewer.TranslatePoint(new Point(), control).X;
        var commandLeft = removeButton.TranslatePoint(new Point(), control).X;
        using (Assert.EnterMultipleScope()) {
            Assert.That(scrollViewer.HorizontalScrollBarVisibility, Is.EqualTo(ScrollBarVisibility.Auto));
            Assert.That(scrollViewer.ScrollableWidth, Is.GreaterThan(0));
            Assert.That(removeButton.ActualWidth, Is.GreaterThan(0));
            Assert.That(commandLeft, Is.GreaterThanOrEqualTo(scrollViewerLeft - 1));
            Assert.That(commandLeft + removeButton.ActualWidth,
                        Is.LessThanOrEqualTo(scrollViewerLeft + scrollViewer.ViewportWidth + 1));
        }
    });

    /// <summary>Verifies a supplied scroll viewer style can override automatic horizontal scrolling.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task HorizontalScroll_ConsumerStyleOverridesDefaultVisibility() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        Start(@operator, (_, _) => Task.FromException(new IOException("expected error")));
        var scrollStyle = new Style(typeof(ScrollViewer));
        scrollStyle.Setters.Add(new Setter(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        var control = new OperatorControl {
            Operator = @operator,
            OperationScrollViewerStyle = scrollStyle
        };
        await Layout(control, 950, 200);

        var scrollViewer = (ScrollViewer)control.Template.FindName("PART_OperationListScrollViewer", control);
        using (Assert.EnterMultipleScope()) {
            Assert.That(scrollViewer.Style, Is.SameAs(scrollStyle));
            Assert.That(scrollViewer.HorizontalScrollBarVisibility, Is.EqualTo(ScrollBarVisibility.Disabled));
            Assert.That(scrollViewer.ScrollableWidth, Is.Zero);
        }
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
        var root = Start(@operator, async (progress, _) => await progress.Child(
            "child",
            (_, _) => Task.FromException(new IOException("child")),
            OperationFailureBehavior.Record,
            default));
        var control = new OperatorControl { Operator = @operator };
        await Layout(control);
        var row = Descendants<Grid>(control).Single(grid => grid.ColumnDefinitions.Count == OperationRowColumnCount &&
            ReferenceEquals(grid.DataContext, root));
        var error = row.Children.OfType<Control>().Single(item =>
            Grid.GetColumn(item) == OperationStatusIndicatorColumn && item.Template?.LoadContent() is ContentPresenter);
        var progress = row.Children.OfType<Control>().Single(item =>
            Grid.GetColumn(item) == OperationStatusIndicatorColumn && item.Template?.LoadContent() is ProgressBar);
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
            await progress.Child(
                "failed child",
                (_, _) => Task.FromException(new IOException("child")),
                OperationFailureBehavior.Record,
                default);
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

    /// <summary>Verifies the display defaults to columns and rejects undefined display values.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task OperationDisplay_DefaultsToDefaultAndRejectsUndefinedValues() => WpfTestThread.Run(() => {
        var control = new OperatorControl();
        using (Assert.EnterMultipleScope()) {
            Assert.That(control.OperationDisplay, Is.EqualTo(OperationDisplay.Default));
            Assert.That(() => control.OperationDisplay = (OperationDisplay)42, Throws.ArgumentException);
        }
        return Task.CompletedTask;
    });

    /// <summary>Verifies the default display keeps every column and scrolls when the columns do not fit.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task DefaultDisplay_KeepsColumnsAndScrollsWhenNarrow() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = await StartReporting(@operator, release);
        var control = new OperatorControl { Operator = @operator };
        try {
            await Layout(control, 600, 400);
            var row = Row(control, root);
            using (Assert.EnterMultipleScope()) {
                Assert.That(ListScrollViewer(control).ScrollableWidth, Is.GreaterThan(0));
                Assert.That(ProgressIndicator(row).Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(new[] { 2, 3, 4, 5, 6 }.Select(column => CellVisibility(row, column)),
                            Is.All.EqualTo(Visibility.Visible));
                Assert.That(HeaderVisibility(control, "Rate"), Is.EqualTo(Visibility.Visible));
                Assert.That(InlineFieldGrids(control, root), Is.Empty);
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>
    /// Verifies responsive display collapses the progress bar, then rates, then progress and target, then data.
    /// </summary>
    /// <param name="width">The control width.</param>
    /// <param name="progressBar">Whether the progress bar is displayed.</param>
    /// <param name="rateColumns">Whether the rate and average values are displayed in columns.</param>
    /// <param name="progressColumns">Whether the progress and target values are displayed in columns.</param>
    /// <param name="dataColumns">Whether the data value and column headers are displayed in columns.</param>
    /// <param name="inline">The header and value of each field displayed beneath the operation.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(1400, true, true, true, true, new string[0])]
    [TestCase(1120, false, true, true, true, new string[0])]
    [TestCase(1000, false, false, true, true, new[] { "Rate=1.0", "Average=1.000" })]
    [TestCase(800, false, false, false, true,
              new[] { "Progress=1", "Rate=1.0", "Average=1.000", "Target=2" })]
    [TestCase(600, false, false, false, false,
              new[] { "Data=details", "Progress=1", "Rate=1.0", "Average=1.000", "Target=2" })]
    public Task ResponsiveDisplay_CollapsesInStagesAsWidthDecreases(int width,
                                                                   bool progressBar,
                                                                   bool rateColumns,
                                                                   bool progressColumns,
                                                                   bool dataColumns,
                                                                   string[] inline) => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = await StartReporting(@operator, release);
        var control = new OperatorControl {
            Operator = @operator,
            OperationDisplay = OperationDisplay.Responsive
        };
        try {
            await Layout(control, width, 400);
            var row = Row(control, root);
            var header = HeaderGrid(control);
            var rateVisibility = rateColumns ? Visibility.Visible : Visibility.Collapsed;
            var progressVisibility = progressColumns ? Visibility.Visible : Visibility.Collapsed;
            var dataVisibility = dataColumns ? Visibility.Visible : Visibility.Collapsed;
            using (Assert.EnterMultipleScope()) {
                Assert.That(ListScrollViewer(control).ScrollableWidth, Is.Zero);
                Assert.That(ProgressIndicator(row).Visibility,
                            Is.EqualTo(progressBar ? Visibility.Visible : Visibility.Collapsed));
                Assert.That(CellVisibility(row, 4), Is.EqualTo(rateVisibility));
                Assert.That(CellVisibility(row, 5), Is.EqualTo(rateVisibility));
                Assert.That(HeaderVisibility(control, "Rate"), Is.EqualTo(rateVisibility));
                Assert.That(HeaderVisibility(control, "Average"), Is.EqualTo(rateVisibility));
                Assert.That(header.ColumnDefinitions[4].ActualWidth, Is.EqualTo(rateColumns ? 100 : 0));
                Assert.That(new[] { 3, 6 }.Select(column => CellVisibility(row, column)),
                            Is.All.EqualTo(progressVisibility));
                Assert.That(new[] { "Progress", "Target" }.Select(item => HeaderVisibility(control, item)),
                            Is.All.EqualTo(progressVisibility));
                Assert.That(header.ColumnDefinitions[3].ActualWidth, Is.EqualTo(progressColumns ? 100 : 0));
                Assert.That(header.ColumnDefinitions[6].ActualWidth, Is.EqualTo(progressColumns ? 100 : 0));
                Assert.That(CellVisibility(row, 2), Is.EqualTo(dataVisibility));
                Assert.That(new[] { "Name", "Data" }.Select(item => HeaderVisibility(control, item)),
                            Is.All.EqualTo(dataVisibility));
                Assert.That(header.ColumnDefinitions[2].ActualWidth, Is.EqualTo(dataColumns ? 250 : 0));
                Assert.That(CellVisibility(row, 1), Is.EqualTo(Visibility.Visible));
                Assert.That(VisibleInlineFields(control, root), Is.EqualTo(inline));
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>
    /// Verifies responsive display restores columns as width increases and when switched to the default.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_RestoresColumnsWhenWidenedOrDisabled() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = await StartReporting(@operator, release);
        var control = new OperatorControl {
            Operator = @operator,
            OperationDisplay = OperationDisplay.Responsive
        };
        try {
            await Layout(control, 600, 400);
            var narrow = VisibleInlineFields(control, root);
            await Layout(control, 1400, 400);
            var widened = VisibleInlineFields(control, root);
            var widenedProgressBar = ProgressIndicator(Row(control, root)).Visibility;
            await Layout(control, 600, 400);
            control.OperationDisplay = OperationDisplay.Default;
            await Layout(control, 600, 400);
            var row = Row(control, root);
            using (Assert.EnterMultipleScope()) {
                Assert.That(narrow, Has.Length.EqualTo(5));
                Assert.That(widened, Is.Empty);
                Assert.That(widenedProgressBar, Is.EqualTo(Visibility.Visible));
                Assert.That(InlineFieldGrids(control, root), Is.Empty);
                Assert.That(ProgressIndicator(row).Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(new[] { 2, 3, 4, 5, 6 }.Select(column => CellVisibility(row, column)),
                            Is.All.EqualTo(Visibility.Visible));
                Assert.That(HeaderGrid(control).ColumnDefinitions.Select(column => column.Width.Value).Take(7),
                            Is.EqualTo(new double[] { 50, 250, 250, 100, 100, 100, 100 }));
                Assert.That(ListScrollViewer(control).ScrollableWidth, Is.GreaterThan(0));
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>
    /// Verifies an explicit progress bar width, limited by its maximum width, determines when it collapses.
    /// </summary>
    /// <param name="width">The progress bar's explicit width.</param>
    /// <param name="maxWidth">The progress bar's maximum width, or zero to leave it unset.</param>
    /// <param name="controlWidth">The control width.</param>
    /// <param name="collapsed">Whether the progress bar is expected to collapse.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(300, 0, 1300, true)]
    [TestCase(300, 0, 1500, false)]
    [TestCase(300, 150, 1300, false)]
    public Task ResponsiveDisplay_ProgressBarStyleWidthDeterminesWhenItCollapses(double width,
                                                                                double maxWidth,
                                                                                double controlWidth,
                                                                                bool collapsed) =>
        WpfTestThread.Run(async () => {
            IOperator @operator = new Operator();
            var release = Signal();
            var root = await StartReporting(@operator, release);
            var style = new Style(typeof(ProgressBar));
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, width));
            if (maxWidth > 0) {
                style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, maxWidth));
            }
            var control = new OperatorControl {
                Operator = @operator,
                OperationDisplay = OperationDisplay.Responsive,
                OperationProgressBarStyle = style
            };
            try {
                await Layout(control, controlWidth, 400);
                using (Assert.EnterMultipleScope()) {
                    Assert.That(ProgressIndicator(Row(control, root)).Visibility,
                                Is.EqualTo(collapsed ? Visibility.Collapsed : Visibility.Visible));
                    Assert.That(ListScrollViewer(control).ScrollableWidth, Is.Zero);
                }
            }
            finally {
                release.TrySetResult();
                await root.Completion.WaitAsync(Timeout);
            }
        });

    /// <summary>Verifies the progress bar style's minimum width and margins determine when it collapses.</summary>
    /// <param name="minWidth">The progress bar's minimum width, or zero to leave it unset.</param>
    /// <param name="collapsed">Whether the progress bar is expected to collapse.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(0, false)]
    [TestCase(260, true)]
    public Task ResponsiveDisplay_ProgressBarStyleDeterminesItsMinimumWidth(double minWidth, bool collapsed) =>
        WpfTestThread.Run(async () => {
            IOperator @operator = new Operator();
            var release = Signal();
            var root = await StartReporting(@operator, release);
            var style = new Style(typeof(ProgressBar));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(12, 0, 12, 0)));
            if (minWidth > 0) {
                style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, minWidth));
            }
            var control = new OperatorControl {
                Operator = @operator,
                OperationDisplay = OperationDisplay.Responsive,
                OperationProgressBarStyle = style
            };
            try {
                await Layout(control, 1300, 400);
                Assert.That(ProgressIndicator(Row(control, root)).Visibility,
                            Is.EqualTo(collapsed ? Visibility.Collapsed : Visibility.Visible));
            }
            finally {
                release.TrySetResult();
                await root.Completion.WaitAsync(Timeout);
            }
        });

    /// <summary>
    /// Verifies a progress bar minimum width from a host dynamic resource determines when it collapses.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_ProgressBarMinimumWidthUsesHostResources() =>
        WpfTestThread.Run(async () => {
            IOperator @operator = new Operator();
            var release = Signal();
            var root = await StartReporting(@operator, release);
            var style = new Style(typeof(ProgressBar));
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty,
                                         new DynamicResourceExtension("BarMinimum")));
            var control = new OperatorControl {
                Operator = @operator,
                OperationDisplay = OperationDisplay.Responsive,
                OperationProgressBarStyle = style
            };
            control.Resources["BarMinimum"] = 300d;
            try {
                await Layout(control, 1300, 400);
                var narrow = ProgressIndicator(Row(control, root)).Visibility;
                var scrollableWidth = ListScrollViewer(control).ScrollableWidth;
                control.Resources["BarMinimum"] = 100d;
                await Layout(control, 1300, 400);
                var indicator = ProgressIndicator(Row(control, root));
                using (Assert.EnterMultipleScope()) {
                    Assert.That(narrow, Is.EqualTo(Visibility.Collapsed));
                    Assert.That(scrollableWidth, Is.Zero);
                    Assert.That(indicator.Visibility, Is.EqualTo(Visibility.Visible));
                    Assert.That(Descendants<ProgressBar>(indicator).Single().MinWidth, Is.EqualTo(100));
                    Assert.That(ListScrollViewer(control).ScrollableWidth, Is.Zero);
                }
            }
            finally {
                release.TrySetResult();
                await root.Completion.WaitAsync(Timeout);
            }
        });

    /// <summary>
    /// Verifies the progress bar style is not applied outside operation rows in Default mode, and never receives
    /// the host's data context in Responsive mode.
    /// </summary>
    /// <param name="display">The display mode to verify.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(OperationDisplay.Default)]
    [TestCase(OperationDisplay.Responsive)]
    public Task ProgressBarStyle_IsNotAppliedWithHostDataContext(OperationDisplay display) =>
        WpfTestThread.Run(async () => {
        var converter = new RecordingConverter();
        var style = new Style(typeof(ProgressBar));
        style.Setters.Add(new Setter(FrameworkElement.TagProperty, new Binding { Converter = converter }));
        var control = new OperatorControl {
            DataContext = "host",
            Operator = new Operator(),
            OperationDisplay = display,
            OperationProgressBarStyle = style
        };
        await Layout(control, 1400, 400);
        if (display == OperationDisplay.Default) {
            Assert.That(converter.Values, Is.Empty);
        }
        else {
            Assert.That(converter.Values, Has.No.Member("host"));
        }
    });

    /// <summary>Verifies root commands remain inside a narrow viewport that cannot scroll horizontally.</summary>
    /// <param name="inset">
    /// The horizontal inset added by a custom items template: <c>none</c>, a left <c>margin</c>, a padded
    /// <c>border</c>, or a default <c>listbox</c>.
    /// </param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase("none")]
    [TestCase("margin")]
    [TestCase("border")]
    [TestCase("listbox")]
    public Task ResponsiveDisplay_NarrowViewportKeepsCommandsReachable(string inset) => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var running = await StartReporting(@operator, release);
        Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
        var scrollStyle = new Style(typeof(ScrollViewer));
        scrollStyle.Setters.Add(new Setter(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        var control = new OperatorControl {
            Operator = @operator,
            OperationDisplay = OperationDisplay.Responsive,
            OperationScrollViewerStyle = scrollStyle
        };
        if (inset != "none") {
            var items = new FrameworkElementFactory(inset == "listbox" ? typeof(ListBox) : typeof(ItemsControl));
            items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
            if (inset == "listbox") {
                control.OperationItemsTemplate = new DataTemplate { VisualTree = items };
            }
            else if (inset == "margin") {
                items.SetValue(FrameworkElement.MarginProperty, new Thickness(75, 0, 0, 0));
                control.OperationItemsTemplate = new DataTemplate { VisualTree = items };
            }
            else {
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.PaddingProperty, new Thickness(30));
                border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
                border.SetValue(Border.BorderBrushProperty, Brushes.Gray);
                border.AppendChild(items);
                control.OperationItemsTemplate = new DataTemplate { VisualTree = border };
            }
        }
        try {
            await Layout(control, 320, 400);
            AssertCommandsReachable(control);
        }
        finally {
            release.TrySetResult();
            await running.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>Verifies root commands stay reachable when an items template inset changes at a fixed width.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_InsetChangeAtFixedWidthKeepsCommandsReachable() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var running = await StartReporting(@operator, release);
        Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
        var control = NarrowResponsiveControl(@operator);
        var items = new FrameworkElementFactory(typeof(ItemsControl));
        items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
        items.SetResourceReference(FrameworkElement.MarginProperty, "ItemsInset");
        control.OperationItemsTemplate = new DataTemplate { VisualTree = items };
        control.Resources["ItemsInset"] = new Thickness(0);
        try {
            await Layout(control, 320, 400);
            AssertCommandsReachable(control);
            control.Resources["ItemsInset"] = new Thickness(75, 0, 0, 0);
            await Layout(control, 320, 400);
            AssertCommandsReachable(control);
            control.Resources["ItemsInset"] = new Thickness(0);
            await Layout(control, 320, 400);
            AssertCommandsReachable(control);
        }
        finally {
            release.TrySetResult();
            await running.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>Verifies every root's commands stay reachable when root rows have different insets.</summary>
    /// <param name="runningFirst">Whether the running root precedes the failed root.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(true)]
    [TestCase(false)]
    public Task ResponsiveDisplay_DifferentRootInsetsKeepCommandsReachable(bool runningFirst) =>
        WpfTestThread.Run(async () => {
            IOperator @operator = new Operator();
            var release = Signal();
            if (!runningFirst) {
                Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
            }
            var running = await StartReporting(@operator, release);
            if (runningFirst) {
                Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
            }
            var control = NarrowResponsiveControl(@operator);
            var containerStyle = new Style(typeof(ContentPresenter));
            var second = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 };
            second.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(75, 0, 0, 0)));
            containerStyle.Triggers.Add(second);
            var items = new FrameworkElementFactory(typeof(ItemsControl));
            items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
            items.SetValue(ItemsControl.AlternationCountProperty, 2);
            items.SetValue(ItemsControl.ItemContainerStyleProperty, containerStyle);
            control.OperationItemsTemplate = new DataTemplate { VisualTree = items };
            try {
                await Layout(control, 320, 400);
                AssertCommandsReachable(control);
            }
            finally {
                release.TrySetResult();
                await running.Completion.WaitAsync(Timeout);
            }
        });

    /// <summary>Verifies a nested operation's inline values wrap within the viewport despite nested insets.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_NestedInlineValuesWrapWithinTheViewport() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var data = string.Join(" ", Enumerable.Repeat("details", 60));
        var root = Start(@operator, async (progress, _) => {
            await progress.Child("child", (child, _) => {
                child.Change(data: data);
                return Task.FromException(new IOException("child failed"));
            }, OperationFailureBehavior.Record, default);
            await release.Task;
        });
        var items = new FrameworkElementFactory(typeof(ItemsControl));
        items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
        items.SetValue(FrameworkElement.MarginProperty, new Thickness(30, 0, 30, 0));
        var control = new OperatorControl {
            Operator = @operator,
            OperationDisplay = OperationDisplay.Responsive,
            OperationItemsTemplate = new DataTemplate { VisualTree = items }
        };
        try {
            await Layout(control, 600, 400);
            var scrollViewer = ListScrollViewer(control);
            var childRow = Row(control, ((IEnumerable)root.ChildSource).Cast<OperationBase>().Single());
            var dataText = Descendants<TextBlock>(control)
                .Single(text => text.Text == data && !childRow.IsAncestorOf(text));
            var right = dataText.TranslatePoint(new Point(dataText.ActualWidth, 0), scrollViewer).X;
            using (Assert.EnterMultipleScope()) {
                Assert.That(scrollViewer.ScrollableWidth, Is.Zero);
                Assert.That(right, Is.LessThanOrEqualTo(scrollViewer.ViewportWidth + 1));
                Assert.That(dataText.ActualHeight, Is.GreaterThan(dataText.FontSize * 2));
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>
    /// Verifies an aligned items template restores every column when widened after the narrowest stage.
    /// </summary>
    /// <param name="alignment">The horizontal alignment of the items template's list.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(HorizontalAlignment.Right)]
    [TestCase(HorizontalAlignment.Center)]
    public Task ResponsiveDisplay_AlignedItemsRestoreColumnsWhenWidened(HorizontalAlignment alignment) =>
        WpfTestThread.Run(async () => {
            IOperator @operator = new Operator();
            var release = Signal();
            var root = await StartReporting(@operator, release);
            var items = new FrameworkElementFactory(typeof(ItemsControl));
            items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
            items.SetValue(FrameworkElement.HorizontalAlignmentProperty, alignment);
            var control = new OperatorControl {
                Operator = @operator,
                OperationDisplay = OperationDisplay.Responsive,
                OperationItemsTemplate = new DataTemplate { VisualTree = items }
            };
            try {
                await Layout(control, 320, 400);
                var narrow = VisibleInlineFields(control, root);
                await Layout(control, 1400, 400);
                using (Assert.EnterMultipleScope()) {
                    Assert.That(narrow, Has.Length.EqualTo(5));
                    Assert.That(VisibleInlineFields(control, root), Is.Empty);
                    Assert.That(ProgressIndicator(Row(control, root)).Visibility, Is.EqualTo(Visibility.Visible));
                    Assert.That(new[] { "Name", "Data", "Progress", "Rate" }
                                    .Select(item => HeaderVisibility(control, item)),
                                Is.All.EqualTo(Visibility.Visible));
                    Assert.That(HeaderGrid(control).ColumnDefinitions[1].ActualWidth, Is.EqualTo(250));
                }
            }
            finally {
                release.TrySetResult();
                await root.Completion.WaitAsync(Timeout);
            }
        });

    /// <summary>
    /// Verifies inline values rewrap within the viewport when an items template inset changes at a fixed width.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_InlineValuesRewrapWhenInsetChangesAtFixedWidth() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var data = string.Join(" ", Enumerable.Repeat("details", 60));
        var root = await StartReporting(@operator, release, data);
        var items = new FrameworkElementFactory(typeof(ItemsControl));
        items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
        items.SetResourceReference(FrameworkElement.MarginProperty, "ItemsInset");
        var control = new OperatorControl {
            Operator = @operator,
            OperationDisplay = OperationDisplay.Responsive,
            OperationItemsTemplate = new DataTemplate { VisualTree = items }
        };
        control.Resources["ItemsInset"] = new Thickness(0);
        try {
            (double Right, double Height, double Scrollable) Measure() {
                var scrollViewer = ListScrollViewer(control);
                var row = Row(control, root);
                var text = Descendants<TextBlock>(control).Single(item => item.Text == data && !row.IsAncestorOf(item));
                var right = text.TranslatePoint(new Point(text.ActualWidth, 0), scrollViewer).X;
                return (right - scrollViewer.ViewportWidth, text.ActualHeight, scrollViewer.ScrollableWidth);
            }
            await Layout(control, 600, 400);
            var initial = Measure();
            control.Resources["ItemsInset"] = new Thickness(100, 0, 0, 0);
            await Layout(control, 600, 400);
            var inset = Measure();
            control.Resources["ItemsInset"] = new Thickness(0);
            await Layout(control, 600, 400);
            var restored = Measure();
            using (Assert.EnterMultipleScope()) {
                Assert.That(initial.Right, Is.LessThanOrEqualTo(1));
                Assert.That(inset.Right, Is.LessThanOrEqualTo(1));
                Assert.That(inset.Scrollable, Is.Zero);
                Assert.That(inset.Height, Is.GreaterThan(initial.Height));
                Assert.That(restored.Right, Is.LessThanOrEqualTo(1));
                Assert.That(restored.Height, Is.EqualTo(initial.Height));
                Assert.That(restored.Scrollable, Is.Zero);
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>
    /// Verifies responsive layout settles when a stage change decides whether automatic vertical scrolling
    /// is needed, and still restores the columns when widened.
    /// </summary>
    /// <param name="running">
    /// Whether the root is running, near the progress bar breakpoint, rather than failed, near the data breakpoint.
    /// </param>
    /// <param name="failedRoots">The number of failed roots, when the root is not running.</param>
    /// <param name="width">
    /// A control width whose viewport straddles the breakpoint with and without a scrollbar.
    /// </param>
    /// <param name="scrollBarWidth">The width of a custom vertical scrollbar, or zero for the theme's.</param>
    /// <param name="minHeight">The first control height to lay out.</param>
    /// <param name="maxHeight">The last control height to lay out.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(false, 1, 660, 0, 60, 160)]
    [TestCase(true, 0, 1160, 0, 60, 160)]
    [TestCase(false, 4, 660, 5, 380, 480)]
    public Task ResponsiveDisplay_SettlesWithAutomaticVerticalScrolling(bool running,
                                                                       int failedRoots,
                                                                       double width,
                                                                       double scrollBarWidth,
                                                                       double minHeight,
                                                                       double maxHeight) =>
        WpfTestThread.Run(async () => {
            const int changeLimit = 20;
            IOperator @operator = new Operator();
            var release = Signal();
            var root = running ? await StartReporting(@operator, release) : null;
            for (var i = 0; i < failedRoots; i++) {
                root = Start(@operator, (progress, _) => {
                    progress.Change(data: "details");
                    return Task.FromException(new IOException("failure"));
                });
            }
            var scrollStyle = new Style(typeof(ScrollViewer));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                                               ScrollBarVisibility.Auto));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty,
                                               ScrollBarVisibility.Auto));
            if (scrollBarWidth > 0) {
                var verticalBar = new Trigger {
                    Property = ScrollBar.OrientationProperty,
                    Value = Orientation.Vertical
                };
                verticalBar.Setters.Add(new Setter(FrameworkElement.WidthProperty, scrollBarWidth));
                verticalBar.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, scrollBarWidth));
                var scrollBarStyle = new Style(typeof(ScrollBar));
                scrollBarStyle.Triggers.Add(verticalBar);
                scrollStyle.Resources.Add(typeof(ScrollBar), scrollBarStyle);
            }
            var barStyle = new Style(typeof(ProgressBar));
            barStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 42d));
            var control = new OperatorControl {
                Operator = @operator,
                OperationDisplay = OperationDisplay.Responsive,
                OperationScrollViewerStyle = scrollStyle,
                OperationProgressBarStyle = barStyle
            };
            control.BeginInit();
            control.EndInit();
            control.ApplyTemplate();
            var widthChanges = 0;
            ListScrollViewer(control).ScrollChanged += (_, e) => {
                if (e.ViewportWidthChange != 0 && ++widthChanges == changeLimit) {
                    control.OperationDisplay = OperationDisplay.Default; // Stop an oscillating layout.
                }
            };
            try {
                var oscillatingHeight = 0d;
                for (var height = minHeight; height <= maxHeight && oscillatingHeight == 0; height += 2) {
                    widthChanges = 0;
                    await Layout(control, width, height);
                    await Layout(control, width, height);
                    if (widthChanges >= changeLimit) {
                        oscillatingHeight = height;
                    }
                }
                await Layout(control, 1400, 400);
                using (Assert.EnterMultipleScope()) {
                    Assert.That(oscillatingHeight, Is.Zero, "Height at which the viewport width kept changing");
                    Assert.That(control.OperationDisplay, Is.EqualTo(OperationDisplay.Responsive));
                    Assert.That(control.IsProgressBarCollapsed, Is.False);
                    Assert.That(control.IsDataInline, Is.False);
                }
            }
            finally {
                release.TrySetResult();
                await root.Completion.WaitAsync(Timeout);
            }
        });

    /// <summary>
    /// Verifies scrollbar space is reserved once across vertical scrollbar visibility changes.
    /// </summary>
    /// <param name="visibility">
    /// The vertical scrolling policy.
    /// </param>
    /// <param name="gutter">
    /// The space the template permanently reserves beside the presenter.
    /// </param>
    /// <returns>
    /// A task representing execution of the test.
    /// </returns>
    [TestCase(ScrollBarVisibility.Auto, 0)]
    [TestCase(ScrollBarVisibility.Visible, 0)]
    [TestCase(ScrollBarVisibility.Auto, 12)]
    public Task ResponsiveDisplay_SettlesWithVerticalScrollbar(ScrollBarVisibility visibility, int gutter) =>
        WpfTestThread.Run(async () => {
            const int changeLimit = 20;
            IOperator @operator = new Operator();
            for (var i = 0; i < 4; i++) {
                Start(@operator, (progress, _) => {
                    progress.Change(data: "short");
                    return Task.FromException(new IOException("short"));
                });
            }
            var template = (ControlTemplate)XamlReader.Parse($$"""
                <ControlTemplate TargetType="ScrollViewer"
                                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Grid>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition/>
                      <ColumnDefinition Width="{{gutter}}"/>
                    </Grid.ColumnDefinitions>
                    <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Content="{TemplateBinding Content}"/>
                    <ScrollBar Grid.ColumnSpan="2"
                               x:Name="PART_VerticalScrollBar" Orientation="Vertical"
                               Width="30" MaxWidth="12" MinWidth="0"
                               HorizontalAlignment="Right" Maximum="{TemplateBinding ScrollableHeight}"
                               ViewportSize="{TemplateBinding ViewportHeight}" Value="{TemplateBinding VerticalOffset}"
                               Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
                  </Grid>
                </ControlTemplate>
                """);
            var scrollStyle = new Style(typeof(ScrollViewer));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.TemplateProperty, template));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, visibility));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                                               ScrollBarVisibility.Auto));
            var control = new OperatorControl {
                Operator = @operator,
                OperationDisplay = OperationDisplay.Responsive,
                OperationScrollViewerStyle = scrollStyle
            };
            await Layout(control, 670, 600);
            Assert.That(control.IsDataInline, Is.False, "The 650 DIP columns fit beside the 12 DIP scrollbar.");
            var scrollViewer = ListScrollViewer(control);
            var heightChanges = 0;
            scrollViewer.ScrollChanged += (_, e) => {
                if (e.ExtentHeightChange != 0 && ++heightChanges == changeLimit) {
                    control.OperationDisplay = OperationDisplay.Default; // Stop an oscillating layout.
                }
            };

            await Layout(control, 655, 430);

            using (Assert.EnterMultipleScope()) {
                Assert.That(heightChanges, Is.LessThan(changeLimit));
                Assert.That(control.OperationDisplay, Is.EqualTo(OperationDisplay.Responsive));
                Assert.That(scrollViewer.ViewportWidth, Is.EqualTo(655 - gutter));
                Assert.That(control.IsDataInline, Is.True);
                var button = Descendants<Button>(control).First(item => Equals(item.Content, "Remove"));
                var right = button.TranslatePoint(new Point(button.ActualWidth, 0), scrollViewer).X;
                Assert.That(right, Is.LessThanOrEqualTo(643.5));
                var point = button.TranslatePoint(new Point(button.ActualWidth - 2, button.ActualHeight / 2), control);
                var hit = VisualTreeHelper.HitTest(control, point)?.VisualHit;
                Assert.That(hit is not null && (hit == button || button.IsAncestorOf(hit)), Is.True);
            }
            await Layout(control, 670, 600);
            Assert.That(control.IsDataInline, Is.False);
        });

    /// <summary>
    /// Verifies a consumer animation survives collapse without destabilizing stage selection.
    /// </summary>
    /// <returns>
    /// A task representing execution of the test.
    /// </returns>
    [Test]
    public Task ResponsiveDisplay_PreservesAnimatedRateWidths() => WpfTestThread.Run(async () => {
        IOperator operations = new Operator();
        var root = Start(operations, (_, _) => Task.FromException(new IOException("short")));
        await root.Completion.WaitAsync(Timeout);
        var control = new OperatorControl { Operator = operations, OperationDisplay = OperationDisplay.Responsive };
        await Layout(control, 1400);
        var rates = HeaderGrid(control).ColumnDefinitions.Skip(4).Take(2).ToArray();
        foreach (var column in rates) {
            column.Width = new GridLength(65);
            AnimateWidth(column, 100);
        }
        var changes = 0;
        ListScrollViewer(control).ScrollChanged += (_, _) => {
            if (++changes == 24) {
                control.OperationDisplay = OperationDisplay.Default;
            }
        };
        await Layout(control, 1020);
        Assert.That(control.AreProgressRatesInline, Is.True);
        Assert.That(rates.Select(column => column.ActualWidth), Is.All.Zero);
        await Layout(control, 1400);
        Assert.That(rates.Select(column => column.ActualWidth), Is.All.EqualTo(100));
        await Layout(control, 1020);
        foreach (var rate in rates) {
            AnimateWidth(rate, 65);
        }
        await Layout(control, 1020);
        Assert.That(control.AreProgressRatesInline, Is.False);
        Assert.That(rates.Select(column => column.ActualWidth), Is.All.EqualTo(65));
        Assert.That(control.OperationDisplay, Is.EqualTo(OperationDisplay.Responsive));

        static void AnimateWidth(ColumnDefinition column, double width) {
            var animation = new ObjectAnimationUsingKeyFrames { Duration = Duration.Forever };
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(new GridLength(width),
                                                               KeyTime.FromTimeSpan(TimeSpan.Zero)));
            var clock = animation.CreateClock();
            column.ApplyAnimationClock(ColumnDefinition.WidthProperty, clock);
            clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
        }
    });

    /// <summary>
    /// Verifies hidden rate column minimum widths leave the root command reachable.
    /// </summary>
    /// <returns>
    /// A task representing execution of the test.
    /// </returns>
    [Test]
    public Task ResponsiveDisplay_CollapsesRateColumnMinimumWidths() => WpfTestThread.Run(async () => {
        IOperator operations = new Operator();
        var root = Start(operations, (_, _) => Task.FromException(new IOException("short")));
        await root.Completion.WaitAsync(Timeout);
        var control = new OperatorControl { Operator = operations, OperationDisplay = OperationDisplay.Responsive };
        await Layout(control, 1400);
        var rates = HeaderGrid(control).ColumnDefinitions.Skip(4).Take(2).ToArray();
        foreach (var rate in rates) {
            rate.MinWidth = 65;
        }
        await Layout(control, 900);
        Assert.That(rates.Select(column => column.ActualWidth), Is.All.Zero);
        var button = Descendants<Button>(control).Single(item => Equals(item.Content, "Remove"));
        var right = button.TranslatePoint(new Point(button.ActualWidth, 0), ListScrollViewer(control)).X;
        Assert.That(right, Is.LessThanOrEqualTo(ListScrollViewer(control).ViewportWidth));
        await Layout(control, 1400);
        Assert.That(rates.Select(column => column.MinWidth), Is.All.EqualTo(65));
        Assert.That(rates.Select(column => column.ActualWidth), Is.All.EqualTo(100));
    });

    /// <summary>Verifies root commands stay reachable when deeper operations widen the scroll extent.</summary>
    /// <param name="deepRootRunning">Whether the root with descendants is running rather than failed.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(true)]
    [TestCase(false)]
    public Task ResponsiveDisplay_DeeperOperationsKeepRootCommandsReachable(bool deepRootRunning) =>
        WpfTestThread.Run(async () => {
            IOperator @operator = new Operator();
            var release = Signal();
            var recorded = Signal();
            var deep = Start(@operator, async (progress, _) => {
                await progress.Child("child", async (child, _) => {
                    await child.Child("grandchild", (grandchild, _) => {
                        grandchild.Change(data: "short");
                        return Task.FromException(new IOException("grandchild failed"));
                    }, OperationFailureBehavior.Record, default);
                }, OperationFailureBehavior.Record, default);
                recorded.TrySetResult();
                if (!deepRootRunning) {
                    throw new IOException("root failed");
                }
                await release.Task;
            });
            await recorded.Task.WaitAsync(Timeout);
            var other = deepRootRunning
                ? Start(@operator, (_, _) => Task.FromException(new IOException("failure")))
                : await StartReporting(@operator, release);
            var items = new FrameworkElementFactory(typeof(ItemsControl));
            items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
            items.SetValue(FrameworkElement.MarginProperty, new Thickness(35, 0, 35, 0));
            var control = new OperatorControl {
                Operator = @operator,
                OperationDisplay = OperationDisplay.Responsive,
                OperationItemsTemplate = new DataTemplate { VisualTree = items }
            };
            try {
                await Layout(control, 400, 400);
                Assert.That(ListScrollViewer(control).ScrollableWidth, Is.GreaterThan(0));
                AssertCommandsReachable(control);
            }
            finally {
                release.TrySetResult();
                await Task.WhenAll(deep.Completion, other.Completion).WaitAsync(Timeout);
            }
        });

    /// <summary>Verifies root commands stay reachable in an items template with a width limit.</summary>
    /// <param name="limit">The width property that limits the items template's list.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase("Width")]
    [TestCase("MaxWidth")]
    public Task ResponsiveDisplay_WidthLimitedItemsKeepCommandsReachable(string limit) =>
        WpfTestThread.Run(async () => {
            IOperator @operator = new Operator();
            var release = Signal();
            var running = await StartReporting(@operator, release);
            Start(@operator, (_, _) => Task.FromException(new IOException("failure")));
            var items = new FrameworkElementFactory(typeof(ItemsControl));
            items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
            items.SetValue(limit == "Width" ? FrameworkElement.WidthProperty : FrameworkElement.MaxWidthProperty,
                           320d);
            var control = new OperatorControl {
                Operator = @operator,
                OperationDisplay = OperationDisplay.Responsive,
                OperationItemsTemplate = new DataTemplate { VisualTree = items }
            };
            try {
                await Layout(control, 1400, 400);
                Assert.That(control.IsDataInline, Is.True);
                AssertCommandsReachable(control);
            }
            finally {
                release.TrySetResult();
                await running.Completion.WaitAsync(Timeout);
            }
        });

    /// <summary>
    /// Verifies an items template inset wider than the viewport gives the same layout whichever stage it narrows from.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_InsetWiderThanViewportIsIndependentOfResizePath() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        Start(@operator, (progress, _) => {
            progress.Change(data: "details");
            return Task.FromException(new IOException("failure"));
        });
        OperatorControl Create() {
            var items = new FrameworkElementFactory(typeof(ItemsControl));
            items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
            items.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 400, 0));
            return new OperatorControl {
                Operator = @operator,
                OperationDisplay = OperationDisplay.Responsive,
                OperationItemsTemplate = new DataTemplate { VisualTree = items }
            };
        }
        string Describe(OperatorControl control) =>
            $"{control.IsProgressBarCollapsed} {control.AreProgressRatesInline} {control.IsProgressInline} " +
            $"{control.IsDataInline} {ListScrollViewer(control).ScrollableWidth} " +
            string.Join(",", HeaderGrid(control).ColumnDefinitions.Select(column => column.ActualWidth));
        var direct = Create();
        await Layout(direct, 1400, 400);
        await Layout(direct, 300, 400);
        var stepped = Create();
        await Layout(stepped, 1400, 400);
        await Layout(stepped, 600, 400);
        await Layout(stepped, 300, 400);
        using (Assert.EnterMultipleScope()) {
            Assert.That(Describe(direct), Is.EqualTo(Describe(stepped)));
            Assert.That(direct.IsDataInline, Is.True);
            Assert.That(HeaderGrid(direct).ColumnDefinitions[1].ActualWidth, Is.Zero);
        }
    });

    /// <summary>
    /// Verifies applying a template in Default mode keeps its header columns' local widths and bindings.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task DefaultDisplay_PreservesTemplateHeaderColumnWidths() => WpfTestThread.Run(async () => {
        var control = new OperatorControl {
            Operator = new Operator(),
            Template = new ControlTemplate(typeof(OperatorControl)) {
                VisualTree = new FrameworkElementFactory(typeof(LocalWidthHeaderGrid), "PART_OperationHeaderGrid")
            }
        };
        await Layout(control, 1400, 400);
        var columns = HeaderGrid(control).ColumnDefinitions;
        using (Assert.EnterMultipleScope()) {
            Assert.That(columns.Select(column => column.Width), Is.EqualTo(new[] {
                new GridLength(120), new GridLength(200)
            }));
            Assert.That(BindingOperations.IsDataBound(columns[1], ColumnDefinition.WidthProperty), Is.True);
        }
    });

    /// <summary>Verifies a header column's width binding survives a Responsive-to-Default round trip.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_RestoresHeaderColumnWidthBindingsWhenDisabled() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = await StartReporting(@operator, release);
        var control = new OperatorControl { Operator = @operator };
        try {
            await Layout(control, 1400, 400);
            var data = HeaderGrid(control).ColumnDefinitions[2];
            BindingOperations.SetBinding(data, ColumnDefinition.WidthProperty,
                                         new Binding { Source = new GridLength(300) });
            control.OperationDisplay = OperationDisplay.Responsive;
            await Layout(control, 600, 400);
            var collapsed = data.ActualWidth;
            control.OperationDisplay = OperationDisplay.Default;
            await Layout(control, 1400, 400);
            using (Assert.EnterMultipleScope()) {
                Assert.That(collapsed, Is.Zero);
                Assert.That(BindingOperations.IsDataBound(data, ColumnDefinition.WidthProperty), Is.True);
                Assert.That(data.Width, Is.EqualTo(new GridLength(300)));
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>Verifies temporary header widths do not write to consumer width binding sources.</summary>
    /// <param name="mode">The binding mode that can write back to the source.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(BindingMode.TwoWay)]
    [TestCase(BindingMode.OneWayToSource)]
    public Task ResponsiveDisplay_KeepsHeaderColumnWidthSources(BindingMode mode) => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = await StartReporting(@operator, release);
        var control = new OperatorControl { Operator = @operator, OperationDisplay = OperationDisplay.Responsive };
        try {
            await Layout(control, 1400, 400);
            var rate = HeaderGrid(control).ColumnDefinitions[4];
            var source = new ColumnDefinition { Width = new GridLength(100) };
            BindingOperations.SetBinding(rate, ColumnDefinition.WidthProperty, new Binding(nameof(rate.Width)) {
                Source = source,
                Mode = mode
            });
            if (mode == BindingMode.OneWayToSource) {
                rate.SetCurrentValue(ColumnDefinition.WidthProperty, new GridLength(100));
            }
            await Layout(control, 1400, 400);
            await Layout(control, 1020, 400);
            var collapsed = (control.AreProgressRatesInline, rate.ActualWidth, source.Width,
                             BindingOperations.IsDataBound(rate, ColumnDefinition.WidthProperty));
            control.OperationDisplay = OperationDisplay.Default;
            await Layout(control, 1400, 400);
            using (Assert.EnterMultipleScope()) {
                Assert.That(collapsed, Is.EqualTo((true, 0d, new GridLength(100), true)));
                Assert.That(source.Width, Is.EqualTo(new GridLength(100)));
                Assert.That(rate.ActualWidth, Is.EqualTo(100));
                Assert.That(BindingOperations.IsDataBound(rate, ColumnDefinition.WidthProperty), Is.True);
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>
    /// Verifies collapsed header columns reappear at a fixed width when their bound widths shrink enough to fit.
    /// </summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_FollowsBoundHeaderColumnWidthChanges() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = await StartReporting(@operator, release);
        var control = new OperatorControl { Operator = @operator, OperationDisplay = OperationDisplay.Responsive };
        try {
            await Layout(control, 1400, 400);
            var sources = new[] { 4, 5 }.Select(index => {
                var source = new ColumnDefinition { Width = new GridLength(100) };
                BindingOperations.SetBinding(HeaderGrid(control).ColumnDefinitions[index],
                                             ColumnDefinition.WidthProperty,
                                             new Binding(nameof(source.Width)) { Source = source });
                return source;
            }).ToArray();
            await Layout(control, 1020, 400);
            var inline = control.AreProgressRatesInline;
            foreach (var source in sources) {
                source.Width = new GridLength(65);
            }
            await Layout(control, 1020, 400);
            var header = HeaderGrid(control);
            using (Assert.EnterMultipleScope()) {
                Assert.That(inline, Is.True);
                Assert.That(control.AreProgressRatesInline, Is.False);
                Assert.That(control.IsProgressBarCollapsed, Is.True);
                Assert.That(new[] { 4, 5 }.Select(index => header.ColumnDefinitions[index].ActualWidth),
                            Is.All.EqualTo(65));
                Assert.That(ListScrollViewer(control).ScrollableWidth, Is.Zero);
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>Verifies collapsed headers respond to changes in their dynamic width resource.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_FollowsDynamicResourceHeaderColumnWidthChanges() => WpfTestThread.Run(async () => {
        const string resourceKey = "RateWidth";
        IOperator @operator = new Operator();
        var release = Signal();
        var root = await StartReporting(@operator, release);
        var control = new OperatorControl { Operator = @operator, OperationDisplay = OperationDisplay.Responsive };
        try {
            await Layout(control, 1400, 400);
            var header = HeaderGrid(control);
            var rateColumns = new[] { header.ColumnDefinitions[4], header.ColumnDefinitions[5] };
            control.Resources[resourceKey] = new GridLength(100);
            foreach (var column in rateColumns) {
                column.SetResourceReference(ColumnDefinition.WidthProperty, resourceKey);
            }
            await Layout(control, 1020, 400);
            using (Assert.EnterMultipleScope()) {
                Assert.That(control.AreProgressRatesInline, Is.True);
                Assert.That(rateColumns.Select(column => column.ActualWidth), Is.All.Zero);
            }

            control.Resources[resourceKey] = new GridLength(65);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            using (Assert.EnterMultipleScope()) {
                Assert.That(control.AreProgressRatesInline, Is.False);
                Assert.That(rateColumns.Select(column => column.ActualWidth), Is.All.EqualTo(65));
                Assert.That(ListScrollViewer(control).ScrollableWidth, Is.Zero);
            }

            control.Resources[resourceKey] = new GridLength(100);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            using (Assert.EnterMultipleScope()) {
                Assert.That(control.AreProgressRatesInline, Is.True);
                Assert.That(rateColumns.Select(column => column.ActualWidth), Is.All.Zero);
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>Verifies responsive display shows a long error beneath its operation with wrapping text.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_WrapsErrorsBeneathTheOperation() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var message = string.Join(" ", Enumerable.Repeat("failure", 80));
        var root = Start(@operator, (_, _) => Task.FromException(new IOException(message)));
        var control = new OperatorControl {
            Operator = @operator,
            OperationDisplay = OperationDisplay.Responsive
        };
        await Layout(control, 1400, 400);
        var row = Row(control, root);
        var columnError = row.Children.OfType<Control>().Single(item =>
            Grid.GetColumn(item) == OperationStatusIndicatorColumn && item.Template?.LoadContent() is ContentPresenter);
        var scrollViewer = ListScrollViewer(control);
        var columnErrorText = Descendants<TextBlock>(columnError).ToArray();
        var errorText = Descendants<TextBlock>(control)
            .Single(text => text.Text == message && !columnErrorText.Contains(text));
        using (Assert.EnterMultipleScope()) {
            Assert.That(columnError.Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(ProgressIndicator(row).Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(errorText.TextWrapping, Is.EqualTo(TextWrapping.Wrap));
            Assert.That(errorText.ActualHeight, Is.GreaterThan(errorText.FontSize * 2));
            Assert.That(errorText.ActualWidth, Is.LessThanOrEqualTo(scrollViewer.ViewportWidth));
            Assert.That(scrollViewer.ScrollableWidth, Is.Zero);
        }
    });

    /// <summary>Verifies column and header cells retain a consumer's implicit style in either display.</summary>
    /// <param name="display">The display mode to verify.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(OperationDisplay.Default)]
    [TestCase(OperationDisplay.Responsive)]
    public Task CellsRetainImplicitConsumerStyle(OperationDisplay display) => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = await StartReporting(@operator, release);
        var cellStyle = new Style(typeof(ContentControl));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(13)));
        var control = new OperatorControl {
            Operator = @operator,
            OperationDisplay = display
        };
        control.Resources.Add(typeof(ContentControl), cellStyle);
        try {
            if (display == OperationDisplay.Responsive) {
                await Layout(control, 600, 400);
            }
            await Layout(control, 1400, 400);
            var rowCells = Row(control, root).Children.OfType<ContentControl>()
                .Where(item => Grid.GetColumn(item) <= 6)
                .ToArray();
            var headerCells = HeaderGrid(control).Children.OfType<ContentControl>().ToArray();
            using (Assert.EnterMultipleScope()) {
                Assert.That(rowCells, Has.Length.EqualTo(7));
                Assert.That(headerCells, Has.Length.EqualTo(7));
                Assert.That(rowCells.Concat(headerCells).Select(item => item.Padding),
                            Is.All.EqualTo(new Thickness(13)));
                Assert.That(rowCells.Concat(headerCells).Select(item => item.Visibility),
                            Is.All.EqualTo(Visibility.Visible));
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>Verifies a consumer's implicit cell style can hide column and header cells in either display.</summary>
    /// <param name="display">The display mode to verify.</param>
    /// <returns>A task representing execution of the test.</returns>
    [TestCase(OperationDisplay.Default)]
    [TestCase(OperationDisplay.Responsive)]
    public Task CellsRetainImplicitConsumerVisibility(OperationDisplay display) => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = await StartReporting(@operator, release);
        var cellStyle = new Style(typeof(ContentControl));
        foreach (var hidden in new[] { "Data", "details" }) {
            var trigger = new Trigger { Property = ContentControl.ContentProperty, Value = hidden };
            trigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            cellStyle.Triggers.Add(trigger);
        }
        var control = new OperatorControl {
            Operator = @operator,
            OperationDisplay = display
        };
        control.Resources.Add(typeof(ContentControl), cellStyle);
        try {
            await Layout(control, 1400, 400);
            var row = Row(control, root);
            using (Assert.EnterMultipleScope()) {
                Assert.That(CellVisibility(row, 2), Is.EqualTo(Visibility.Collapsed));
                Assert.That(HeaderVisibility(control, "Data"), Is.EqualTo(Visibility.Collapsed));
                Assert.That(CellVisibility(row, 3), Is.EqualTo(Visibility.Visible));
                Assert.That(HeaderVisibility(control, "Progress"), Is.EqualTo(Visibility.Visible));
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });

    /// <summary>Verifies values displayed beneath an operation use the inline templates.</summary>
    /// <returns>A task representing execution of the test.</returns>
    [Test]
    public Task ResponsiveDisplay_InlineValuesUseInlineTemplates() => WpfTestThread.Run(async () => {
        IOperator @operator = new Operator();
        var release = Signal();
        var root = await StartReporting(@operator, release);
        var headerTemplate = new DataTemplate();
        var dataTemplate = new DataTemplate();
        var progressTemplate = new DataTemplate();
        var rateTemplate = new DataTemplate();
        var averageTemplate = new DataTemplate();
        var targetTemplate = new DataTemplate();
        var control = new OperatorControl {
            Operator = @operator,
            OperationDisplay = OperationDisplay.Responsive,
            OperationDataContentTemplate = new DataTemplate(),
            OperationInlineHeaderTemplate = headerTemplate,
            OperationInlineDataContentTemplate = dataTemplate,
            OperationInlineProgressContentTemplate = progressTemplate,
            OperationInlineProgressRateContentTemplate = rateTemplate,
            OperationInlineProgressRateAverageContentTemplate = averageTemplate,
            OperationInlineTargetContentTemplate = targetTemplate
        };
        try {
            await Layout(control, 600, 400);
            var cells = InlineFieldGrids(control, root)
                .Select(grid => grid.Children.OfType<ContentControl>().ToArray())
                .ToArray();
            using (Assert.EnterMultipleScope()) {
                Assert.That(cells.Select(cell => cell[0].ContentTemplate), Is.All.SameAs(headerTemplate));
                Assert.That(cells.Select(cell => cell[1].ContentTemplate),
                            Is.EqualTo(new[] {
                                dataTemplate, progressTemplate, rateTemplate, averageTemplate, targetTemplate
                            }));
            }
        }
        finally {
            release.TrySetResult();
            await root.Completion.WaitAsync(Timeout);
        }
    });
}

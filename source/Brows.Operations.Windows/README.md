# Brows.Operations.Windows

WPF presentation for Brows.Operations. The package provides `OperatorControl`, which displays relevant root operations and their child hierarchy, progress, errors, and root-level Cancel or Remove commands.

Assign an operator created through the Composition package to the control's `Operator` property:

```xml
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:operations="clr-namespace:Brows.Windows.Controls;assembly=Brows.Operations.Windows">
  <operations:OperatorControl x:Name="OperationView" />
</Window>
```

```csharp
// operatorFactory is resolved through Brows.Composition.
OperationView.Operator = operatorFactory.Create();
```

Set `OperationItemsTemplate` to choose the items control used for both root and child collections:

```xml
<operations:OperatorControl x:Name="OperationView">
  <operations:OperatorControl.OperationItemsTemplate>
    <DataTemplate>
      <ListBox ItemsSource="{Binding}" />
    </DataTemplate>
  </operations:OperatorControl.OperationItemsTemplate>
</operations:OperatorControl>
```

The template receives the collection source as its data context. Bind `ItemsSource` to it; the built-in operation row template remains available. A `ListView` can be used the same way. Leaving `OperationItemsTemplate` unset or setting it to null uses the default `ItemsControl`.

Set `OperationErrorTemplate` to customize how an operation's exception is displayed. The template receives the exception itself as its data context:

```xml
<operations:OperatorControl.OperationErrorTemplate>
  <DataTemplate>
    <TextBlock Text="{Binding Message}" Foreground="DarkRed" TextWrapping="Wrap" />
  </DataTemplate>
</operations:OperatorControl.OperationErrorTemplate>
```

Leaving `OperationErrorTemplate` unset or setting it to null uses the default message display.

Set `OperationDisplay` to `Responsive` to adapt the layout to the available width. The default value,
`Default`, displays every value in its own column and scrolls horizontally when the columns do not fit.

```xml
<operations:OperatorControl x:Name="OperationView" OperationDisplay="Responsive" />
```

In `Responsive` mode, the layout matches `Default` while the columns fit in the width available to a root
operation row: the operation list's viewport less any horizontal margin, padding, or border that
`OperationItemsTemplate` adds around it, and limited by any `Width` or `MaxWidth` set within it. While an
automatic vertical scrollbar is hidden, its width is still reserved. If a scroll template places the scrollbar
over the content, the overlap is reserved while it is visible as well. Each root row uses the resulting width
budget, so operations nested more deeply do not move its commands out of view. As the width decreases, the control:

1. Collapses the progress bar. The progress bar requires the width that `OperationProgressBarStyle` gives it,
   resolved from `Width`, `MinWidth`, and `MaxWidth` as WPF layout does, plus its horizontal `Margin`. Without
   an explicit width or minimum width, it requires 100 device-independent pixels.
   In `Responsive` mode the control applies the style to one hidden progress bar, without an operation data
   context, to read these values. Sizing that depends on an operation's values, such as a trigger on
   progress, is not reflected. Space required by elements inside the progress bar's control template is not
   detected either; if a custom template needs a minimum width, also set it as `MinWidth` on the style.
2. Shows the rate and average values beneath the other values.
3. Shows the progress and target values beneath the other values as well, while the data remains in its column.
4. Shows the data vertically beneath the operation name too, and hides the column headers. The name column
   then narrows as needed so that the Cancel and Remove commands remain in the viewport. Below the depth and
   command column widths (150 device-independent pixels by default), the remaining content is clipped or scrolled.

Errors are always shown beneath the operation in `Responsive` mode so that long messages can wrap. Values shown
beneath the operation use their own templates, so column templates sized for the columns do not need to suit the
vertical layout:

| Property | Template data context |
| --- | --- |
| `OperationInlineHeaderTemplate` | Header text displayed beside each value. |
| `OperationInlineDataContentTemplate` | Detail text. |
| `OperationInlineProgressContentTemplate` | Progress display string. |
| `OperationInlineProgressRateContentTemplate` | Formatted progress-rate string. |
| `OperationInlineProgressRateAverageContentTemplate` | Formatted average progress-rate string. |
| `OperationInlineTargetContentTemplate` | Target display string. |

Leaving an inline template unset or setting it to null uses wrapping text. `OperationErrorTemplate` is used for
errors in both modes.

The built-in Cancel and Remove commands capture their creating WPF dispatcher. `CanExecuteChanged`
notifications run immediately when raised on that dispatcher; notifications raised on workers are
queued asynchronously to it. The operator posts root and child collection changes asynchronously
to the context captured when it was created. Create the operator on the WPF dispatcher or pass its
context explicitly to `IOperatorFactory.Create(SynchronizationContext)`.

You can call `Operate`, report progress, and register children from worker threads. The operation
tree synchronizes state updates, while property-change notifications run on the updating thread;
WPF bindings marshal bound property updates to the dispatcher. Direct event subscribers run on the
updating thread after state locks have been released and the update's state has been committed.
Reentrant reports append notifications to the current thread's batch. Creating the operator does not
change delegate execution or normal `await` context capture. The project targets `net462`, `net48`,
`net8.0-windows`, and `net10.0-windows`, and requires WPF. See the
[repository](https://github.com/brows-app/brows-operations) for the sample project and usage guidance.

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

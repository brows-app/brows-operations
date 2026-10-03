# Brows.Operations.Windows

WPF presentation for Brows.Operations. The package provides `OperatorControl`, which displays relevant root operations and their child hierarchy, progress, errors, and root-level Cancel or Remove commands.

Assign an operator created through the Composition package to the control's `Operator` property:

```xml
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:operations="clr-namespace:Brows.Operations;assembly=Brows.Operations.Windows">
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

The built-in Cancel and Remove commands raise `CanExecuteChanged` on the control's dispatcher, because operation state changes run there. As a safeguard, a notification raised on any other thread is queued asynchronously to the dispatcher that created the command.

The project targets `net8.0-windows` and `net10.0-windows` and requires WPF. Create the operator on the control's dispatcher thread, for example by calling `IOperatorFactory.Create()` there; setting an operator created on another thread or without a synchronization context throws `InvalidOperationException`. Operation state, notifications, and collection changes then run on the dispatcher, while work can start operations and report progress from any thread. See the [repository](https://github.com/brows-app/brows-operations#threading) for the threading contract, the sample project, and usage guidance.

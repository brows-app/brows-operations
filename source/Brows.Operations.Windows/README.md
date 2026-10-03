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

The built-in Cancel and Remove commands capture their creating WPF dispatcher. `CanExecuteChanged` notifications run immediately when raised on that dispatcher; notifications raised on workers are queued asynchronously to it so bound buttons update safely. Core operation property-change notifications remain on the thread updating state.

The project targets `net8.0-windows` and `net10.0-windows` and requires WPF. Access the control and call `Operate` on the WPF dispatcher thread with its default `synchronizeWithCurrentContext: true`. Each call then captures the dispatcher synchronization context for root and child observable collection changes, including removals requested from workers. Mutations dispatch synchronously, so keep the dispatcher responsive and await worker tasks. Passing `synchronizeWithCurrentContext: false` disables collection dispatch for that root and its descendants; use it with UI binding only if you provide the required collection synchronization yourself. The option does not change delegate execution or normal `await` context capture. Operator creation does not capture a context. Progress reports and child registration may run on worker threads; property-change notifications are not dispatched by the library. Serialize state updates across the operation tree. See the [repository](https://github.com/brows-app/brows-operations) for the sample project and usage guidance.

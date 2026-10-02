# Brows.Operations

Brows.Operations tracks asynchronous work as a hierarchy. It aggregates progress from child tasks, keeps failures available for display, and includes a WPF control for showing active and failed operations.

## Packages

| Project | Purpose |
| --- | --- |
| [Brows.Operations](source/Brows.Operations/README.md) | Core operation interfaces and progress reporting. |
| [Brows.Operations.Composition](source/Brows.Operations.Composition/README.md) | Creates `IOperator` instances through Brows.Composition. |
| [Brows.Operations.Windows](source/Brows.Operations.Windows/README.md) | WPF `OperatorControl` for displaying operations. |

The [WPF sample](samples/Brows.Operations.Sample/) demonstrates nested tasks, batches, cancellation, progress updates, and failures.

## Getting started

After resolving `IOperatorFactory` through Brows.Composition, create an `IOperator` and submit work with `Operate`. Each delegate receives progress reporting and a cancellation token.

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using Brows.Operations;

void StartCopy(IOperatorFactory operatorFactory) {
    IOperator operations = operatorFactory.Create();

    operations.Operate("Copy files", async (progress, token) => {
        progress.Change(data: "Starting");
        token.ThrowIfCancellationRequested();

        await progress.Children(Enumerable.Range(1, 3), file => new OperationChild(
            $"File {file}",
            async (childProgress, childToken) => {
                childProgress.Change(setTarget: 1,
                                    name: $"Copy file {file}",
                                    data: "Copying");
                await Task.Delay(TimeSpan.FromMilliseconds(250), childToken);
                childProgress.Change(addProgress: 1, data: "Copied");
            }));
    });
}
```

`Change` supports adding or setting progress and target values, as well as updating the operation name, detail text, and display strings. Child progress and targets roll up to their parents. `Child` creates one child task; `Children` creates and awaits a set of children.

Successful root operations are removed from the collection when they finish. Failed operations remain available with their error state until removed. The WPF control displays relevant operations and their child hierarchy.

## UI scheduling

Each `Operate` call captures `SynchronizationContext.Current` and passes it to the root operation and all descendants. Root and child additions, automatic root cleanup, and explicit root removals modify their observable collections on that captured context. Changes made from another context use synchronous dispatch, so collection mutations finish before the calling API continues. Keep the context responsive and await worker tasks instead of blocking the UI thread.

For UI binding, call `Operate` on the UI synchronization context, such as the WPF dispatcher. Operator creation does not capture a context. Progress reports and child registration may run on worker threads, including after `ConfigureAwait(false)`. Property-change notifications are raised on the thread updating state and are not dispatched by the library. State updates across an operation tree must still be serialized; collection dispatch does not make progress reporting thread-safe. Enumerate live collections on their owning UI context and avoid concurrent mutations during enumeration or `RemoveComplete` selection.

If `Operate` is called without a synchronization context, observable collection changes run directly on the calling thread.

The WPF control's Cancel and Remove commands deliver `CanExecuteChanged` on their creating dispatcher. Worker notifications are queued asynchronously; notifications already on the dispatcher are delivered immediately.

After the first progress or target update, an operation becomes relevant if it is still running about one second later. Errors become relevant immediately. Report a change when work begins so long-running tasks become visible.

## Build and run

Build and test the solution on Windows with .NET 10:

```powershell
dotnet build
dotnet test
```

Run the WPF sample on Windows:

```powershell
dotnet run --project samples/Brows.Operations.Sample/Brows.Operations.Sample.csproj
```

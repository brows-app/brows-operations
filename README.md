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

## Threading

An operator is affine to the `SynchronizationContext` that is current when it is created. For a WPF app, create it on the dispatcher thread, for example by calling `IOperatorFactory.Create()` there. All operation state changes, property-change notifications, events, and root and child observable collection changes then run on that context, so bindings and handlers never observe a worker thread.

`Operate`, every `IOperationProgress` member, and cancellation can be called from any thread, including after `ConfigureAwait(false)` or inside `Task.Run`. Calls made off the context are posted to it in order:

```csharp
operations.Operate("Hash files", (progress, token) => Task.Run(() => {
    progress.Change(setTarget: 100);
    for (var file = 1; file <= 100; file++) {
        token.ThrowIfCancellationRequested();
        HashFile(file);
        progress.Change(addProgress: 1);
    }
}, token));
```

- Progress reports from workers are applied asynchronously. Consecutive pending reports for an operation are merged, so tight loops do not flood the dispatcher; the final values match applying each report in order.
- On the context, `Operate` and `Child` start their delegates immediately. From another thread, the operation is first added on the context; an `Operate` delegate then starts on the thread pool, and a `Child` delegate starts on the caller's continuation. Children requested before the parent delegate returns, even without awaiting, are still registered.
- Read `IOperation` state on the operator's context. `IOperationCollection.Snapshot()` and `Count` are safe from any thread; `Remove` and `RemoveComplete` run synchronously on the context.
- The operator must not outlive its context. Work posted after a WPF dispatcher shuts down is dropped.
- When no context is current at creation, such as in a console app or service, changes run on the calling thread under a single operator lock. Notifications are raised while that lock is held, so handlers should not block on other threads that use the operator.

`OperatorControl` requires an operator created on its dispatcher thread and throws `InvalidOperationException` otherwise.
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

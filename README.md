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

For byte-valued progress, pass `kind: OperationProgressKind.FileSize` to display values using B, KiB, MiB, and
larger binary units:

```csharp
progress.Change(setProgress: bytesCopied,
                setTarget: totalBytes,
                kind: OperationProgressKind.FileSize);
```

`progressStringFormat` and `targetStringFormat` customize the numeric portion of scaled values; counts below
1 KiB remain whole bytes. `formatProvider` selects the number-formatting culture; null leaves each existing
setting unchanged.

The WPF sample displays the current and average progress rates, in progress units per second, for each
operation. The current rate samples net progress change since the previous sample about once per second;
the average is the net change since the operation started divided by elapsed lifetime, including idle time.
Progress corrections can make either rate negative. The current rate becomes zero when the operation and
its descendants finish, while the average freezes at its final value. File-size progress uses bytes per
second. These bindable runtime values are not members of the `IOperation` interface.

Successful root operations are removed from the collection when they finish. Failed operations remain available with their error state until removed. The WPF control displays relevant operations and their child hierarchy.

## UI scheduling

`IOperatorFactory.Create()` captures `SynchronizationContext.Current` when the operator is created.
For WPF binding, create the operator on the dispatcher thread or pass the dispatcher context to
`Create(SynchronizationContext)`. Root and child membership changes take effect immediately.
Observable collection changes are posted asynchronously to the captured context. With no context, the
producer that starts a queue drain projects changes synchronously. If a concurrent or reentrant update
queues a change while a drain is active, its API can return before that projection; the event then runs
on the active drainer thread.

You can call `Operate`, report progress, and register children from worker threads. The operation tree
synchronizes its state updates. Property-change notifications run on the thread making each update;
WPF bindings marshal bound property updates to their dispatcher. Direct event subscribers run on the
updating thread after state locks have been released. Updates commit their state before notifications;
reentrant reports append their notifications to the current thread's notification batch. The operator
does not change delegate execution or normal `await` context capture.
Use `IOperationCollection.Snapshot()` to enumerate roots while other threads add or remove them. For
UI-bound child collections, enumerate their observable source on the captured dispatcher.

For example, a delegate can report progress and add a child from a worker thread:

```csharp
operations.Operate("Background work", async (progress, token) => {
    await Task.Run(async () => {
        progress.Change(data: "Working");
        await progress.Child("Child work", async (childProgress, childToken) => {
            await Task.Delay(250, childToken).ConfigureAwait(false);
            childProgress.Change(setProgress: 1, setTarget: 1);
        });
    }, token);
});
```

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

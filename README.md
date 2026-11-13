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

        var batch = await progress.Children(Enumerable.Range(1, 3), file => new OperationChild(
            $"File {file}",
            async (childProgress, childToken) => {
                childProgress.Change(setTarget: 1,
                                    name: $"Copy file {file}",
                                    data: "Copying");
                await Task.Delay(TimeSpan.FromMilliseconds(250), childToken);
                childProgress.Change(addProgress: 1, data: "Copied");
            }), OperationFailureBehavior.Record, default);

        foreach (var result in batch.Results.Where(result => result.Kind == OperationResultKind.Failed)) {
            progress.Change(data: $"A file copy failed: {result.Error.Message}");
        }
    });
}
```

`Child` and `Children` require a failure policy and an additional cancellation token. Pass `default` when
no extra token is needed; each child still inherits cancellation from its parent. Use `Throw` when the
next step depends on the current child succeeding:

```csharp
await progress.Child("Prepare", (_, token) => Task.Delay(TimeSpan.FromMilliseconds(100), token),
                     OperationFailureBehavior.Throw, default);
await progress.Child("Use prepared data", (_, token) => Task.Delay(TimeSpan.FromMilliseconds(100), token),
                     OperationFailureBehavior.Throw, default);
```

`Record` records ordinary work failures and returns their outcomes. `Throw` records the same failures
and also propagates them. A single distinct failure keeps its original exception; multiple distinct
failures are returned or thrown as an aggregate. Failures anywhere in a descendant tree affect the
child's outcome, and an ordinary failure takes precedence over child-only cancellation. Infrastructure
errors have highest precedence. Cancellation of the parent or supplied token takes precedence over the
work outcome and always propagates after started children finish; recorded work failures remain on the
operation tree. Child-only cancellation is returned under `Record` and propagated under `Throw`.
Caught child failures remain recorded in the operation tree.

An operation finishes only after its delegate task and registered descendants finish, including asynchronous
cleanup or disposal represented by the returned task. Keep ownership of staged resources until awaiting
`Child` returns a result whose `Kind` is `OperationResultKind.Succeeded`. A `Children` batch waits for every
started sibling after a failure. Returning
null from its factory skips an item, which has no result; its supplied token cancels every started batch child
and stops further registration without canceling the parent or unrelated operations.

`Change` supports adding or setting progress and target values, as well as updating the operation name, detail text, and display strings. Child progress and targets roll up to their parents. `Child` creates one child task; `Children` creates and awaits a set of children.

For byte-valued progress, pass `kind: OperationProgressKind.FileSize` to display values using B, KiB, MiB, and
larger binary units:

```csharp
progress.Change(setProgress: bytesCopied,
                setTarget: totalBytes,
                kind: OperationProgressKind.FileSize);
```

`progressFormat` and `targetFormat` customize the numeric portion of scaled values; counts below
1 KiB remain whole bytes. `formatProvider` selects the number-formatting culture; null leaves each existing
setting unchanged.

The WPF sample displays the current and average progress rates, in progress units per second, for each
operation. The current rate samples net progress change since the previous sample about once per second;
the average is the net change since the operation started divided by elapsed lifetime, including idle time.
Progress corrections can make either rate negative. The current rate becomes zero when the operation and
its descendants finish, while the average freezes at its final value. File-size rates use the same formatting
as file-size progress, automatically scaling to B, KiB, MiB, and larger binary units. Rate strings contain
the formatted value and unit without a `/s` suffix. These bindable runtime values are not members of the
`IOperation` interface.

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
        }, OperationFailureBehavior.Record, default);
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

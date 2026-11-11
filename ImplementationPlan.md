# Operation progress rates

## Goal and scope

Expose each operation's current progress rate and running average, and display both in the WPF sample.
Implement only the rate feature, its tests, sample presentation, and documentation. Do not change progress
aggregation, relevance timing, cancellation behavior, automatic removal, target frameworks, or dependencies.

This plan chooses the semantics below so implementation does not require further product decisions.

## Contract

Add these bindable, read-only properties to the internal `OperationBase` runtime object. Keep them off the
public `IOperation` interface; the sample binds to the actual operation object at runtime:

```csharp
public double ProgressRate { get; }
public double ProgressRateAverage { get; }
```

- Values are progress units per second, not percentage points per second. File-size progress uses bytes
  per second. Targets, display strings, and formatting settings do not enter the calculation.
- Start measurement when the operation starts executing, where `Operate` currently starts its stopwatch.
  The initial progress baseline is zero. An initial nonzero `setProgress` therefore counts as progress.
- Publish samples approximately once per second while running. Between samples, getters return the last
  published values. Both values begin at zero; do not compute or notify from getters.
- For a sample at elapsed time `t`, with previous sample time `previousTime`:
  `ProgressRate = (progress - previousProgress) / (t - previousTime).TotalSeconds`.
  Use actual monotonic elapsed time, including when scheduling delays the callback. This is the rate over
  the latest sampling interval, nominally one second, rather than an exact sliding one-second window.
- `ProgressRateAverage = (progress - initialProgress) / t.TotalSeconds`. This is a lifetime, time-weighted
  average, including initial delays, idle periods, and time waiting for descendants. Do not average samples
  arithmetically or use only time spent reporting progress.
- Calculate from net numeric progress. Existing progress supports decreases; corrections can produce
  negative current and average rates. Do not clamp decreases, count only positive increments, or reset
  measurement on `setProgress`. A call with both set and add contributes its resulting net change.
- A sample with no progress produces current rate zero and updates the average for elapsed idle time.
  Target-only or metadata-only reports must not reset the measurement baseline or create progress.
- At zero elapsed time, publish zero, never NaN or infinity. Ignore a repeated sample timestamp without
  consuming pending progress or moving the previous sample baseline.
- When the delegate **and all registered descendants** finish, calculate the final average at the stopped
  elapsed time and set the current rate to zero. This applies to success, failure, and cancellation.
  Include progress since the last periodic sample, even for operations lasting less than a second.
  Freeze both values afterward, including if a retained progress reporter is subsequently used.
- Measure each parent's already aggregated `Progress`, including its own direct reports and descendant
  deltas. Never sum child rates or averages: their sampling intervals and lifetimes may differ.
  As with existing progress aggregation, callers must use compatible units within a hierarchy.

Use `double` arithmetic for the division and convert operands before subtracting `long` progress values
to avoid introducing integer subtraction overflow. Retain existing progress arithmetic behavior itself.
Document floating-point approximation; tests should use tolerances.

Do not add rate members to `IOperation`, rate arguments to `IOperationProgress.Change`, configurable
sampling APIs, public clock abstractions, rate events, or public formatted-rate properties.

## Implementation sequence

### 1. Deterministic rate calculation

Introduce an internal sealed `OperationRate` in its own file under the core `Operations` directory.
Keep measurement state private. Give it narrowly scoped internal sampling and completion methods that
accept progress and elapsed `TimeSpan` explicitly, and expose the two calculated values internally.
It must implement the contract above without scheduling, synchronization, UI references, or wall-clock
reads. Let `OperationBase` own synchronization. Completion must be idempotent and prevent later samples
from changing the frozen result.

First add `tests/Brows.Operations.Tests/Operations/OperationRateTest.cs` using existing NUnit conventions.
Use exact supplied elapsed times, not real delays. Establish these examples:

| Elapsed seconds | Progress | Current rate | Average rate |
| --- | --- | --- | --- |
| 0 | 0 | 0 | 0 |
| 1 | 10 | 10 | 10 |
| 3 | 50 | 20 | 50 / 3 |
| 4 | 50 | 0 | 12.5 |
| 5, completed | 60 | 0 | 12 |

Also cover fractional progress rates, repeated timestamps, initial nonzero progress, negative corrections,
long-value extremes, completion before the first tick, zero-duration completion, and frozen results.
The unequal intervals above specifically detect an incorrect unweighted average of samples.

### 2. Connect measurement to operation lifetime

Work primarily in `source/Brows.Operations/Operations/OperationBase.cs`. Read `ChangeProgress`, `Operate`,
`Flush`, and `OperationSynchronization` together before editing. Reuse the existing stopwatch and the
tree's existing synchronization gate; do not introduce another lock or a second time origin.

- Create each operation's measurement state alongside its stopwatch before invoking its delegate. Start
  the root sampler on the first numeric progress report, even if the root delegate has not returned yet.
  Its first delay avoids immediate rate changes and no timer is needed for operations without numeric
  reports. At root finalization, close and cancel it. Await an active refresh before reading sampler errors;
  synchronously join an inactive canceled delay so short synchronous operations still complete inline and
  root completion leaves no sampler running. Rates continue to measure from the operation's original
  stopwatch start, including initial idle time.
- Add one tracked asynchronous sampling loop per **root**, which visits that root and its running
  descendants approximately once per second. Children need measurement state, not their own loops.
  Traverse the existing child collections under the shared tree gate. There is no need for a global
  scheduler, static registry, or additional collection subscriptions.
- Use `Task.Delay` with `ConfigureAwait(false)` and a separate sampler-stop cancellation source. Do not
  use the work token: a cancellation request can leave delegates or descendants running for some time.
  The loop should start by awaiting its first delay, allowing delegate execution to start as before.
- Keep the periodic refresh method internal only where needed for deterministic integration tests.
  Factor sampling so those tests can supply elapsed times through an internal seam, without adding
  public test hooks or sleeping to obtain precise numeric results.
- During a refresh, read progress and elapsed time, update both stored rates for every running operation,
  then enqueue notifications. Commit the entire refresh before subscribers execute. Use the existing
  `Synchronization.Update` and notification queue; do not invoke subscribers while holding the gate.
- Implement synchronized getters and cached `PropertyChangedEventArgs` for both new properties.
  Notify only when a numeric value changes. Periodic sampling must not raise `ProgressChanged`, trigger
  `MakeRelevant`, or change `ProgressPercent` or any progress/target strings.
- Finalize each operation's measurement inside its existing final-state transaction, after joining
  descendants and stopping its stopwatch, before publishing `Complete` and `Completed`. Ensure the
  current rate becomes zero and the final average is visible to completion subscribers.
- At root finalization, signal sampler shutdown and observe/join its task **outside** the state gate.
  Guard refreshes against completed operations so a queued refresh cannot overwrite final values.
  Dispose sampler resources on every exit, including synchronous completion and unexpected failures.
  Root completion must not leave a pending sampler retaining the operation tree.
- Track the sampling task rather than using `async void` or a raw thread-pool timer callback. A throwing
  property subscriber must not become an unobserved task fault or an unhandled timer exception. Observe
  sampler faults through the existing completion-task/root-observer path, after required descendant
  joining and final-state/resource cleanup. Keep observer exceptions distinct from delegate errors;
  consult `NotificationFailureTests.cs` and preserve its existing behavior.

Do not change delegate scheduling, capture a dispatcher for sampling, or marshal ordinary property
notifications to the collection context. Rate notifications may originate on the sampling worker;
existing WPF property bindings handle dispatcher delivery.

### 3. Integration coverage

Add `OperationBaseTest.cs` for operation integration, reusing the existing signal, child-enumeration, and
bounded-wait patterns. Inspect `OperationSynchronizationTest.cs`, `SynchronizationTests.cs`, and
`NotificationFailureTests.cs` for suitable helpers and existing behavior to preserve.

- Verify values through `OperationBase` references cast from real items in `IOperationCollection.Snapshot()`;
  keep the public `IOperation` contract focused on completion state.
- Use controlled sampling to cover direct set/add reports, set plus add, target-only and metadata-only
  changes, idle intervals, and a child correction propagated to its parent.
- Cover a parent with direct progress plus concurrent children and a grandchild. Confirm descendant
  progress counts once and the parent's denominator starts at its own start, not a child's start.
- Verify measurement continues while the parent delegate has returned but a descendant remains active,
  including a failed parent and cancellation requested before work actually finishes.
- Verify sampling starts during a long synchronous root delegate and while joining descendants after a
  synchronous parent failure. Confirm numeric short synchronous operations still complete synchronously.
- Cover success, failure, cancellation, synchronous completion, and completion between ticks. Final
  rates must be frozen before completion notifications and remain frozen under a late refresh/report.
- Verify both values are committed before notification, subscribers run outside state locks, and
  reentrant reports do not corrupt sampling or deadlock. Add a throwing-subscriber case proving sampler
  fault observation and cleanup without bypassing finalization.
- Add one bounded real-scheduler test proving a running operation receives a rate notification and then
  a zero-rate idle sample without any additional report. Signal on notifications; do not assert exact
  wall-clock rates. Use deterministic tests for shutdown/late-refresh races, not an arbitrary sleep
  hoping to observe no more callbacks. Always release test delegates in `finally`.

Run the new focused tests before broader validation. Confirm they detect missing/incorrect behavior,
rather than merely failing because an API has not yet been declared or the build environment is missing.

### 4. Display both rates in the sample

Keep presentation changes in `samples/Brows.Operations.Sample`; no Windows package API changes are needed.
The library row's Data content is a string, but its containing `ContentControl.DataContext` is the operation.

- Extend `DataCellTemplate` in `Resources/OperationsTheme.xaml` to retain the detail text and add visible
  lines labeled `Rate` and `Average`. Within that template, bind through the ancestor `ContentControl`'s
  `DataContext.ProgressRate` and `DataContext.ProgressRateAverage`. Do not bind these paths directly to
  the string-valued cell content, or replace the existing hierarchy template.
- Display numeric rates with at most two fractional digits and explicit `units/s`. Use the same ancestor's
  `DataContext.ProgressKind` in a data trigger to select `B/s` for file-size operations. Raw fractional
  bytes per second are sufficient here; retain existing progress/target binary-size formatting unchanged.
  Sample rate text may use the sample binding culture; it does not inherit progress-string formats.
- Keep the existing bounded detail column and scrolling. Use tooltips for full text and verify the extra
  row height, long values, negative corrections, error rows, child rows, and narrow window layout.
- Adjust the existing simulated file-copy scenario in `OperationsSampleWindow.xaml.cs` to include a
  short, cancellable idle phase lasting several sampling intervals between two progress phases. Retain
  its byte target/kind and long-enough execution time. It should visibly demonstrate current rate
  falling to zero while the lifetime average decreases, then both changing when work resumes.
- Update the scenario's visible description in `OperationsSampleWindow.xaml` and the sample README to
  explain the labels, idle phase, units, and zero-current/frozen-average completion behavior.

Exercise the actual sample style at runtime. Existing Windows tests do not automatically cover a changed
sample resource dictionary. If adding an automated binding test, load the actual sample dictionary and
verify worker-originated updates reach both rendered labels; testing a duplicate template is insufficient.

### 5. Documentation and validation

Update the root README and core README with the new read-only properties, sampling cadence, signed net
progress semantics, lifetime average, unit interpretation, and completion behavior. Explain that numeric
rates are sampled even when reports stop. Use the repository's XML documentation format for new public
members. Do not promise an exact sliding window or refresh on every `Change` call.

The solution is `brows-operations.slnx`; `global.json` selects .NET SDK 10.0.100 with feature roll-forward.
Core targets are `net462`, `net48`, `net8.0`, and `net10.0`. Windows projects target the two .NET Framework
versions plus `net8.0-windows` and `net10.0-windows`; the sample targets `net10.0-windows`.
Use Windows with the required .NET Framework targeting packs and .NET runtimes. Stay compatible with
.NET Framework APIs: do not introduce `PeriodicTimer`, `TimeProvider`, or `Stopwatch.GetElapsedTime`.

From the repository root, run focused tests first, rebuilding the changed code:

```powershell
dotnet restore
dotnet test tests/Brows.Operations.Tests/Brows.Operations.Tests.csproj `
  --no-restore --configuration Release --framework net10.0 `
  --filter "FullyQualifiedName~OperationRateTest|FullyQualifiedName~OperationBaseTest"
dotnet test tests/Brows.Operations.Tests/Brows.Operations.Tests.csproj --no-restore --configuration Release
dotnet build --no-restore --configuration Release
dotnet test --no-build --no-restore --configuration Release
```

Build and test all solution frameworks because this changes shared hierarchy behavior.
Pack only the three package-producing source
projects, using `dotnet pack <project> --no-build --no-restore --configuration Release` for each. Do not
publish packages. Run the sample with `dotnet run --project samples/Brows.Operations.Sample` and inspect
the copy/idle scenario, nested operations, cancellation, and retained failure results.

Before finishing, review `git diff --check`, the full diff, and new/untracked files. Confirm the public API
delta is limited to the two properties, no generated output is included, and unrelated work is preserved.
Report changed behavior, checks and results, and any unavailable framework/runtime or UI validation.

## Acceptance criteria

- Consumers can read current and average rates through `IOperation`; values follow the stated equations.
- Idle operations refresh without reports, and hierarchy progress is counted exactly once.
- Completion freezes a correct final average and zero current rate, with no surviving root sampler.
- Deterministic tests cover arithmetic and lifecycle races; existing synchronization tests still pass.
- The sample visibly displays both labeled rates for roots and children and demonstrates an idle interval.

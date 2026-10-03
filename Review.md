# Code review

Re-reviewed on October 1, 2026. This covers all production C# and XAML, the sample, existing and newly added tests, READMEs, project configuration, packaging, and the CI workflow.

The review assumes creation, progress reporting, cancellation, and continuations run sequentially on a Windows UI synchronization context. PropertyChanged subscribers are expected not to throw, and the logging library is assumed never to throw. Every reproduced defect below occurs within that model; none requires concurrent access from multiple threads.

**Result:** Findings **1–15 are fixed** for the supported UI scenarios. Finding **17 is accepted** under the user's notification policy: unexpected listener exceptions should surface during development. Tests that deliberately throw from PropertyChanged have been removed.

**Severity:** High means execution, cancellation, or lifetime tracking can fail materially. Medium means a conditional correctness issue or an unmet integration requirement. Suggestions without an established failure are listed separately.

## Recheck of previous findings

### 1. Fixed — The control's collection bindings resolve

**Locations:** [OperatorControl.xaml](source/Brows.Operations.Windows/Operations/OperatorControl.xaml), [OperatorControl.xaml.cs](source/Brows.Operations.Windows/Operations/OperatorControl.xaml.cs).

The control exposes its built-in collection through a read-only dependency property. The root items and relevance trigger bind to that property. WPF dispatcher tests confirm an active root binding containing a failed operation, visible panel, live removal, operator replacement, and collapse when the operator is cleared or empty.

The control still supports the built-in `OperationCollection` specifically. A different `IOperator.Operations` implementation is cast to null and displays nothing. This is an abstraction limitation, not a regression of this fix.

### 2. Fixed — Missing data contexts do not make the converter throw

**Location:** [OperationCommandConverter.cs](source/Brows.Operations.Windows/Operations/OperationCommandConverter.cs).

Null and unsupported operation values return `DependencyProperty.UnsetValue`. Tests verify both cases and materialize actual operation rows on an STA dispatcher without an unhandled exception. Invalid command parameters for a valid operation still deliberately throw `ArgumentException`; `ConvertBack` remains unsupported.

### 3. Fixed — Synchronous completion is observed

**Locations:** [OperationManager.cs](source/Brows.Operations/Operations/OperationManager.cs), [OperationBase.cs](source/Brows.Operations/Operations/OperationBase.cs), `Start`.

The manager subscribes and adds the root before starting it; parents register children before starting them. Tests confirm immediate root success and immediate parent/child success leave no retained root, while immediate failure remains complete and in error. Ordinary asynchronous completion is also covered.

### 4. Fixed — Delegate failure or cancellation does not bypass the child join

**Location:** [OperationBase.cs](source/Brows.Operations/Operations/OperationBase.cs), `Operate`.

The delegate outcome is recorded separately from the child wait. Tests confirm a failed parent remains incomplete while its child runs. A canceled parent waits for a child that ignores cancellation and retains that child's late error. Enumeration and factory failures in `Children` also wait for children already started. Delegate failure does not implicitly cancel descendants.

### 5. Fixed — Each package contains its declared README

**Locations:** [source/Directory.Build.props](source/Directory.Build.props), the README in each source project.

The props explicitly pack `$(MSBuildProjectDirectory)\README.md` at the package root. This now selects each project's own README. The repository and package READMEs contain usage and UI scheduling guidance.

The Release CI build and pack commands succeed. All three generated `.nupkg` files contain `README.md`, its contents exactly match the respective project file, and their nuspec metadata declares that README. Sample and test projects are not packable.

### 6. Fixed — Child registration closes when the delegate returns

**Location:** [OperationBase.cs](source/Brows.Operations/Operations/OperationBase.cs), `Child` and the delegate `finally`.

Registration closes before enumerating children for the final join. New tests retain a progress wrapper and attempt registration while an existing child is still running, after either a successful or failed delegate return. Both attempts throw `InvalidOperationException`, execute no new child delegate, and leave only the registered child in the tree. Registration after full completion was already covered.

### 7. Fixed — Cancellation blocks ordinary subsequent child work

**Location:** [OperationBase.cs](source/Brows.Operations/Operations/OperationBase.cs), `Child`, `Operate`, and `Cancel`.

Tests confirm existing descendants receive cancellation, registration after `Cancel` returns throws `OperationCanceledException`, and cancellation between child registration and `Start` skips the child's delegate. Linked token sources remain alive through the child join.

The #8 follow-up also verifies that registration from a synchronous cancellation notification is rejected before a new child delegate can run.

### 8. Fixed — Cancellation propagates through token callback failures and reentrant notifications

**Locations:** [OperationBase.cs](source/Brows.Operations/Operations/OperationBase.cs), `Cancel`; [OperationCommandConverter.cs](source/Brows.Operations.Windows/Operations/OperationCommandConverter.cs), `CancelCommand.Execute`.

`Cancel` marks the request before publishing state, so registration from a reentrant cancellation notification is rejected. Repeated or reentrant cancellation requests return immediately. It attempts each snapshotted child and its own token source, collecting cancellation-token callback aggregates and throwing one flattened `AggregateException`. The WPF command catches that aggregate.

State and capability events now use normal invocation. Their exceptions are allowed to propagate under the accepted notification policy; the former state-notification guards and tests that throw from PropertyChanged have been removed.

**Coverage:** Core cases retain throwing token callbacks, sibling and parent cancellation, reentrant cancellation, and exactly-once callbacks despite repeated requests. The WPF case retains containment of token callback aggregates while work is canceled and completes without an operation error.

### 9. Fixed — Error messages have the correct visibility

**Location:** [OperatorControl.xaml](source/Brows.Operations.Windows/Operations/OperatorControl.xaml), error and progress controls.

Dispatcher tests confirm an operation with its own error shows the exception message and collapses its progress control. A parent whose error is only in a child has `Error == null`: its own error control is collapsed and its progress control remains visible. The child's message is displayed in its own row.

### 10. Fixed — Ancestor progress strings remain current

**Location:** [OperationBase.cs](source/Brows.Operations/Operations/OperationBase.cs), string getters and progress notifications.

Default strings format current values instead of caching numeric text. Tests verify descendant reports notify the parent with current `ProgressString` and `TargetString`, and actual WPF text bindings refresh to those values. Existing tests cover explicit strings and clearing them back to numeric defaults.

### 11. Fixed — Relevant descendants surface their ancestor path

**Location:** [OperationBase.cs](source/Brows.Operations/Operations/OperationBase.cs), `Relevant` setter.

Relevance propagates to each ancestor and the collection. Core tests cover immediate descendant failure and timed relevance of a still-running numeric descendant. A WPF test materializes a failed leaf under an otherwise successful middle/root path and verifies all three rows are visible. Relevance remains sticky.

### 12. Fixed — Count changes notify bindings

**Location:** [OperationCollection.cs](source/Brows.Operations/Operations/OperationCollection.cs), `Add` and `Remove`.

Tests verify count notifications after addition and successful removal, with none after null, repeated, or running-operation removal attempts. A WPF `Count` binding changes from `1` to `0` after removal. The public interface still does not require `INotifyPropertyChanged` of alternative implementations.

### 13. Fixed — Reentrant numeric reports preserve ancestor totals

**Location:** [OperationBase.cs](source/Brows.Operations/Operations/OperationBase.cs), `ChangeProgress` and `Flush`.

The shared change context commits the whole numeric hierarchy before delivering notifications. New tests reproduce ancestor-to-leaf reentrancy through three levels. Progress and target are each tested with a nested set and a nested addition. The first ancestor notification sees every level already at `1`; the nested report leaves every level at `2` for set or `3` for addition. Existing root-level reentrancy coverage also passes.

## Additional findings

### 14. Fixed — Rejected removal requests preserve later command removal

**Locations:** [Operation.cs](source/Brows.Operations/Operations/Operation.cs), `CanRemove`; [OperationManager.cs](source/Brows.Operations/Operations/OperationManager.cs), `Remove`; [OperationBase.cs](source/Brows.Operations/Operations/OperationBase.cs), finalization.

Previously, `CanRemove` became true when `Progressing` became false, before the collection's required `Complete` state. A removal attempt from that notification was rejected, but the manager detached its removal handler anyway. Later commands could no longer remove the failed root.

`CanRemove` now uses `Complete`, and its event and property notifications are raised when `Complete` changes. The manager detaches its `Removed` handler only after the collection confirms successful removal. Rejected requests during execution or finalization preserve the handler.

**Verification:** The former explicit regression now passes in normal runs. Additional core tests verify rejected requests both while running and from the `Progressing == false` notification, followed by successful removal after completion. Capability event/property notifications see both `Complete` and `CanRemove` true, for successful and failed operations. WPF tests verify the Remove command enables at completion, survives execution while disabled, and can remove reentrantly from its eligibility notification.

### 15. Fixed — The public collection source prevents external mutation

**Location:** [OperationCollection.cs](source/Brows.Operations/Operations/OperationCollection.cs), `Source`.

`IOperationCollection.Source` now lazily creates and caches a `ReadOnlyObservableCollection<Operation>` around the owned collection. External callers can enumerate its live contents and observe changes, but cannot mutate it through `IList` or generic collection interfaces. Owner-controlled addition and removal retain validation, relevance accounting, and subscriptions.

**Verification:** The former explicit read-only-source regression is enabled and passes. Existing collection enumeration, add/remove notification, count notification, and relevance tests pass. WPF tests also confirm root items, live removal, count binding, visibility, and operator replacement continue to work with the wrapper.

The internal child source remains mutable; that separate hardening suggestion is retained under Areas to improve.

### 17. Accepted — Unexpected notification failures surface during development

**Location:** [OperationBase.cs](source/Brows.Operations/Operations/OperationBase.cs), `Operate`, notification setters, and `ProgressWrapper.Child`; [Operation.cs](source/Brows.Operations/Operations/Operation.cs), `OnPropertyChanged`.

The user rejected broad notification exception containment. PropertyChanged subscribers are expected not to throw in the supported WPF usage. `UpdateState`, `NotifyEvent`, and `NotifyObservers` have been removed. State, relevance, capability, and completion events use ordinary invocation. Logging methods are called directly under the logging library's guarantee that they do not throw.

Delegate failures are still recorded as operation errors. Registration closes before publishing a delegate error, and lifetime `finally` paths retain child joining and cancellation-source disposal. The source field is cleared before disposal and final state notifications. Unexpected child completion-task faults propagate, and an async root observer surfaces unexpected root faults through the UI synchronization context.

Notification exceptions are not converted into operation errors or suppressed to continue publishing state. A throwing listener may prevent later listeners or later state updates; that behavior is accepted for development diagnostics. Completion-listener tests now expect propagation after the result is finalized. Tests that deliberately throw from PropertyChanged have been removed.

## Areas to improve

### Public contracts and documentation

The READMEs explain UI scheduling and include examples. Public APIs now have XML documentation covering child-registration lifetime, error/cancellation outcomes, removal policy, and `Change` ordering and units. Public test fixtures and sample declarations are documented as well; internal and private implementations remain undocumented.

Successfully awaiting `Child` can represent failed work, and `Children` returns whether any children were started rather than whether they succeeded. Tests and the XML comments document these semantics. The comments also explain that explicit progress/target strings are cleared by any later `Change` using their null defaults, including metadata-only changes. Consider adding these details to the READMEs. Validate `OperationChild.Task` earlier if null delegates should be rejected at construction; the documentation currently describes validation when the child is started.

The public operation API exposes only completion and aggregate error flags. Cancellation has no separate outcome, unknown targets have no indeterminate presentation, and command/display state requires internal implementations. Consider exposing the state needed by supported consumers and documenting the built-in-only WPF integration.

`OperationBaseCollection.Source` still exposes its mutable child collection to internal consumers. Consider a read-only observable wrapper there as well, so internal callers cannot bypass child registration and lifetime tracking.

### Large operations and scheduling

Child history, delegates, and completion tasks remain retained until the root tree is released. Error roots retain their entire tree. The default recursive nonvirtualized `ItemsControl` instances materialize containers even for rows that will be collapsed. Consumers can now supply an `OperationItemsTemplate` containing another items control for root and child collection sources; virtualization depends on that control and its layout. Consider reducing retained successful history and virtualizing relevant rows. These are structural scaling concerns; no performance benchmark was performed.

`Children` eagerly enumerates and starts all work before its first await. Enumeration, factories, and synchronous delegate prefixes can block the UI, while an unbounded amount of asynchronous I/O can be outstanding. Consider bounded scheduling, yielding, and stopping enumeration on cancellation. Enumeration/factory failure now joins already-started children, as verified under #4.

### Subscription ownership

Direct public collection removal does not detach the operation's `Removed` handler owned by its manager. Retaining a removed `IOperation` consequently retains that manager and its collection, including other roots. Consolidate successful-removal cleanup so command removal and public collection removal have the same subscription lifecycle. This is inferred from the event/reference structure; no GC or memory benchmark was performed.

### SDK selection

[global.json](global.json) declares SDK version `10.0.0`. The installed host reports it as invalid and explains that SDK feature bands begin at `100`. Builds here selected `10.0.112` and succeeded; a build failure is not established. Use a valid intended baseline, such as `10.0.100`, with the desired roll-forward policy.

## Test coverage and validation

The current suite has **75 core cases** and **23 WPF cases**, with no explicit defect cases remaining. Token-cancellation, reentrant-notification, #14, and #15 regressions remain enabled. PropertyChanged exception-containment cases have been removed. Tests start core work through `IOperator`; internal state is inspected or cancellation/removal invoked where public interfaces do not expose the behavior. The factory remains untested as previously requested.

The new WPF project is included in the solution. Dedicated STA threads and real dispatchers cover collection bindings, relevance/row visibility, error presentation, numeric ancestor text, count binding, operator replacement, actual Cancel/Remove buttons, converter edge cases, command eligibility/events, and containment of throwing token callbacks. Both test helpers have bounded execution to avoid hanging the process.

Tests use NUnit fluent constraints. Tests that check several values use individual assertions inside `Assert.EnterMultipleScope`, including captured state and notification sequences; tuple equality assertions have been replaced. Parameterized cases cover set/add reentrancy, delegate outcomes, batch construction failures, and command parameters. No coverage percentage is claimed: coverage was assessed against branches and observable scenarios in the source. Performance, external Brows integration, factory composition, and execution outside the UI context assumption remain outside this verification.

**Commands and results:**

- `dotnet build brows-operations.slnx --no-restore --configuration Release -p:ContinuousIntegrationBuild=true`: succeeded, zero errors during the original review. Missing public XML documentation has since been addressed. A subsequent solution build with `GenerateDocumentationFile=true` and XML documentation diagnostics treated as errors succeeds; only repository remote/SourceLink metadata warnings remain.
- Last test execution before the notification-policy change: `dotnet test brows-operations.slnx --no-restore --configuration Release` passed **100 core and 29 WPF cases**. The revised **75 core and 23 WPF cases** compile successfully; they were not rerun as part of this policy change. The empty Composition project remains without tests.
- `dotnet pack brows-operations.slnx --no-restore --configuration Release --no-build --output out/review-validation-pack -p:ContinuousIntegrationBuild=true`: succeeded. ZIP contents and nuspec metadata were checked for all three project-specific READMEs.
- WPF restore/build/test/pack required access to local Windows SDK metadata denied by the default sandbox. Rerunning with that access succeeded; the initial environment restriction is not a repository defect.

Completion-listener propagation and delegate-error publication coverage is in [NotificationFailureTests.cs](tests/Brows.Operations.Tests/Operations/NotificationFailureTests.cs), [RegressionTests.cs](tests/Brows.Operations.Tests/Operations/RegressionTests.cs), and [OperatorControlTests.cs](tests/Brows.Operations.Windows.Tests/Operations/OperatorControlTests.cs).

## WPF threading follow-up, October 3, 2026

### 19. Resolved — State notifications invoke subscribers while holding the state lock

**Fix:** `agent/19/notifications-outside-lock` batches notifications on the updating thread and publishes
them after the outermost state update releases its locks. Nested and reentrant reports retain FIFO
notification delivery. Collection relevance commits in the state transaction; collection projection
drains remain scheduled even when a listener throws. The original defect is recorded below.

**Locations on the reviewed branch:**
`source/Brows.Operations/Operations/OperationSynchronization.cs:52–63`;
`source/Brows.Operations/Operations/OperationBase.cs:460–467`, `:511–513`, and `:557–570`.
Numeric notifications at `OperationBase.cs:178–191` and `:250–263`, and collection property
notifications in `OperationCollection.cs:63–68` and `:93–100`, have the same locking pattern.

`ProgressWrapper.Change` holds the shared state gate while calling setters that synchronously invoke
`PropertyChanged` subscribers. Scalar getters, including the public `IOperation.Complete`, acquire
that same gate. A direct subscriber that synchronously marshals to the WPF dispatcher and reads
operation state therefore causes a lock cycle:

1. The worker holds the state gate and raises `PropertyChanged`.
2. The subscriber calls `Dispatcher.Invoke` and waits for its UI callback.
3. The UI callback reads `IOperation.Complete` and waits for the worker's state gate.
4. The worker cannot release the gate until the subscriber returns.

The dispatcher and reporting worker remain blocked. The gate is shared across the operator, so other
operation getters and updates can also stall. This does not require an exception, concurrent
enumeration, or a UI thread blocking on an operation task. It affects direct event subscribers that
marshal synchronously; ordinary WPF scalar bindings already marshal asynchronously and do not
require that subscriber pattern.

**Reproduction:** A focused harness used a real STA WPF dispatcher, a worker calling
`progress.Change(name: "changed")`, and a `PropertyChanged` subscriber posting a dispatcher callback
that reads `((IOperation)root).Complete`. The handler waited for that callback with a bounded timeout.
The callback entered the dispatcher but could not finish its getter until the handler timed out and
released the worker's gate. An unbounded synchronous dispatcher invocation would deadlock.

**Resolution:** Commit state changes and collect the required notifications while holding the gate,
then invoke external subscribers after releasing it. Account for nested setters and reentrant updates
so an inner method does not publish while an outer update still holds the gate. Preserve numeric
hierarchy consistency, notification ordering, reentrant reports, and exception propagation. Apply the
same rule to state/capability events and collection property notifications. Add a bounded real-WPF
regression showing that a worker notification handler can synchronously dispatch a public state read
and finish without a lock cycle.

## Fix validation for issue 19

- The new WPF Name-notification regression failed against the original code because the dispatcher
  getter waited for the worker's state lock, then passed after the fix.
- All 21 WPF notification cases passed on every target framework, covering metadata, numeric values,
  cancellation, completion, relevance, capability events, and collection property notifications.
- Full solution restore and Release build passed with zero warnings and zero errors.
- Full solution tests passed: 101 core cases and 48 WPF cases per target framework, 596 executions total.
- Core coverage includes nested gates, reentrant FIFO delivery, concurrent delivery on updater threads,
  listener exception propagation, collection scheduling after exceptions, and committed finalization state.
- Composition test assemblies contain no discoverable tests. No public API members were added.

Issue 19 is resolved in `F:/dev/me/agent-brows-operations-19`. Issue 18 remains open; issue 20 is resolved below.

### 20. Resolved — The no-context observable notification contract does not match concurrent execution

**Original finding:** `IOperationCollection` and `IOperationProgress` promised that observable collection
changes without a captured synchronization context ran on the calling thread. `CollectionChangeQueue`
serializes its projection through one active drain. If another producer enqueues during that drain, its
core membership update is immediate and its API can return before the observable source changes; the
active producer later raises that event on its own thread. Captured-context delivery remains owned by
that context.

**Resolution:** Public XML and README guidance now distinguishes immediate core membership from the
serialized observable projection. It documents synchronous draining by the producer that starts a
no-context drain, delayed projection for concurrent or reentrant enqueue operations, and event delivery
on the active drainer thread. The synchronous child-delegate prefix guarantee and updater-thread scalar
property notifications remain documented separately. No production behavior or public API changed.

**Verification:** `Operate_WithoutContext_OverlappingProducersSerializeCollectionProjection` uses two
dedicated producers and explicit signals. It holds the first Add handler while the second `Operate`
returns, then checks that both roots are in the core snapshot while only the first is projected. After
the hold is released, the second Add arrives on the first producer's thread and follows the first Add in
FIFO order. The previous sequential-only test was renamed to identify its narrower scope. Since issue 20
is a documentation mismatch, the focused test asserts the existing queue behavior; it is not expected
to fail against the unchanged implementation. The original focused reproduction showed caller thread 8
and notification thread 9 for the overlapping producer.

**Validation:** The focused regression passed 1/1 on each of `net462`, `net48`, `net8.0`, and `net10.0`.
The full core test project passed 102/102 on each target framework. The core Release build succeeded on
all four frameworks with zero warnings and zero errors. `git diff --check` passed.

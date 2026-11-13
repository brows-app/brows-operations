# Explicit child outcomes and failure policies

## Summary and public contract

Replace the legacy `Child` and `Children` methods on `IOperationProgress`. This is an accepted breaking change:
do not retain compatibility overloads or introduce extension methods.

Add these public types in `Brows.Operations`, each in its own file:

```csharp
public enum OperationFailureBehavior {
    Record,
    Throw
}

public enum OperationResultKind {
    Succeeded,
    Failed,
    Canceled
}

public sealed record OperationResult(
    OperationResultKind Kind,
    Exception Error);
```

Replace the interface methods with:

```csharp
Task<OperationResult> Child(
    string name,
    OperationDelegate task,
    OperationFailureBehavior failureBehavior,
    CancellationToken cancellationToken);

Task<OperationBatchResult> Children<T>(
    IEnumerable<T> source,
    Func<T, OperationChild> child,
    OperationFailureBehavior failureBehavior,
    CancellationToken cancellationToken);
```

Both `failureBehavior` and `cancellationToken` are required. Callers may explicitly pass `default` when they have no
additional cancellation token. Reject undefined policy values with `ArgumentOutOfRangeException` before registering
work or enumerating a batch.

Add a public sealed `OperationBatchResult` class with:

- `IReadOnlyList<OperationResult> Results`
- `int SucceededCount`
- `int FailedCount`
- `int CanceledCount`

Its public constructor accepts `IEnumerable<OperationResult> results`. Reject a null sequence with
`ArgumentNullException`, and null entries or undefined outcomes with `ArgumentException`. Store a defensive copy
behind a read-only wrapper, not a mutable array. Properties are get-only and counts derive from that snapshot.
Results follow source order, excluding items whose factory returned null. An empty batch returns an empty result
with zero counts. No overall batch outcome or batch exception property is needed.

OperationResult.Error permits null without enabling nullable reference types project-wide:

- Succeeded: null.
- Failed: the single original exception or an aggregate of distinct failures.
- Canceled: the original cancellation exception when available; otherwise a synthesized OperationCanceledException
  carrying the canceled operation token.

Supply an internal IsExternalInit compatibility shim for net462 and net48; do not change framework targets or
dependencies.

Keep the positional `OperationResult` above without additional constructor validation; document its invariants as
guarantees of package-produced results. Use the namespace `System.Runtime.CompilerServices` for the internal shim.
Validate null delegates, sources, and factories before registration/enumeration. Names retain their current permissive
behavior. Validate each factory-produced delegate before registering its child.

## Outcome, failure, and cancellation rules

Apply policy only after the delegate’s returned task, registered descendants, and operation finalization have finished.

| Condition | Record | Throw |
| --- | --- | --- |
| Infrastructure failure | Throw | Throw |
| Parent or supplied token canceled | Throw OperationCanceledException | Throw OperationCanceledException |
| Ordinary work failure in the subtree | Return Failed | Throw failure |
| Child-only cancellation in the subtree | Return Canceled | Throw OperationCanceledException |
| Successful subtree | Return Succeeded | Return Succeeded |

The table establishes precedence. Record all ordinary failures even when cancellation or infrastructure errors determine
what the caller receives.

Cancellation classification:

- A delegate's `OperationCanceledException` represents cancellation when the delegate's effective linked token is
  canceled, or when its own token is canceled and is known to the operation tree (an operation token or a token
  supplied to `Child`/`Children`), when the exception is observed. Otherwise it is an ordinary failure. Timeouts and
  other cancellation of tokens the library never received are failures; only cancellation requested through tokens
  the library knows produces a canceled outcome.
- Apply this rule to both roots and children. A root can therefore recognize cancellation propagated from a descendant’s
  canceled token.
- Cancellation requested before finalization prevents a success result even if the delegate ignores cancellation and
  returns normally.
- Failure takes precedence over child-only cancellation. Cancellation alone must not populate the operation’s ordinary
  error state.
- Retain exception classification internally; later token cancellation cannot reclassify an earlier work failure.
  Freeze terminal cancellation state after joining descendants and before publishing `Complete` or disposing sources.
  Use that same snapshot for results and tree completion flags. Do not recheck child tokens after public awaits or
  rewrite completed outcomes when tokens are canceled later, including during completion notifications.

For ambient cancellation, prefer the canceled parent token, otherwise the canceled supplied token. Propagate an
`OperationCanceledException` carrying that token; reuse an original cancellation exception only if its token matches.
This lets an enclosing operation recognize cancellation even if its own token is not canceled. For cancellation-only
subtrees, choose the node's own cancellation first, then the first canceled descendant in registration order.
Retain token values separately from source ownership and preserve the existing source-disposal cancellation leases.

Outcome describes work, not `Task.Status`: propagating an unsolicited `OperationCanceledException` as an ordinary
failure can still produce a canceled async task under .NET semantics. Keep the existing root regression for an
unsolicited exception with neither token canceled; add root coverage for a canceled exception token unknown to the
tree, which is recorded as an error.

Link the parent operation token and supplied token into the child’s operation token before invoking its delegate.
Descendants inherit that linkage. A supplied child token cancels that subtree without canceling its parent or siblings.
Keep the linked source alive until the complete subtree finishes.

For batches, parent cancellation and the batch’s supplied token are ambient cancellation. They stop further registration
and propagate after all started children finish. Individual canceled children remain individual outcomes in Record mode.

For Throw batches, wait for every started child without canceling siblings because another child failed. After joining,
ordinary failures take precedence over individual cancellations; if only individual cancellations remain, propagate the
first cancellation in source order.

## Implementation changes

### Separate completion from outcome

Refactor OperationBase and its private ProgressWrapper so that internal completion continues to mean “the operation
lifetime ended,” with infrastructure faults remaining exceptional. Do not make ordinary Throw-policy failures fault the
shared internal completion task.

Store immutable terminal subtree information internally:

- Captured ordinary failures.
- Cancellation status and cancellation exception.
- Infrastructure failures needed for exceptional completion.

Apply Record or Throw at the public Child/Children boundary. Root operations retain their existing reporting and removal
model; they do not gain a failure-policy argument.

Preserve synchronous delegate startup through its first asynchronous wait and existing synchronization-context behavior.

### Preserve and combine failures

Capture exceptions with ExceptionDispatchInfo where they first enter the operation machinery. Use it to rethrow single
failures, preserving exception identity and original stack frames.

Build subtree summaries from the node’s own failure and finalized descendant summaries. Include descendants registered
without awaiting their public child task.

Expand only aggregates created by this library, using retained provenance metadata. An `AggregateException` explicitly
thrown by delegate code is one original failure: preserve its identity and stack instead of blindly calling `Flatten()`.
When a returned delegate task has multiple faults, inspect its `Exception.InnerExceptions` after awaiting it so errors
hidden by await's single-exception propagation are retained. Those entries are separate original failures; an explicit
aggregate stored as one entry stays intact. Include synchronous delegate invocation errors as well.

Deduplicate by reference identity, including exceptions forwarded through multiple ancestors. Do not deduplicate by
message, type, or equality. Use a comparer compatible with all target frameworks. Order node-local failures first,
then child subtrees in registration order. A forwarded library aggregate contributes its original entries rather
than becoming an additional failure. Share the aggregation and policy helpers between single and batch APIs.

For a single distinct ordinary failure, return and throw that exact exception. For multiple failures, construct an
AggregateException containing each distinct failure once.

Keep existing node-level Error reporting and descendant-based CompleteWithError. A caller catching a propagated child
failure does not erase that failure from the tree or make its ancestor’s subtree successful.

### Keep infrastructure failures exceptional

Distinguish failures originating in registration, progress notifications, collection publication, completion
notifications, token-source management, and finalization from ordinary delegate failures.

Track exception provenance by reference in a synchronized, tree-scoped registry shared by operations. Mark machinery
exceptions at public API boundaries before propagating them, and register the entries of library-created aggregates.
Also retain original work-failure/cancellation classifications for forwarded exceptions, so ancestor handlers do not
reclassify them against later token state. Classify only newly observed exceptions using the current cancellation rule.
Do not wrap single exceptions, modify `Exception.Data`, or use a process-wide strong-reference registry. Recognize
marked exceptions escaping an enclosing delegate so its error handler does not convert them into Record results.
If consumer code handles a progress or registration exception, its registry entry alone must not fault completion.
An actual registered child's infrastructure completion fault still propagates through the internal descendant join.

Validation errors and batch enumeration/factory failures are exceptional under either policy. A token-backed
cancellation exception from enumeration/factory code follows the cancellation rule instead of being automatically
classified as infrastructure. Other construction errors retain infrastructure precedence over ambient cancellation.

Ensure cleanup and descendant joining cannot be skipped by notification failures. Capture multiple infrastructure
failures and aggregate distinct exceptions; preserve the original exception when there is only one. Infrastructure
exceptions take precedence over ordinary work errors, which remain recorded.

Fix registration ownership around observable notifications: once a child is committed to the authoritative collection,
it must be started and joined even if publication throws. An uncommitted child must not start. Neither path may leave an
incomplete child stranded in the tree.

Record commitment explicitly inside the existing synchronization gate. Retain publication errors after `Core.Add`,
start that committed child exactly once, and await it before propagating. Preserve notification-before-startup
ordering and reentrant cancellation that skips delegate invocation. Do not roll back committed observable membership.

Use guarded finalization phases: close registration, join children, stop the root sampler, freeze terminal work state,
release sources under existing leases, publish final state, and notify completion. A phase failure must not bypass
essential cleanup or completion-source settlement. Aggregate distinct infrastructure faults rather than allowing a
later finally block to mask an earlier exception. Keep subscriber invocation fail-fast within each notification;
do not redesign event dispatch. Recognize and retain infrastructure faults separately from ordinary work failures
when inspecting a delegate task with multiple faults.

Preserve asynchronous collection notification delivery. Errors raised later on the captured context retain that delivery
path; the new API does not wait for the UI notification queue to drain.

External `Cancel()` callback failures retain their existing caller-facing propagation. Do not promise to attach these
out-of-band errors to a completed child task. Root infrastructure failures retain the existing root observation path.

### Implement batch coordination

Replace the LINQ-based batch implementation with explicit coordination:

1. Validate arguments and policy before enumeration; reject already canceled ambient tokens without enumerating.
2. Enumerate once, check cancellation between enumeration steps, before factories, and before registration. Invoke each
   reached factory once, skip null descriptions, and start children in source order.
3. Collect terminal summaries independently of the selected public Throw policy.
4. On enumeration, factory, registration, or enumerator-disposal failure, stop scheduling and retain the exception.
   Dispose the enumerator and preserve both body and disposal errors when both occur.
5. Await all committed children and descendants, including children whose publication failed, before propagating errors.
6. Apply infrastructure, ambient cancellation, work-failure, and individual-cancellation precedence.

Inspect every child completion failure after joining; do not rely on the single exception surfaced by awaiting
Task.WhenAll.

Ordinary child failure, including a synchronous failure under Throw, must not stop scheduling subsequent siblings.
Do not implement batches by awaiting each public Throw call sequentially. Enumeration remains synchronous and
cooperative: the library cannot interrupt a blocking factory or `MoveNext`. Completed child entries keep their frozen
outcomes even when the batch subsequently encounters ambient cancellation.

Registration must reject canceled parents based on effective token state as well as existing cancellation flags,
with validation and authoritative membership commitment under the same existing synchronization gate.
Preserve the rule that registration closes when the parent delegate finishes.

## Migration and documentation

Update every repository call site, including core tests, WPF tests, samples, tools, and README examples.

- Use explicit Record where existing behavior intentionally continues after ordinary child failures.
- Replace Boolean batch assertions with result-list and count assertions.
- Update cancellation assertions to the new propagation contract.
- Update construction-error tests: errors now surface after the batch joins started children, and escaping machinery
  failures fault internal completion instead of being recorded as root work failures. Observe expected root faults
  through the existing test context helper so the async-void root observer does not fail tests unexpectedly.
- Demonstrate Throw for dependent sequential work and Record with outcome inspection for independent transfers.
- Update `IOperationProgress`, `OperationDelegate`, `OperationChild`, and result-type XML documentation, including
  method references after signature changes. Update `IOperator` cancellation remarks for canceled exception tokens.
- Document that completion includes asynchronous cleanup and disposal represented by the delegate’s returned task.
  Callers must retain staging ownership until awaiting Child establishes Succeeded.
- Document that caught failures remain recorded in the tree, batches wait for started siblings, and skipped items
  produce no result.

Do not add archive-specific code, sibling-cancellation options, package-version changes, or release-workflow changes.

## Test plan and acceptance criteria

Use the existing NUnit framework and synchronization helpers. Add focused cases to OperationBaseTest and preserve the
existing regression coverage. Use explicit signals with bounded waits.

Give the batch result its own `OperationBatchResultTest` fixture. Release blocked delegates in test cleanup even if
an assertion fails. Keep new helpers compatible with every target framework.

Cover these independent behaviors:

- Success, synchronous throw, and asynchronous failure under both policies.
- Original exception identity and original throw-site stack frames.
- Failure from awaited and unawaited descendants, including a descendant using Record beneath an ancestor using Throw.
- Multiple distinct failures and repeated propagation of single or aggregated failures without duplicate entries.
- Delegate completion delayed by asynchronous cleanup; cleanup failure prevents success.
- Parent and supplied cancellation before startup, during execution, and while joining noncooperative descendants.
- Canceled operation token with a tokenless cancellation exception; canceled unknown exception token with an
  uncanceled operation token resulting in failure; neither token canceled resulting in failure.
- Individual cancellation under both policies, mixed failure/cancellation precedence, and ambient cancellation taking
  precedence while retaining recorded failures.
- Supplied cancellation reaching grandchildren without canceling siblings or the parent.
- Empty batches, skipped entries, mixed outcomes, source-order results, counts, and defensive result-list immutability.
- Batch Throw waiting for a slow sibling before throwing; all distinct work failures retained.
- Enumeration, factory, and registration failures waiting for started children.
- Invalid arguments and enum values creating no work.
- Progress and completion listener failures propagating under both policies, including through an enclosing delegate.
- A registration listener throwing after collection commitment without leaving a stranded child.
- Existing cancellation callback leases, reentrancy, collection scheduling, rates, and root removal behavior remaining
  correct.

Also cover user-created aggregates retaining identity, multi-fault delegate tasks, frozen cancellation classifications,
ambient exception tokens, synchronous batch failure not stopping sibling creation, enumerator disposal failure, and
multiple infrastructure errors. Verify a handled progress exception does not poison successful work. Test registration
publication errors racing cancellation, constructor validation, and the root's canceled-exception-token rule directly.

Introduce the API/types and migrate callers to restore compilation, then add focused failing behavior tests before
each execution change where feasible. Missing-API compilation failures are not behavioral red/green evidence.

Run focused core tests on net10.0, then the complete core suite on net462, net48, net8.0, and net10.0. Finish with the
repository’s documented Release restore, build, and test sequence for the full solution, covering WPF targets and the
sample, then pack only the three source package projects.

Use the SDK selected by `global.json` and repository test-runner guidance. Report unavailable runtimes or targeting
packs explicitly; do not alter framework targets to bypass validation limitations.

Before completion, review tracked and new files, run the whitespace check, and confirm public API changes match this
plan. Report behavior changes, test results, and validation limitations. No implementation is part of this plan review.

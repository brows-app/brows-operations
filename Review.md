# Implementation review

Scope: implementation of `Plan.md` on branch `suggestions`, including local changes and new files.
Baseline: `72f56d691c9bda69e6b7a492f64176166991ec6c` (initial clean checkout).
Date: 2026-11-12 (America/Chicago).
Comparison: baseline to final working tree, including new files and documentation.

## Summary

All 13 confirmed findings are resolved (2 P1 and 11 P2); there are no open findings. Core and Windows tests pass on
every target framework. Full solution Release build/test and all three source package checks pass. Original problem
descriptions and issue numbers are retained below.

## Findings

### #1 [P2] Known exception provenance must not suppress another node's failure [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationBase.cs:186`.

The initial implementation returns immediately for a known work exception or library work aggregate. The registry is
tree-wide, so two independent siblings throwing the same exception instance can make the second sibling successful.
Provenance identifies classification and original dispatch information, not membership in the current subtree.

Resolution: retain known failures in each affected node, expand known library aggregates into that node's failures,
and deduplicate references when forming terminal summaries. Verify independent sibling reuse as well as ancestor
propagation and library aggregate forwarding.

### #2 [P2] Handled boundary errors must not poison the parent lifetime [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationBase.cs:707`.

The initial boundary handler records infrastructure or cancellation directly on the parent before throwing the API
error. A consumer catching an invalid registration error therefore still faults or cancels the parent. The plan
requires provenance marking without a terminal fault when a boundary error is handled by consumer code.

Resolution: mark boundary provenance without recording a terminal parent error; classify it when it actually escapes
the delegate. Preserve propagation of a registered child's actual infrastructure completion fault through the join.
Verify handled and unhandled validation/progress failures separately.

### #3 [P1] Child publication errors must reach the public child task [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationBase.cs:308`.

The initial path records a publication error on the parent and then creates a result from the child's summary.
Successful child work can therefore return `Succeeded` from Record even though registration publication failed.

Resolution: after joining the committed child, propagate publication failures through the public task in both modes,
combining distinct child infrastructure faults without replacing them. Verify publication failure with successful,
failed, and canceled child work and ensure every committed lifetime settles.

### #4 [P1] Progress listener errors lack infrastructure provenance [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationBase.cs:1510`.

At milestone 1, Change directly invokes Synchronization.Update without marking an escaping listener exception.
A child delegate calling Change can therefore return a Record failure result instead of propagating infrastructure
failure. A catch inside the update callback would also miss notification drains, which run in Update's finally.

Resolution: catch outside the complete update call, mark infrastructure provenance, and rethrow the original error.
Verify listener errors under both policies, propagation through enclosing delegates, and handling by consumer code.

### #5 [P2] Batch cancellation classification precedes known infrastructure errors [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationBase.cs:546`.

At milestone 1, a token-backed OperationCanceledException is classified before checking its infrastructure provenance.
A factory invoking progress machinery whose listener throws that exception can therefore be treated as cancellation
instead of an infrastructure fault. Infrastructure must retain precedence even when ambient cancellation occurs.

Resolution: expand/check known infrastructure before classifying cancellation, and preserve existing classifications.
Verify a factory progress listener throwing a token-backed cancellation exception alongside ambient cancellation.

### #6 [P2] Batch ambient cancellation depends on a disposable parent source [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationBase.cs:521`.

At milestone 1, every cancellation check retrieves the parent's current TokenSource. An unawaited batch can resume
after its parent has joined the batch children and disposed that source. If cancellation came through a linked token
rather than Cancel(), the cancellation flag is false and the token is lost. Record can return canceled child results
instead of propagating ambient cancellation. This is a source-traced scheduling race, not yet an executed reproduction.

Resolution: capture the effective parent token at batch entry and retain it independently of source disposal. Verify
an unawaited batch whose continuation is delayed until after its canceled parent finishes.

### #7 [P2] A reused work exception must still be exceptional when thrown by machinery [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationTreeState.cs:89`.

The initial registry retained its first classification forever. Reusing an ordinary work exception as a progress
listener error therefore prevented infrastructure marking and allowed Record to swallow the listener failure.

Resolution: infrastructure marking overrides earlier work/cancellation provenance while preserving existing
infrastructure aggregate metadata and dispatch information. Completed node summaries remain immutable. Verify reuse
of the same exception across independent work and notification origins.

### #8 [P2] Close registration before publishing a delegate failure [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationBase.cs:1169`.

An intermediate rewrite recorded delegate errors before closing registration. An Error subscriber could register new
work after the delegate had finished, contrary to the existing registration contract and its regression test.

Resolution: close registration in the delegate-await finally before classifying/publishing captured errors. Preserve
`NotificationFailureTests.DelegateFailure_ClosesRegistrationBeforePublishingItsError` across all target frameworks.

### #9 [P2] Record must not swallow construction errors with existing work provenance [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationBase.cs:546`.

An intermediate correction accumulated known work exceptions from factories as policy-controlled failures. Record
could then return a normal batch result after a factory/enumeration/disposal failure, with no child result representing
that failure. Construction errors must remain exceptional regardless of earlier work provenance.

Resolution: keep construction infrastructure separate from actual child work summaries; known work provenance can
prevent reclassification as cancellation but cannot make construction failure return normally. Verify a factory
rethrowing a previously observed work exception under Record.

### #10 [P2] Controlled test callbacks must execute outside the queue lock [Resolved]

Status: Resolved.

Location: `tests/Brows.Operations.Tests/Operations/ControlledSynchronizationContext.cs:12`.

RunNewest holds Gate while calling RunAt. RunAt releases only its own nested acquisition before invoking the callback,
so the outer lock remains held. A callback waiting for another thread to post can deadlock that Post operation.

Resolution: choose and remove the queued item under the gate, then run the callback after all gate acquisitions have
been released. Verify the delayed-continuation test and retain bounded waits and cleanup.

### #11 [P2] Expand known infrastructure aggregates at every recording boundary [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationBase.cs:127`.

An intermediate implementation stored known library infrastructure aggregates as opaque errors in guarded phases.
A parent completion listener rethrowing its child's infrastructure aggregate then produced a summary containing both
the wrapper and the original leaves, counting propagated errors again.

Resolution: expand retained library infrastructure aggregate entries when recording infrastructure faults, while
preserving caller-created aggregates as original exceptions. Verify nested completion-listener propagation contains
only the two original distinct infrastructure errors.

### #12 [P2] Required notification failures must not replace earlier errors [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationSynchronization.cs:22`.

The existing machinery can lose notification error A when its required-only drain throws B. An update-body error can
also be replaced by a finally-block drain failure. This pre-existing behavior falls within the plan's infrastructure
error-preservation guarantee. A new direct regression reproduced the first path: only B reached the caller.

Resolution: retain both dispatch records in an internal infrastructure aggregate, preserving single-exception identity
and caller-created aggregate identity. Register the internal aggregate's leaves in tree provenance. Verify both
notification/required-drain and update-body/finally-drain combinations.

### #13 [P2] Startup notification errors must not strand a cancellation lease [Resolved]

Status: Resolved.

Location: `source/Brows.Operations/Operations/OperationBase.cs:982`.

The existing pattern increments the cancellation lease inside Update but assigns its return value only after
notification delivery. Canceling a child from its registration notification, then throwing from its Progressing
notification, skips the matching cancellation/release call. Completion can leave a phantom lease and pending disposal.
The rewrite retained this pre-existing path, contrary to the plan's cancellation-source ownership guarantee.

Resolution: track acquired ownership independently of Update's return, then guarantee cancellation/release even if
startup notification delivery throws. Verify completion, zero outstanding leases, and no pending source disposal.

## Milestone-2 correction evidence

The corrected paths below were reviewed against source and the complete 209/209 core net10.0 run.

| Finding | Primary regression evidence |
| --- | --- |
| #1 | `Children_RecordMarksIndependentSiblingsFailedForReusedException` |
| #2 | `Child_RejectsUndefinedFailureBehaviorBeforeRegistration` |
| #3 | `RegistrationPublicationFailure_JoinsCommittedChildBeforePropagating` |
| #4 | `ProgressListenerFailure_PropagatesUnderBothPolicies` |
| #5 | `BatchFactory_ProgressListenerCancellationRemainsInfrastructure` |
| #6 | `UnawaitedBatch_DelayedContinuationRetainsCanceledParentToken` |
| #7 | `ReusedWorkException_ProgressListenerOriginRemainsInfrastructure` |
| #8 | `DelegateFailure_ClosesRegistrationBeforePublishingItsError` |
| #9 | `Children_FactoryReusingWorkExceptionRemainsInfrastructure` |
| #10 | `UnawaitedBatch_DelayedContinuationRetainsCanceledParentToken` and callback-outside-lock source review |
| #11 | `NestedCompletionFailures_AggregateDistinctInfrastructureLeaves` |
| #12 | `ListenerAndRequiredCollectionFailures_AggregateBothOriginalExceptions` |
| #13 | `OperationCancellation_StartupProgressListenerFailureReleasesTokenLease` |

Additional evidence covers handled validation/progress errors, batch publication joins, both notification masking
sites, user aggregate identity, and cancellation callback/source-disposal ownership. Findings #12 and #13 had direct
failing reproductions before the fixes and passed afterward. No original behavioral baseline was established for the
breaking API migration; its initial compile collision is not counted as behavioral evidence.

## Validation and coverage

- Reviewed the complete task diff, new files, public API surface, documentation, sample, and relevant callers/tests.
- Core tests: net462 208/208, net48 208/208, net8.0 208/208, net10.0 209/209.
- Full solution Release restore/build/test passed. Windows tests passed 52/52 on each of net462, net48,
  net8.0-windows, and net10.0-windows: 1,041 executed tests across core and Windows targets in the solution run.
- Composition test assemblies contain no tests on their four frameworks; those projects built successfully.
- All three source projects packed successfully, each producing nupkg and snupkg files in out/package/release.
  Core was rebuilt/repacked after the final XML-documentation-only edit; behavior tests were not repeated for comments.
- NuGet vulnerability-feed queries could not complete because feeds were unreachable (NU1900). Restore/build/test/pack
  still succeeded using available packages. VSTest used its private temporary artifact directory.
- Whitespace, encoding, line endings, required-token signatures, legacy-call removal, and task-only file scope checked.
- Only one GPT-6-Luna implementation subagent was used with Max reasoning; the parent reviewed milestones and owns
  the final commit. No dependency, target-framework, release-workflow, or generated-output changes belong to the commit.

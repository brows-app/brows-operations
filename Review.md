# Branch review against dev

Reviewed `dev...HEAD`, including the core rate lifecycle, arithmetic, sample bindings, documentation,
and added tests. No existing review findings were present. The initial findings were reproduced with
an isolated .NET 10 console probe under ignored `out/review-probe/`; production code was not changed
during that review.

A follow-up review of the current branch against `dev` retained resolved findings 1-3 and found
issues 4-5 below. Both new findings were reproduced in isolated .NET 10 processes under ignored
`out/review-pass2/`. Issues 4 and 5 have since been resolved.

The latest review of `dev...HEAD` retained resolved findings 1-5 and found test reliability issue 6,
which is now resolved with an internal switch for tests that supply synthetic times. No additional
production-code defect was identified. The Release solution build passed before this fix with zero
warnings or errors. The full core suite passed after the fix: 144 tests on `net462`, `net48`, and
`net8.0`, and 145 on `net10.0`. The timing race was reproduced before the fix under ignored
`out/review-pass3/`. The sample UI was not rerun during this review.

A further review after the issue 6 fix retained resolved findings 1-6 and found issue 7 below. The
Release solution build passed with zero warnings or errors before this test-only fix. The rate/lifecycle
fixtures passed 20 tests each on `net462`, `net48`, and `net8.0`, and 21 on `net10.0`. An isolated,
signal-controlled probe under ignored `out/review-pass4/` reproduced the missed-event sequence. Issue 7
has since been resolved by registering both listeners before operation startup and releasing the
reentrant test delegate in cleanup. The `OperationBaseTest` fixture passed after the fix: 11 tests on
`net462`, `net48`, and `net8.0`, and 12 on `net10.0`. Production code was not changed, and the sample UI
was not rerun during this review.

A review after commit `b8cb5b6` found that issue 7 was only partially resolved: four other real-sampler
tests retained the same missed-event race. A signal-controlled probe under ignored `out/review-pass5/`
reproduced the failure using the current test helper itself. Issue 7 has since been fully resolved by
registering every affected listener before operation startup and releasing the listener-failure test's
delegate during cleanup. The `OperationBaseTest` fixture passed after the fix: 11 tests on `net462`,
`net48`, and `net8.0`, and 12 on `net10.0`. Full solution tests passed before this test-only fix: 577 core
and 192 WPF test executions across all target frameworks; the composition projects contain no tests.
No additional production-code defect was identified. The sample bindings were inspected; the UI was not
rerun.

A review after commit `0493fa2` confirmed the subscription fixes for issue 7 and found issue 8 below.
The Release solution build passed with zero warnings or errors. The rate/lifecycle fixtures passed 20
tests each on `net462`, `net48`, and `net8.0`, and 21 on `net10.0`. A signal-controlled probe under
ignored `out/review-pass6/` reproduced the remaining assertion race. No additional production-code
defect was identified. The sample bindings were inspected; the sample UI was not rerun.

Issue 8 has since been resolved by gating the child report until its rate observer is registered and
asserting the captured child sample. The `OperationBaseTest` fixture passed after the fix on all four
target frameworks.

## 1. [Resolved] [P2] Sampler startup requires the root delegate to return normally

Location: `source/Brows.Operations/Operations/OperationBase.cs:207-219` and root delegate invocation at
lines 423-425.

`RateSamplerMayStart` is enabled only after `Task(progress, token)` returns. Numeric reports during a
long synchronous delegate (or the synchronous prefix of an async delegate) only set
`RateSamplingRequested`. Consequently, such an operation can make progress for seconds or minutes
while both rate properties stay zero. Running `Operate` on a worker thread makes this visible to UI
consumers even though the delegate itself has not returned a task yet.

There is also a permanent version of this failure: if the root starts a child and then throws
synchronously, the assignment is skipped entirely. The root still joins its running descendants, but
their reports cannot start the sampler, so the entire tree has no live rates for the remaining work.

Reproductions: a synchronous delegate reported progress 10 and blocked before returning its task;
after 1.3 seconds its average was zero and `RateSamplerTask` was null. A parent that registered a
child reporting progress 10 and then threw synchronously produced the same result while joining that
child.

Suggested fix: allow numeric reports to arrange sampling while the root delegate is still executing,
and ensure the descendant-joining path can start sampling after synchronous failure. Preserve short
synchronous completion without making live sampling depend on normal delegate return. Add regression
coverage for both scenarios.

Resolution: the first numeric report now starts the root sampler immediately, including during
synchronous delegate execution and while the root joins active descendants. Completion closes the
sampler; an inactive canceled delay is joined synchronously, keeping short synchronous operations inline
and leaving no sampler running. Added coverage for both reported failures and synchronous completion.

## 2. [Resolved] [P2] Reentrant completion can silently lose a rate-observer exception

Location: `source/Brows.Operations/Operations/OperationBase.cs:229-259`, with error capture at line 205
and the completion-time error check at lines 500-501.

When completion is triggered inline from a rate notification, `StopRateSampler` recognizes the sampler
thread and skips joining its task. The operation can then finish and check `RateSamplerError` before
the notification has returned. If that subscriber subsequently throws, `SampleRates` catches the
exception and stores it after the root's completion task has already succeeded. Nothing observes or
propagates the stored exception afterward. This also lets completion precede the active sampler's
actual exit, contrary to the intended shutdown contract.

Reproduction: the delegate awaited a default `TaskCompletionSource` using `ConfigureAwait(false)`.
A `ProgressRate` subscriber completed that source, allowing the delegate and root finalization to run
inline, then threw `InvalidOperationException`. The root's completion task was `RanToCompletion`
while `RateSamplerError` contained that exception.

Suggested fix: defer reentrant finalization past the active callback and then join the sampler before
settling completion and reading its error. Avoid both a synchronous self-join and the current unjoined
success path. Add a test where a rate observer releases work and then throws.

Resolution: `StopRateSampler` now awaits the sampler task when completion is reentrant, allowing the
active notification to unwind before completion checks the captured error. The .NET Framework path
keeps synchronous joining only when the sampler is idle and the caller is on a different thread.
`SamplerListenerFailure_ReentrantCompletionWaitsForCallbackBeforeFaulting` covers the release-then-throw
case.

## 3. [Resolved] [P3] Small progress changes disappear at large accumulated totals

Location: `source/Brows.Operations/Operations/OperationRate.cs:7-8,11-12,33-34,49`.

The helper stores the previous progress as `double` and converts the new total to `double` before
subtracting. Above `2^53`, distinct adjacent `long` values can round to the same floating-point value.
This makes an exactly representable small rate become zero, or makes successive small rates jump
between zero and a larger value. The long-extreme test covers overflow but does not detect this loss
of the interval delta.

Reproduction: sample progress `9007199254740992` at one second, then `9007199254740993` at two seconds.
The second interval reports zero units per second instead of one.

Suggested fix: retain progress baselines as `long` and calculate their difference without overflow or
premature rounding, for example with `decimal` subtraction followed by conversion of the delta to
`double` for division. Add tests for small positive and negative changes above `2^53`, while retaining
the signed-long-extreme coverage.

Resolution: progress baselines now remain `long`, and `ProgressDifference` subtracts in `decimal`
before converting the exact delta to `double`. `SampleAndComplete_PreserveSmallSignedDeltasAboveDoublePrecision`
covers positive and negative deltas for sampled and completed averages. The existing signed-long-extreme
test remains in place.

## 4. [Resolved] [P2] Idle sampler shutdown can deadlock a bounded thread pool

Location: `source/Brows.Operations/Operations/OperationBase.cs:221-245`; the original synchronous
`task.GetAwaiter().GetResult()` was at line 238.

Canceling the idle `Task.Delay` can queue the `SampleRates` continuation to the thread pool. Shutdown
then blocks its caller until that continuation completes. If the caller occupies the last available
pool worker, the continuation cannot run and the operation never completes. A short synchronous
delegate that reports progress now takes this path because its first numeric report starts the sampler.
This also adds blocking under pool saturation even when the pool can eventually inject more workers.

Reproduction: in an isolated process, set both minimum and maximum worker counts to one, then use
`Task.Run` to call `Operate` with a delegate that reports progress 1 and returns `Task.CompletedTask`.
After three seconds, `Operate` has not returned, `Complete` is false, `RateSamplerRefreshing` is false,
the sampler task is `WaitingForActivation`, and its cancellation source reports cancellation requested.
The only worker is blocked joining the sampler whose continuation needs that worker.

Resolution: `StopRateSampler` now awaits only when a refresh is active. Since the root closes sampling
before shutdown, an idle sampler can observe cancellation or closure and exit without a synchronous
join. The `Sampler_ShutdownDoesNotBlockABoundedThreadPool` regression runs the original one-worker
scenario in a child process with a bounded watchdog, so a failure cannot hang the test host. The
active-refresh join required by finding 2 remains intact.

## 5. [Resolved] [P2] A throwing progress listener can bypass sampler startup after progress commits

Location: `source/Brows.Operations/Operations/OperationBase.cs:744-802`; originally the sampler request
followed `Synchronization.Update` at lines 801-802.

The numeric update commits inside `Synchronization.Update`, which drains external notifications before
returning. If a `PropertyChanged` or `ProgressChanged` listener throws, control never reaches
`RequestRateSampler`. A delegate can catch that listener exception and continue running, but both rate
properties then stay zero indefinitely unless another numeric report successfully returns. The committed
progress should still initiate sampling; startup currently depends on unrelated listeners succeeding.

Reproduction: attach a listener that throws on `PropertyChanged("Progress")`, then let the delegate call
`Change(setProgress: 10)` inside a try/catch and await a release signal. After 1.3 seconds, the operation
is still running with progress 10, average rate zero, and `RateSamplerTask` null. Releasing it produces
only the final average.

Resolution: `ProgressWrapper.Change` now requests the root sampler inside the synchronized update,
after the numeric change commits and before queued notifications drain. The
`Sampler_ReportsProgressAfterProgressListenerFailure` regression verifies that a delegate can catch
the listener exception, continue without another numeric report, and still receive rate samples.

## 6. [Resolved] [P3] Synthetic-time integration tests race the live sampler

Location: `tests/Brows.Operations.Tests/Operations/OperationBaseTest.cs:81-84`, `108-119`, and `172`.

These tests start real operations that report numeric progress, which also starts the periodic sampler,
then call `RefreshRates` with fixed elapsed times and assert exact rates. They therefore depend on
reaching the synthetic sample before the first real tick. If a debugger pause or slow test worker delays
that call beyond one second, the real sampler advances the measurement timestamp past the supplied
one-second timestamp. The helper correctly ignores the earlier synthetic sample, leaving rates based
on real elapsed time. The numeric assertions fail; the outside-state-lock notification test can instead
time out because its expected `(4, 4)` notification never occurs.

Reproduction: start the same progress-10 operation, wait for its first real rate notification, then
invoke `RefreshRates` with the test's one-second timestamp. The expected average is 10 within 0.000001,
but the retained real sample produced 9.893786247122641. This models a scheduling pause before line 81
and demonstrates that these integration tests still depend on wall-clock timing.

Resolution: an internal `DisableRateSampling` seam lets the fixed-time integration tests suppress the
periodic loop before each operation starts. Real-scheduler tests continue exercising automatic sampling.

## 7. [Resolved] [P3] Real-sampler tests can miss their only positive-rate notification

Location: `tests/Brows.Operations.Tests/Operations/OperationBaseTest.cs:27-42`, `184-227`, and `335-425`.

Originally, `SamplerListenerFailure_ReentrantCompletionWaitsForCallbackBeforeFaulting` and
`Sampler_ReportsProgressAndThenAnIdleRateWithoutAnotherChange` attached their rate listeners after the
operation has reported its sole progress increment and started sampling. If the test thread is paused
or descheduled across the first tick, that positive-rate notification is delivered before subscription.
The next tick sets the rate to zero, and later idle ticks never make it positive again. Consequently,
the positive-rate signal never completes and these tests time out despite correct library behavior.
The reentrant-completion test also never releases its waiting delegate on this failure path.

Reproduction: use an early observer to await the first positive tick, then attach the same positive/idle
observer used by the idle-rate test. At subscription, the current rate was 0.9847216497159965. The next
idle tick was observed, while the new observer's positive-rate signal remained incomplete. This models
a scheduling pause between operation startup and listener registration without an arbitrary sleep.

Resolution: all rate observers now start waiting through the existing `beforeStart` callback, before
their delegates report progress. This covers the three `WaitForPositiveRate` callers and the separate
listener-failure test. The listener-failure test now releases its delegate in `finally` and drains
operation completion before asserting, so a missed signal cannot leave its operation running.

## 8. [Resolved] [P3] Descendant sampler test asserts a transient child rate after awaiting the parent

Location: `tests/Brows.Operations.Tests/Operations/OperationBaseTest.cs:428-433`.

`Sampler_ReportsDescendantProgressAfterSynchronousParentFailure` saves the parent's positive rate in
an early signal, but asserts the child's live `ProgressRate` after awaiting that signal. If the test
continuation is delayed until the next sampler tick, the child has correctly entered its idle interval
and its current rate is zero. The saved parent sample still passes, but the child assertion fails despite
correct production behavior. Early subscription in issue 7 prevents losing the parent event; it does
not preserve the child's transient value for this later assertion.

Reproduction: use the same synchronous-parent-failure scenario with one child progress report and an
unreleased child delegate. Capture the parent's positive rate with the current `WaitForPositiveRate`
helper and observe the child's positive and subsequent idle notifications before evaluating the
assertion. The captured parent rate was 9.662811365978497 and the captured child rate was
9.67364413720091, but the child's current rate was zero and the parent was still incomplete. This models
a delayed test continuation using explicit notifications, without arbitrary sleeps.

Resolution: the child now waits on a reporting gate until the test has found it and subscribed to its
positive-rate signal. The assertion checks that captured sample instead of the later live property. Both
the reporting gate and child delegate are released in `finally` before operation completion is awaited.

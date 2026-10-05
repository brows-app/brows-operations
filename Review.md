# Code review

Fresh review completed October 5, 2026. This review independently inspected the production projects,
sample, tests, project configuration, and WPF resources. Findings are ordered by severity.

## Findings

### 1. Resolved — Public removal retains the operation manager and the entire collection

**Locations:** [OperationManager.cs](source/Brows.Operations/Operations/OperationManager.cs:48) and
[OperationCollection.cs](source/Brows.Operations/Operations/OperationCollection.cs:92).

Each root subscribes its `Removed` event to an `OperationManager`. The manager removes that handler only
when its own `Remove` method succeeds. `IOperationCollection.Remove` and `RemoveComplete` call
`OperationCollection.Remove` directly, bypassing the manager, so they leave the subscription on a
removed operation. If an application keeps that removed operation to inspect its failure, the operation
keeps its manager alive; the manager keeps the collection alive, including every other root it contains.
Repeatedly clearing failed operations can therefore retain completed and active operation trees that the
application no longer displays.

**Fix:** `OperationCollection.Remove` now raises an internal collection-removal notification after it
commits a successful removal. `OperationManager` uses that notification to detach both its command and
collection-removal handlers, so `Remove` and `RemoveComplete` release the manager from the retained root.

**Verification:** `CollectionRemoval_DetachesTheManagerSubscription` first failed against the unfixed
code for both removal paths, each retaining an `OperationManager` through the root's `Removed` event. It
now passes and confirms neither path leaves that event targeting a manager.

## Validation

- `dotnet restore brows-operations.slnx` completed successfully after granting access to local Windows
  SDK metadata required by the multi-targeted projects.
- `dotnet test tests/Brows.Operations.Tests/Brows.Operations.Tests.csproj --configuration Release
  --no-restore` passed: 107 tests on each of `net462`, `net48`, `net8.0`, and `net10.0`.
- `dotnet test tests/Brows.Operations.Windows.Tests/Brows.Operations.Windows.Tests.csproj --configuration
  Release --no-restore` passed: 48 tests on each of `net462`, `net48`, `net8.0-windows`, and
  `net10.0-windows`.

## Independent assessment of the current changes

The code changes fix a real conditional retention issue: retaining a completed root after public
collection removal previously retained its manager through `Removed`, and therefore retained the
collection and its remaining roots. Cleanup now runs for every successful collection removal, including
`RemoveComplete`, rather than only manager-initiated removal. The new parameterized regression checks
both public paths. No new production-code defects were identified in the current diff.

This assessment does not independently confirm the earlier test results above. The focused `net10.0`
regression test command with Release configuration and `--no-restore` could not build because sandbox
access to `C:\Users\ken\AppData\Local\Microsoft SDKs` was denied (MSB4184). Broader build/test validation
was not run because of that environment restriction. `git diff --check` passed.
